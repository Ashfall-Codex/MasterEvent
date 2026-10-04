use axum::{
    extract::{ConnectInfo, Path, Query, State},
    http::{HeaderMap, StatusCode},
    routing::{get, post},
    Json, Router,
};
use serde::Deserialize;
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::net::SocketAddr;

use crate::accounts::{self, UpsertOutcome};
use crate::connect_client::ConnectError;
use crate::http::{internal, with_db};
use crate::state::AppState;

const LEADER_TOKEN_HEADER: &str = "x-leader-token";
const MAX_NAME_LENGTH: usize = 128;
const MAX_ALIAS_LENGTH: usize = 64;

/// Le plugin s'authentifie avec son LeaderToken ; on n'en manipule que le SHA-256 exactement comme pour la propriété des templates partagés.
fn extract_token_hash(headers: &HeaderMap) -> Option<[u8; 32]> {
    headers
        .get(LEADER_TOKEN_HEADER)
        .and_then(|v| v.to_str().ok())
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(|token| {
            let mut hasher = Sha256::new();
            hasher.update(token.as_bytes());
            hasher.finalize().into()
        })
}

fn unauthorized() -> (StatusCode, Json<Value>) {
    (
        StatusCode::UNAUTHORIZED,
        Json(json!({ "error": "Missing or invalid X-Leader-Token header" })),
    )
}

/// Résout le compte du porteur du LeaderToken sans jamais le créer :
/// les lectures et écritures de documents exigent un `register` préalable.
async fn resolve_account(
    state: &AppState,
    headers: &HeaderMap,
) -> Result<accounts::Account, (StatusCode, Json<Value>)> {
    let Some(hash) = extract_token_hash(headers) else {
        return Err(unauthorized());
    };

    match with_db(state, move |conn| {
        accounts::get_or_create_by_token_hash(conn, &hash, None)
    })
    .await
    {
        Some(Ok(account)) => Ok(account),
        Some(Err(e)) => {
            tracing::error!("Résolution du compte MasterEvent échouée : {}", e);
            Err(internal())
        }
        None => Err(internal()),
    }
}

#[derive(Deserialize)]
struct RegisterRequest {
    alias: Option<String>,
}

/// Premier contact du plugin : échange le LeaderToken contre un identifier public stable.
async fn register(
    State(state): State<AppState>,
    ConnectInfo(addr): ConnectInfo<SocketAddr>,
    headers: HeaderMap,
    body: Option<Json<RegisterRequest>>,
) -> (StatusCode, Json<Value>) {
    if !state.account_rate_limiter.check(&addr.ip().to_string()) {
        return (
            StatusCode::TOO_MANY_REQUESTS,
            Json(json!({ "error": "Too many requests" })),
        );
    }

    let Some(hash) = extract_token_hash(&headers) else {
        return unauthorized();
    };

    let alias = body
        .and_then(|Json(b)| b.alias)
        .map(|a| truncate(a.trim(), MAX_ALIAS_LENGTH))
        .filter(|a| !a.is_empty());

    match with_db(&state, move |conn| {
        accounts::get_or_create_by_token_hash(conn, &hash, alias.as_deref())
    })
    .await
    {
        Some(Ok(account)) => (
            StatusCode::OK,
            Json(json!({
                "identifier": account.id,
                "alias": account.alias,
                "createdAt": account.created_at,
            })),
        ),
        Some(Err(e)) => {
            tracing::error!("Enregistrement du compte MasterEvent échoué : {}", e);
            internal()
        }
        None => internal(),
    }
}

#[derive(Deserialize)]
struct ListQuery {
    since: Option<i64>,
    kind: Option<String>,
}

/// Synchro descendante : ce qui a changé depuis `since`, tombstones compris.
async fn list_documents(
    State(state): State<AppState>,
    headers: HeaderMap,
    Query(query): Query<ListQuery>,
) -> (StatusCode, Json<Value>) {
    let account = match resolve_account(&state, &headers).await {
        Ok(a) => a,
        Err(e) => return e,
    };

    if let Some(kind) = &query.kind {
        if !accounts::is_valid_kind(kind) {
            return (
                StatusCode::BAD_REQUEST,
                Json(json!({ "error": "Invalid kind" })),
            );
        }
    }

    let since = query.since.unwrap_or(0).max(0);
    let kind = query.kind.clone();
    let account_id = account.id.clone();

    match with_db(&state, move |conn| {
        accounts::list_documents(conn, &account_id, kind.as_deref(), since, true, true)
    })
    .await
    {
        Some(Ok(docs)) => (
            StatusCode::OK,
            Json(json!({
                "serverTime": accounts::now_ms(),
                "documents": docs.iter().map(document_json).collect::<Vec<_>>(),
            })),
        ),
        Some(Err(e)) => {
            tracing::error!("Listing des documents échoué : {}", e);
            internal()
        }
        None => internal(),
    }
}

/// Synchro montante : le plugin dépose l'état courant d'une fiche ou d'un modèle.
async fn put_document(
    State(state): State<AppState>,
    Path((kind, name)): Path<(String, String)>,
    headers: HeaderMap,
    body: String,
) -> (StatusCode, Json<Value>) {
    let account = match resolve_account(&state, &headers).await {
        Ok(a) => a,
        Err(e) => return e,
    };

    match validate_document(&kind, &name, &body) {
        Ok(()) => {}
        Err(e) => return e,
    }

    let account_id = account.id.clone();
    let (kind_owned, name_owned) = (kind.clone(), name.trim().to_string());

    match with_db(&state, move |conn| {
        accounts::upsert_document(conn, &account_id, &kind_owned, &name_owned, &body)
    })
    .await
    {
        Some(Ok(UpsertOutcome::Saved(doc))) => (StatusCode::OK, Json(document_json(&doc))),
        Some(Ok(UpsertOutcome::QuotaExceeded)) => (
            StatusCode::INSUFFICIENT_STORAGE,
            Json(json!({ "error": "Document quota reached" })),
        ),
        Some(Err(e)) => {
            tracing::error!("Écriture du document échouée : {}", e);
            internal()
        }
        None => internal(),
    }
}

async fn delete_document(
    State(state): State<AppState>,
    Path((kind, name)): Path<(String, String)>,
    headers: HeaderMap,
) -> (StatusCode, Json<Value>) {
    let account = match resolve_account(&state, &headers).await {
        Ok(a) => a,
        Err(e) => return e,
    };

    if !accounts::is_valid_kind(&kind) {
        return (
            StatusCode::BAD_REQUEST,
            Json(json!({ "error": "Invalid kind" })),
        );
    }

    let account_id = account.id.clone();
    let name_owned = name.trim().to_string();

    match with_db(&state, move |conn| {
        accounts::delete_document(conn, &account_id, &kind, &name_owned)
    })
    .await
    {
        Some(Ok(true)) => (StatusCode::OK, Json(json!({ "deleted": true }))),
        Some(Ok(false)) => (
            StatusCode::NOT_FOUND,
            Json(json!({ "error": "Document not found" })),
        ),
        Some(Err(e)) => {
            tracing::error!("Suppression du document échouée : {}", e);
            internal()
        }
        None => internal(),
    }
}

#[derive(Deserialize)]
struct GenerateLinkCodeRequest {
    alias: Option<String>,
}

/// Le plugin demande un code de liaison ; c'est le relay qui parle à Connect,
/// jamais le plugin directement (il ne détient aucun service token).
async fn generate_link_code(
    State(state): State<AppState>,
    ConnectInfo(addr): ConnectInfo<SocketAddr>,
    headers: HeaderMap,
    body: Option<Json<GenerateLinkCodeRequest>>,
) -> (StatusCode, Json<Value>) {
    if !state.connect_rate_limiter.check(&addr.ip().to_string()) {
        return (
            StatusCode::TOO_MANY_REQUESTS,
            Json(json!({ "error": "Too many requests" })),
        );
    }

    let Some(hash) = extract_token_hash(&headers) else {
        return unauthorized();
    };

    let alias = body
        .and_then(|Json(b)| b.alias)
        .map(|a| truncate(a.trim(), MAX_ALIAS_LENGTH))
        .filter(|a| !a.is_empty());
    let alias_for_db = alias.clone();

    let account = match with_db(&state, move |conn| {
        accounts::get_or_create_by_token_hash(conn, &hash, alias_for_db.as_deref())
    })
    .await
    {
        Some(Ok(a)) => a,
        Some(Err(e)) => {
            tracing::error!("Résolution du compte avant génération de code échouée : {}", e);
            return internal();
        }
        None => return internal(),
    };

    match state
        .connect
        .create_link_code(&account.id, account.alias.as_deref())
        .await
    {
        Ok(mut value) => {
            // On renvoie aussi l'identifier : le plugin le persiste pour ses appels suivants.
            if let Some(obj) = value.as_object_mut() {
                obj.insert("identifier".into(), json!(account.id));
            }
            (StatusCode::OK, Json(value))
        }
        Err(ConnectError::NotConfigured) => (
            StatusCode::SERVICE_UNAVAILABLE,
            Json(json!({ "error": "connect_not_configured" })),
        ),
        Err(ConnectError::Unreachable) => (
            StatusCode::BAD_GATEWAY,
            Json(json!({ "error": "connect_unreachable" })),
        ),
    }
}

async fn link_status(
    State(state): State<AppState>,
    Path(code): Path<String>,
    headers: HeaderMap,
) -> (StatusCode, Json<Value>) {
    if extract_token_hash(&headers).is_none() {
        return unauthorized();
    }
    if !code.chars().all(|c| c.is_ascii_alphanumeric()) || code.len() > 16 {
        return (
            StatusCode::BAD_REQUEST,
            Json(json!({ "error": "Invalid code format" })),
        );
    }

    match state.connect.get_link_code_status(&code).await {
        Ok(value) => (StatusCode::OK, Json(value)),
        Err(ConnectError::NotConfigured) => (
            StatusCode::SERVICE_UNAVAILABLE,
            Json(json!({ "error": "connect_not_configured" })),
        ),
        Err(ConnectError::Unreachable) => (
            StatusCode::BAD_GATEWAY,
            Json(json!({ "error": "connect_unreachable" })),
        ),
    }
}

/// État de liaison du compte courant, affiché dans l'onglet Cloud du plugin.
async fn my_status(State(state): State<AppState>, headers: HeaderMap) -> (StatusCode, Json<Value>) {
    let account = match resolve_account(&state, &headers).await {
        Ok(a) => a,
        Err(e) => return e,
    };

    if !state.connect.is_configured() {
        return (
            StatusCode::OK,
            Json(json!({ "connectEnabled": false, "identifier": account.id, "linked": false })),
        );
    }

    match state.connect.get_verification(&account.id).await {
        Ok(v) => (
            StatusCode::OK,
            Json(json!({
                "connectEnabled": true,
                "identifier": account.id,
                "linked": v.get("verified").and_then(Value::as_bool).unwrap_or(false),
                "level": v.get("level").cloned().unwrap_or(Value::Null),
                "since": v.get("since").cloned().unwrap_or(Value::Null),
            })),
        ),
        Err(_) => (
            StatusCode::OK,
            Json(json!({ "connectEnabled": true, "identifier": account.id, "linked": false })),
        ),
    }
}

/// Représentation commune d'un document, partagée par les deux façades HTTP.
pub fn document_json(doc: &accounts::Document) -> Value {
    let mut value = json!({
        "id": doc.id,
        "kind": doc.kind,
        "name": doc.name,
        "version": doc.version,
        "updatedAt": doc.updated_at,
        "deleted": doc.deleted,
    });
    if let (Some(data), false) = (&doc.data, doc.deleted) {
        let parsed: Value = serde_json::from_str(data).unwrap_or(Value::Null);
        // Remonté même quand le contenu n'est pas joint (listing) : le site en a besoin pour
        // présenter les modèles d'un autre MJ en lecture seule.
        let read_only = parsed
            .get("IsSubscription")
            .and_then(Value::as_bool)
            .unwrap_or(false);
        let obj = value.as_object_mut().expect("json! object");
        obj.insert("readOnly".into(), json!(read_only));
        obj.insert("data".into(), parsed);
    }
    value
}

/// Variante métadonnées seules : même forme, sans le contenu, mais en conservant `readOnly`.
pub fn document_summary_json(doc: &accounts::Document, raw_data: Option<&str>) -> Value {
    let read_only = raw_data
        .and_then(|raw| serde_json::from_str::<Value>(raw).ok())
        .and_then(|v| v.get("IsSubscription").and_then(Value::as_bool))
        .unwrap_or(false);
    json!({
        "id": doc.id,
        "kind": doc.kind,
        "name": doc.name,
        "version": doc.version,
        "updatedAt": doc.updated_at,
        "deleted": doc.deleted,
        "readOnly": read_only,
    })
}

/// Contrôles communs à toute écriture : type connu, nom exploitable, taille bornée,
/// contenu réellement JSON (la base ne doit jamais accueillir de payload opaque).
pub fn validate_document(
    kind: &str,
    name: &str,
    body: &str,
) -> Result<(), (StatusCode, Json<Value>)> {
    if !accounts::is_valid_kind(kind) {
        return Err((
            StatusCode::BAD_REQUEST,
            Json(json!({ "error": "Invalid kind" })),
        ));
    }
    let trimmed = name.trim();
    if trimmed.is_empty() || trimmed.chars().count() > MAX_NAME_LENGTH {
        return Err((
            StatusCode::BAD_REQUEST,
            Json(json!({ "error": "Invalid document name" })),
        ));
    }
    if body.len() > accounts::MAX_DOCUMENT_SIZE {
        return Err((
            StatusCode::BAD_REQUEST,
            Json(json!({ "error": "Body too large" })),
        ));
    }
    match serde_json::from_str::<Value>(body) {
        Ok(Value::Object(_)) => Ok(()),
        _ => Err((
            StatusCode::BAD_REQUEST,
            Json(json!({ "error": "Body must be a JSON object" })),
        )),
    }
}

fn truncate(s: &str, max: usize) -> String {
    s.chars().take(max).collect()
}

pub fn router() -> Router<AppState> {
    Router::new()
        .route("/api/account/register", post(register))
        .route("/api/cloud/documents", get(list_documents))
        .route(
            "/api/cloud/documents/{kind}/{name}",
            axum::routing::put(put_document).delete(delete_document),
        )
        .route("/api/connect/generate-link-code", post(generate_link_code))
        .route("/api/connect/link-status/{code}", get(link_status))
        .route("/api/connect/my-status", get(my_status))
}
