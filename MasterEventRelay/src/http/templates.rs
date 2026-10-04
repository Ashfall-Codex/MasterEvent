use axum::{
    extract::{Path, State},
    http::{HeaderMap, StatusCode},
    routing::{get, post},
    Json, Router,
};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};

use crate::db;
use crate::state::AppState;

const TEMPLATE_MAX_SIZE: usize = 64 * 1024;
const LEADER_TOKEN_HEADER: &str = "x-leader-token";

// Hash SHA-256 d'un token pour vérifier l'identité du créateur sans stocker le secret.
fn hash_token(token: &str) -> [u8; 32] {
    let mut hasher = Sha256::new();
    hasher.update(token.as_bytes());
    hasher.finalize().into()
}

// Extrait et hash le header `X-Leader-Token` s'il est présent et valide.
fn extract_token_hash(headers: &HeaderMap) -> Option<[u8; 32]> {
    headers
        .get(LEADER_TOKEN_HEADER)
        .and_then(|v| v.to_str().ok())
        .filter(|s| !s.is_empty())
        .map(hash_token)
}

// Diffuse un message templateUpdated à tous les clients connectés, toutes rooms confondues.
// Les clients filtrent eux-mêmes selon leurs abonnements locaux.
fn broadcast_template_updated(state: &AppState, code: &str, version: i64, name: &str) {
    // Champs préfixés "template*" pour éviter toute collision avec les champs génériques
    // déjà utilisés dans le protocole WS (playerVersion, roomKey, etc.).
    let msg = json!({
        "type": "templateUpdated",
        "templateCode": code,
        "templateVersion": version,
        "templateName": name,
    });
    let payload = match serde_json::to_string(&msg) {
        Ok(s) => s,
        Err(e) => {
            tracing::error!("Failed to serialize templateUpdated broadcast: {}", e);
            return;
        }
    };

    let mut notified = 0u32;
    for room in state.rooms.iter() {
        for handle in room.value().clients.values() {
            if handle.sender.send(payload.clone()).is_ok() {
                notified += 1;
            }
        }
    }
    tracing::info!(
        "[templateUpdated] broadcast {} v{} → {} clients",
        code, version, notified
    );
}

/// Valide et normalise le corps d'un modèle.
/// Retourne (nom, données sérialisées sans le drapeau `permanent`, permanent).
fn parse_template_body(body: &str) -> Result<(String, String, bool), (StatusCode, Json<Value>)> {
    if body.len() > TEMPLATE_MAX_SIZE {
        return Err((
            StatusCode::BAD_REQUEST,
            Json(json!({ "error": "Body too large" })),
        ));
    }

    let mut template: Value = serde_json::from_str(body).map_err(|e| {
        (
            StatusCode::BAD_REQUEST,
            Json(json!({ "error": e.to_string() })),
        )
    })?;

    let name = template
        .get("Name")
        .and_then(|n| n.as_str())
        .ok_or_else(|| {
            (
                StatusCode::BAD_REQUEST,
                Json(json!({ "error": "Missing template Name" })),
            )
        })?
        .to_string();

    let permanent = template
        .get("permanent")
        .and_then(|v| v.as_bool())
        .unwrap_or(false);

    // Le drapeau est un paramètre de stockage, pas une donnée du modèle.
    if let Some(obj) = template.as_object_mut() {
        obj.remove("permanent");
    }

    let data_str = serde_json::to_string(&template).map_err(|e| {
        tracing::error!("Failed to serialize template: {}", e);
        (
            StatusCode::BAD_REQUEST,
            Json(json!({ "error": "Invalid template data" })),
        )
    })?;

    Ok((name, data_str, permanent))
}

async fn create_template(
    State(state): State<AppState>,
    headers: HeaderMap,
    body: String,
) -> (StatusCode, Json<Value>) {
    let (name, data_str, permanent) = match parse_template_body(&body) {
        Ok(v) => v,
        Err(resp) => return resp,
    };

    // Hash du LeaderToken du créateur (si fourni) pour autoriser les PUT ultérieurs.
    let token_hash = extract_token_hash(&headers);
    let db_handle = state.db.clone();

    let result = match tokio::task::spawn_blocking(move || {
        let conn = db_handle.blocking_lock();
        db::insert_template(&conn, &data_str, permanent, token_hash.as_ref().map(|h| h.as_slice()))
    })
    .await
    {
        Ok(r) => r,
        Err(e) => {
            tracing::error!("Template insert task panicked: {}", e);
            return (
                StatusCode::INTERNAL_SERVER_ERROR,
                Json(json!({ "error": "Internal error" })),
            );
        }
    };

    match result {
        Ok(code) => {
            tracing::info!(
                "Template stored: {code} ({name}, permanent: {permanent}, owner: {})",
                if token_hash.is_some() { "yes" } else { "anonymous" }
            );
            (StatusCode::OK, Json(json!({ "code": code, "version": 1 })))
        }
        Err(rusqlite::Error::QueryReturnedNoRows) => (
            StatusCode::SERVICE_UNAVAILABLE,
            Json(json!({ "error": "Template store full" })),
        ),
        Err(e) => (
            StatusCode::INTERNAL_SERVER_ERROR,
            Json(json!({ "error": e.to_string() })),
        ),
    }
}

async fn get_template(
    State(state): State<AppState>,
    Path(code): Path<String>,
) -> (StatusCode, Json<Value>) {
    if !code.chars().all(|c| c.is_ascii_alphanumeric()) {
        return (
            StatusCode::BAD_REQUEST,
            Json(json!({ "error": "Invalid code format" })),
        );
    }

    let db_handle = state.db.clone();
    let code_clone = code.clone();

    let result = match tokio::task::spawn_blocking(move || {
        let conn = db_handle.blocking_lock();
        db::get_template(&conn, &code_clone)
    })
    .await
    {
        Ok(r) => r,
        Err(e) => {
            tracing::error!("Template get task panicked: {}", e);
            return (
                StatusCode::INTERNAL_SERVER_ERROR,
                Json(json!({ "error": "Internal error" })),
            );
        }
    };

    match result {
        Ok(record) => {
            let data: Value = serde_json::from_str(&record.data).unwrap_or(Value::Null);
            (
                StatusCode::OK,
                Json(json!({ "data": data, "version": record.version })),
            )
        }
        Err(_) => (
            StatusCode::NOT_FOUND,
            Json(json!({ "error": "Template not found" })),
        ),
    }
}

// Endpoint léger : renvoie uniquement la version actuelle pour polling.
async fn get_template_version(
    State(state): State<AppState>,
    Path(code): Path<String>,
) -> (StatusCode, Json<Value>) {
    if !code.chars().all(|c| c.is_ascii_alphanumeric()) {
        return (
            StatusCode::BAD_REQUEST,
            Json(json!({ "error": "Invalid code format" })),
        );
    }

    let db_handle = state.db.clone();
    let code_clone = code.clone();

    let result = tokio::task::spawn_blocking(move || {
        let conn = db_handle.blocking_lock();
        db::get_version(&conn, &code_clone)
    })
    .await;

    match result {
        Ok(Ok(version)) => (StatusCode::OK, Json(json!({ "version": version }))),
        Ok(Err(_)) => (
            StatusCode::NOT_FOUND,
            Json(json!({ "error": "Template not found" })),
        ),
        Err(e) => {
            tracing::error!("Template version task panicked: {}", e);
            (
                StatusCode::INTERNAL_SERVER_ERROR,
                Json(json!({ "error": "Internal error" })),
            )
        }
    }
}

/// Charge utile validée d'une mise à jour de modèle.
struct TemplateUpdate {
    name: String,
    data: String,
    token_hash: [u8; 32],
}

/// Valide une mise à jour de modèle : format du code, taille, jeton et charge utile.
fn parse_update_body(
    code: &str,
    body: &str,
    headers: &HeaderMap,
) -> Result<TemplateUpdate, (StatusCode, Json<Value>)> {
    if !code.chars().all(|c| c.is_ascii_alphanumeric()) {
        return Err((
            StatusCode::BAD_REQUEST,
            Json(json!({ "error": "Invalid code format" })),
        ));
    }

    if body.len() > TEMPLATE_MAX_SIZE {
        return Err((
            StatusCode::BAD_REQUEST,
            Json(json!({ "error": "Body too large" })),
        ));
    }

    let token_hash = extract_token_hash(headers).ok_or_else(|| {
        (
            StatusCode::UNAUTHORIZED,
            Json(json!({ "error": "Missing X-Leader-Token header" })),
        )
    })?;

    let mut template: Value = serde_json::from_str(body).map_err(|e| {
        (
            StatusCode::BAD_REQUEST,
            Json(json!({ "error": e.to_string() })),
        )
    })?;

    let name = template
        .get("Name")
        .and_then(|n| n.as_str())
        .unwrap_or("")
        .to_string();

    // Retirer le flag permanent du payload (on n'autorise pas son changement via PUT).
    if let Some(obj) = template.as_object_mut() {
        obj.remove("permanent");
    }

    let data_str = serde_json::to_string(&template).map_err(|e| {
        tracing::error!("Failed to serialize template for update: {}", e);
        (
            StatusCode::BAD_REQUEST,
            Json(json!({ "error": "Invalid template data" })),
        )
    })?;

    Ok(TemplateUpdate {
        name,
        data: data_str,
        token_hash,
    })
}

async fn update_template(
    State(state): State<AppState>,
    Path(code): Path<String>,
    headers: HeaderMap,
    body: String,
) -> (StatusCode, Json<Value>) {
    let update = match parse_update_body(&code, &body, &headers) {
        Ok(v) => v,
        Err(resp) => return resp,
    };
    let TemplateUpdate {
        name,
        data: data_str,
        token_hash,
    } = update;

    let db_handle = state.db.clone();
    let code_for_db = code.clone();

    let result = tokio::task::spawn_blocking(move || {
        let conn = db_handle.blocking_lock();
        db::update_template(&conn, &code_for_db, &data_str, &token_hash)
    })
    .await;

    match result {
        Ok(Ok(db::UpdateResult::Updated(new_version))) => {
            tracing::info!("Template {} mis à jour (v{}) par le créateur", code, new_version);
            broadcast_template_updated(&state, &code, new_version, &name);
            (
                StatusCode::OK,
                Json(json!({ "code": code, "version": new_version })),
            )
        }
        Ok(Ok(db::UpdateResult::NotFound)) => (
            StatusCode::NOT_FOUND,
            Json(json!({ "error": "Template not found" })),
        ),
        Ok(Ok(db::UpdateResult::Forbidden)) => {
            tracing::warn!("Tentative de PUT sur {} avec un token non autorisé", code);
            (
                StatusCode::FORBIDDEN,
                Json(json!({ "error": "Not the template owner" })),
            )
        }
        Ok(Err(e)) => (
            StatusCode::INTERNAL_SERVER_ERROR,
            Json(json!({ "error": e.to_string() })),
        ),
        Err(e) => {
            tracing::error!("Template update task panicked: {}", e);
            (
                StatusCode::INTERNAL_SERVER_ERROR,
                Json(json!({ "error": "Internal error" })),
            )
        }
    }
}

pub fn router() -> Router<AppState> {
    Router::new()
        .route("/api/templates", post(create_template))
        .route("/api/templates/{code}", get(get_template).put(update_template))
        .route("/api/templates/{code}/version", get(get_template_version))
}
