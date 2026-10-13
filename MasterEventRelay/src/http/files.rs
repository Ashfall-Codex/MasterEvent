
use std::net::SocketAddr;
use std::path::{Path as FsPath, PathBuf};

use axum::{
    body::Body,
    extract::{ConnectInfo, DefaultBodyLimit, Path, State},
    http::{header, HeaderMap, StatusCode},
    response::{IntoResponse, Response},
    routing::{post, put},
    Json, Router,
};
use futures_util::StreamExt;
use serde::Deserialize;
use serde_json::{json, Value};
use sha1::{Digest as _, Sha1};
use sha2::Sha256;
use tokio::io::AsyncWriteExt;
use tokio_util::io::ReaderStream;
use tracing::{info, warn};

use crate::state::AppState;
use crate::ws::extract_client_ip;

const LEADER_TOKEN_HEADER: &str = "x-leader-token";
const ROOM_TOKEN_HEADER: &str = "x-room-token";
const MAX_CHECK_HASHES: usize = 2048;
const ORPHAN_GRACE_SECS: u64 = 30 * 60;

type ApiError = (StatusCode, Json<Value>);

fn err(status: StatusCode, message: &str) -> ApiError {
    (status, Json(json!({ "error": message })))
}

fn is_hash(value: &str) -> bool {
    value.len() == 40 && value.bytes().all(|b| b.is_ascii_hexdigit())
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{:02x}", b)).collect()
}

fn token_hash(token: &str) -> [u8; 32] {
    Sha256::digest(token.as_bytes()).into()
}

pub fn room_dir(base: &str, room_key: &str) -> PathBuf {
    let digest = Sha256::digest(room_key.as_bytes());
    FsPath::new(base).join(hex(&digest[..16]))
}

#[derive(PartialEq, Eq)]
enum Access {
    Leader,
    Member,
}

fn authorize(state: &AppState, room_key: &str, headers: &HeaderMap) -> Result<Access, ApiError> {
    let room = state
        .rooms
        .get(room_key)
        .ok_or_else(|| err(StatusCode::NOT_FOUND, "Room not found"))?;

    let header_str = |name: &str| {
        headers
            .get(name)
            .and_then(|v| v.to_str().ok())
            .filter(|s| !s.is_empty())
    };

    if let (Some(stored), Some(token)) = (room.leader_token_hash, header_str(LEADER_TOKEN_HEADER)) {
        if stored == token_hash(token) {
            return Ok(Access::Leader);
        }
    }

    if let Some(token) = header_str(ROOM_TOKEN_HEADER) {
        if token_hash(token) == token_hash(&room.file_token) {
            return Ok(Access::Member);
        }
    }

    Err(err(StatusCode::FORBIDDEN, "Forbidden"))
}

fn check_rate(
    state: &AppState,
    headers: &HeaderMap,
    peer: SocketAddr,
) -> Result<(), ApiError> {
    let ip = extract_client_ip(headers, peer);
    if state.files_rate_limiter.check(&ip) {
        Ok(())
    } else {
        warn!("[files] rate limit pour l'IP {}", ip);
        Err(err(StatusCode::TOO_MANY_REQUESTS, "Too many requests"))
    }
}

async fn dir_size(dir: &FsPath) -> u64 {
    let mut total = 0u64;
    let Ok(mut entries) = tokio::fs::read_dir(dir).await else {
        return 0;
    };
    while let Ok(Some(entry)) = entries.next_entry().await {
        if let Ok(meta) = entry.metadata().await {
            if meta.is_file() {
                total += meta.len();
            }
        }
    }
    total
}

async fn upload(
    State(state): State<AppState>,
    ConnectInfo(peer): ConnectInfo<SocketAddr>,
    Path((room_key, hash)): Path<(String, String)>,
    headers: HeaderMap,
    body: Body,
) -> Result<StatusCode, ApiError> {
    check_rate(&state, &headers, peer)?;
    if !is_hash(&hash) {
        return Err(err(StatusCode::BAD_REQUEST, "Invalid hash"));
    }
    if authorize(&state, &room_key, &headers)? != Access::Leader {
        return Err(err(StatusCode::FORBIDDEN, "Forbidden"));
    }

    let declared = headers
        .get(header::CONTENT_LENGTH)
        .and_then(|v| v.to_str().ok())
        .and_then(|v| v.parse::<u64>().ok());
    if declared.is_some_and(|len| len > state.config.file_max_bytes) {
        return Err(err(StatusCode::PAYLOAD_TOO_LARGE, "File too large"));
    }

    let hash = hash.to_ascii_lowercase();
    let dir = room_dir(&state.config.files_dir, &room_key);
    tokio::fs::create_dir_all(&dir).await.map_err(|e| {
        tracing::error!("[files] création de {:?} impossible : {}", dir, e);
        err(StatusCode::INTERNAL_SERVER_ERROR, "Internal error")
    })?;

    let final_path = dir.join(&hash);
    if tokio::fs::try_exists(&final_path).await.unwrap_or(false) {
        return Ok(StatusCode::OK);
    }

    let used = dir_size(&dir).await;
    let max_file = state.config.file_max_bytes;
    let room_budget = state.config.room_files_max_bytes.saturating_sub(used);
    let limit = max_file.min(room_budget);
    if limit == 0 {
        return Err(err(StatusCode::INSUFFICIENT_STORAGE, "Room quota reached"));
    }

    let tmp_path = dir.join(format!("{}.{}.part", hash, AppState::random_token()));
    let mut file = tokio::fs::File::create(&tmp_path).await.map_err(|e| {
        tracing::error!("[files] création de {:?} impossible : {}", tmp_path, e);
        err(StatusCode::INTERNAL_SERVER_ERROR, "Internal error")
    })?;

    let mut hasher = Sha1::new();
    let mut written = 0u64;
    let mut stream = body.into_data_stream();
    let mut failure: Option<ApiError> = None;

    while let Some(chunk) = stream.next().await {
        let chunk = match chunk {
            Ok(c) => c,
            Err(_) => {
                failure = Some(err(StatusCode::BAD_REQUEST, "Upload interrupted"));
                break;
            }
        };
        written += chunk.len() as u64;
        if written > limit {
            failure = Some(err(StatusCode::PAYLOAD_TOO_LARGE, "File too large"));
            break;
        }
        hasher.update(&chunk);
        if file.write_all(&chunk).await.is_err() {
            failure = Some(err(StatusCode::INTERNAL_SERVER_ERROR, "Internal error"));
            break;
        }
    }

    if failure.is_none() {
        if file.flush().await.is_err() {
            failure = Some(err(StatusCode::INTERNAL_SERVER_ERROR, "Internal error"));
        } else if written == 0 {
            failure = Some(err(StatusCode::BAD_REQUEST, "Empty file"));
        } else if hex(&hasher.finalize_reset()) != hash {
            failure = Some(err(StatusCode::BAD_REQUEST, "Hash mismatch"));
        }
    }
    drop(file);

    if let Some(e) = failure {
        let _ = tokio::fs::remove_file(&tmp_path).await;
        return Err(e);
    }

    tokio::fs::rename(&tmp_path, &final_path).await.map_err(|e| {
        tracing::error!("[files] renommage impossible : {}", e);
        err(StatusCode::INTERNAL_SERVER_ERROR, "Internal error")
    })?;

    info!("[files] room '{}' : {} déposé ({} octets)", room_key, hash, written);
    Ok(StatusCode::CREATED)
}

async fn download(
    State(state): State<AppState>,
    ConnectInfo(peer): ConnectInfo<SocketAddr>,
    Path((room_key, hash)): Path<(String, String)>,
    headers: HeaderMap,
) -> Result<Response, ApiError> {
    check_rate(&state, &headers, peer)?;
    if !is_hash(&hash) {
        return Err(err(StatusCode::BAD_REQUEST, "Invalid hash"));
    }
    authorize(&state, &room_key, &headers)?;

    let path = room_dir(&state.config.files_dir, &room_key).join(hash.to_ascii_lowercase());
    let file = tokio::fs::File::open(&path)
        .await
        .map_err(|_| err(StatusCode::NOT_FOUND, "File not found"))?;
    let len = file
        .metadata()
        .await
        .map(|m| m.len())
        .map_err(|_| err(StatusCode::INTERNAL_SERVER_ERROR, "Internal error"))?;

    let response = Response::builder()
        .status(StatusCode::OK)
        .header(header::CONTENT_TYPE, "application/octet-stream")
        .header(header::CONTENT_LENGTH, len)
        .body(Body::from_stream(ReaderStream::new(file)))
        .map_err(|_| err(StatusCode::INTERNAL_SERVER_ERROR, "Internal error"))?;
    Ok(response.into_response())
}

#[derive(Deserialize)]
struct CheckRequest {
    hashes: Vec<String>,
}

async fn check(
    State(state): State<AppState>,
    ConnectInfo(peer): ConnectInfo<SocketAddr>,
    Path(room_key): Path<String>,
    headers: HeaderMap,
    Json(body): Json<CheckRequest>,
) -> Result<Json<Value>, ApiError> {
    check_rate(&state, &headers, peer)?;
    authorize(&state, &room_key, &headers)?;
    if body.hashes.len() > MAX_CHECK_HASHES {
        return Err(err(StatusCode::BAD_REQUEST, "Too many hashes"));
    }

    let dir = room_dir(&state.config.files_dir, &room_key);
    let mut missing = Vec::new();
    for hash in body.hashes {
        if !is_hash(&hash) {
            return Err(err(StatusCode::BAD_REQUEST, "Invalid hash"));
        }
        let lowered = hash.to_ascii_lowercase();
        if !tokio::fs::try_exists(dir.join(&lowered)).await.unwrap_or(false) {
            missing.push(lowered);
        }
    }

    Ok(Json(json!({ "missing": missing })))
}

pub async fn sweep_orphans(state: &AppState) {
    let base = state.config.files_dir.clone();
    let Ok(mut entries) = tokio::fs::read_dir(&base).await else {
        return;
    };

    let live: std::collections::HashSet<String> = state
        .rooms
        .iter()
        .map(|r| {
            room_dir(&base, r.key())
                .file_name()
                .map(|n| n.to_string_lossy().into_owned())
                .unwrap_or_default()
        })
        .collect();

    let mut removed = 0u32;
    while let Ok(Some(entry)) = entries.next_entry().await {
        let name = entry.file_name().to_string_lossy().into_owned();
        if live.contains(&name) {
            continue;
        }
        let Ok(meta) = entry.metadata().await else {
            continue;
        };
        let age = meta
            .modified()
            .ok()
            .and_then(|m| m.elapsed().ok())
            .map(|d| d.as_secs())
            .unwrap_or(u64::MAX);
        if age < ORPHAN_GRACE_SECS {
            continue;
        }
        if tokio::fs::remove_dir_all(entry.path()).await.is_ok() {
            removed += 1;
        }
    }

    if removed > 0 {
        info!("[files] {} répertoire(s) de room orphelin(s) supprimé(s)", removed);
    }
}

pub fn router(max_file_bytes: u64) -> Router<AppState> {
    let limit = usize::try_from(max_file_bytes).unwrap_or(usize::MAX);
    Router::new()
        .route(
            "/api/files/{room_key}/{hash}",
            put(upload).layer(DefaultBodyLimit::max(limit)).get(download),
        )
        .route("/api/files/{room_key}/check", post(check))
}
