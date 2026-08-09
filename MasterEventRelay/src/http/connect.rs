use axum::{
    extract::{Path, Query, State},
    http::{HeaderMap, StatusCode},
    routing::get,
    Json, Router,
};
use serde::Deserialize;
use serde_json::{json, Value};

use crate::accounts::{self, RenameOutcome, UpsertOutcome};
use crate::http::cloud::{document_json, document_summary_json, validate_document};
use crate::state::AppState;
fn authorize(state: &AppState, headers: &HeaderMap) -> Result<(), (StatusCode, Json<Value>)> {
    let expected = &state.config.connect_incoming_token;
    if expected.is_empty() {
        // Sans secret configuré, la façade reste fermée plutôt que ouverte à tous.
        return Err(forbidden());
    }

    let provided = headers
        .get("authorization")
        .and_then(|v| v.to_str().ok())
        .and_then(|raw| {
            raw.strip_prefix("Bearer ")
                .or_else(|| raw.strip_prefix("bearer "))
        })
        .map(str::trim)
        .unwrap_or_default();

    if provided.is_empty() || !constant_time_eq(provided.as_bytes(), expected.as_bytes()) {
        return Err(forbidden());
    }
    Ok(())
}

/// Comparaison à durée constante : ne pas laisser fuiter le secret par le temps de réponse.
fn constant_time_eq(a: &[u8], b: &[u8]) -> bool {
    if a.len() != b.len() {
        return false;
    }
    let mut diff = 0u8;
    for (x, y) in a.iter().zip(b.iter()) {
        diff |= x ^ y;
    }
    diff == 0
}

fn forbidden() -> (StatusCode, Json<Value>) {
    (
        StatusCode::UNAUTHORIZED,
        Json(json!({ "error": "Invalid service token" })),
    )
}

fn not_found() -> (StatusCode, Json<Value>) {
    (
        StatusCode::NOT_FOUND,
        Json(json!({ "error": "Not found" })),
    )
}

/// Un modèle importé depuis le code de partage d'un autre MJ (`IsSubscription`) reste sa
/// propriété : le plugin l'affiche déjà en lecture seule, et la façade web applique la même
/// règle. Le désabonnement, lui, passe par le chemin plugin et reste possible en jeu.
fn is_subscribed_template(doc: &accounts::Document) -> bool {
    doc.data
        .as_deref()
        .and_then(|raw| serde_json::from_str::<Value>(raw).ok())
        .and_then(|v| v.get("IsSubscription").and_then(Value::as_bool))
        .unwrap_or(false)
}

fn read_only() -> (StatusCode, Json<Value>) {
    (
        StatusCode::FORBIDDEN,
        Json(json!({ "error": "subscribed_template_readonly" })),
    )
}

fn internal() -> (StatusCode, Json<Value>) {
    (
        StatusCode::INTERNAL_SERVER_ERROR,
        Json(json!({ "error": "Internal error" })),
    )
}

async fn with_db<T, F>(state: &AppState, op: F) -> Option<rusqlite::Result<T>>
where
    T: Send + 'static,
    F: FnOnce(&rusqlite::Connection) -> rusqlite::Result<T> + Send + 'static,
{
    let db = state.db.clone();
    match tokio::task::spawn_blocking(move || {
        let conn = db.blocking_lock();
        op(&conn)
    })
    .await
    {
        Ok(r) => Some(r),
        Err(e) => {
            tracing::error!("Tâche SQLite interrompue : {}", e);
            None
        }
    }
}

/// Garde commune : token valide + identifier bien formé + compte existant.
async fn guard(
    state: &AppState,
    headers: &HeaderMap,
    identifier: &str,
) -> Result<accounts::Account, (StatusCode, Json<Value>)> {
    authorize(state, headers)?;

    if !accounts::is_valid_account_id(identifier) {
        return Err(not_found());
    }

    let id = identifier.to_string();
    match with_db(state, move |conn| accounts::find_by_id(conn, &id)).await {
        Some(Ok(Some(account))) => Ok(account),
        Some(Ok(None)) => Err(not_found()),
        Some(Err(e)) => {
            tracing::error!("Résolution du compte depuis Connect échouée : {}", e);
            Err(internal())
        }
        None => Err(internal()),
    }
}

/// Endpoint standard du protocole : Connect y lit l'alias et de quoi afficher un aperçu.
async fn get_account(
    State(state): State<AppState>,
    Path(identifier): Path<String>,
    headers: HeaderMap,
) -> (StatusCode, Json<Value>) {
    let account = match guard(&state, &headers, &identifier).await {
        Ok(a) => a,
        Err(e) => return e,
    };

    let account_id = account.id.clone();
    let counts = with_db(&state, move |conn| {
        Ok((
            accounts::count_documents(conn, &account_id, accounts::KIND_TEMPLATE)?,
            accounts::count_documents(conn, &account_id, accounts::KIND_SHEET)?,
            accounts::count_documents(conn, &account_id, accounts::KIND_NOTE)?,
        ))
    })
    .await;

    let (templates, sheets, notes) = match counts {
        Some(Ok(c)) => c,
        _ => (0, 0, 0),
    };

    (
        StatusCode::OK,
        Json(json!({
            "identifier": account.id,
            "alias": account.alias,
            "metadata": { "templates": templates, "sheets": sheets, "notes": notes },
        })),
    )
}

#[derive(Deserialize)]
struct KindQuery {
    kind: Option<String>,
}

async fn list_documents(
    State(state): State<AppState>,
    Path(identifier): Path<String>,
    headers: HeaderMap,
    Query(query): Query<KindQuery>,
) -> (StatusCode, Json<Value>) {
    let account = match guard(&state, &headers, &identifier).await {
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

    let account_id = account.id.clone();
    let kind = query.kind.clone();

    // Le contenu est lu mais pas renvoyé : la page n'affiche que noms et versions, le détail
    // est chargé document par document. On en a besoin ici uniquement pour savoir si le
    // document appartient à un autre MJ (`readOnly`).
    match with_db(&state, move |conn| {
        accounts::list_documents(conn, &account_id, kind.as_deref(), 0, false, true)
    })
    .await
    {
        Some(Ok(docs)) => (
            StatusCode::OK,
            Json(json!(docs
                .iter()
                .map(|d| document_summary_json(d, d.data.as_deref()))
                .collect::<Vec<_>>())),
        ),
        Some(Err(e)) => {
            tracing::error!("Listing Connect échoué : {}", e);
            internal()
        }
        None => internal(),
    }
}

async fn get_document(
    State(state): State<AppState>,
    Path((identifier, doc_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> (StatusCode, Json<Value>) {
    let account = match guard(&state, &headers, &identifier).await {
        Ok(a) => a,
        Err(e) => return e,
    };

    let account_id = account.id.clone();
    match with_db(&state, move |conn| {
        accounts::get_document_by_id(conn, &account_id, &doc_id)
    })
    .await
    {
        Some(Ok(Some(doc))) => (StatusCode::OK, Json(document_json(&doc))),
        Some(Ok(None)) => not_found(),
        Some(Err(e)) => {
            tracing::error!("Lecture de document depuis Connect échouée : {}", e);
            internal()
        }
        None => internal(),
    }
}

#[derive(Deserialize)]
struct WriteDocumentRequest {
    kind: Option<String>,
    name: String,
    data: Value,
}

/// Création d'un document depuis le site.
async fn create_document(
    State(state): State<AppState>,
    Path(identifier): Path<String>,
    headers: HeaderMap,
    Json(body): Json<WriteDocumentRequest>,
) -> (StatusCode, Json<Value>) {
    let account = match guard(&state, &headers, &identifier).await {
        Ok(a) => a,
        Err(e) => return e,
    };

    let kind = body.kind.unwrap_or_default();
    let data = body.data.to_string();
    if let Err(e) = validate_document(&kind, &body.name, &data) {
        return e;
    }

    let account_id = account.id.clone();
    let name = body.name.trim().to_string();

    match with_db(&state, move |conn| {
        accounts::upsert_document(conn, &account_id, &kind, &name, &data)
    })
    .await
    {
        Some(Ok(UpsertOutcome::Saved(doc))) => (StatusCode::OK, Json(document_json(&doc))),
        Some(Ok(UpsertOutcome::QuotaExceeded)) => (
            StatusCode::INSUFFICIENT_STORAGE,
            Json(json!({ "error": "Document quota reached" })),
        ),
        Some(Err(e)) => {
            tracing::error!("Création de document depuis Connect échouée : {}", e);
            internal()
        }
        None => internal(),
    }
}

/// Mise à jour d'un document existant. Le renommage passe par la même route :
/// on repère le document par son id, donc changer `name` est une opération légitime.
async fn update_document(
    State(state): State<AppState>,
    Path((identifier, doc_id)): Path<(String, String)>,
    headers: HeaderMap,
    Json(body): Json<WriteDocumentRequest>,
) -> (StatusCode, Json<Value>) {
    let account = match guard(&state, &headers, &identifier).await {
        Ok(a) => a,
        Err(e) => return e,
    };

    let account_id = account.id.clone();
    let doc_id_for_lookup = doc_id.clone();
    let existing = match with_db(&state, move |conn| {
        accounts::get_document_by_id(conn, &account_id, &doc_id_for_lookup)
    })
    .await
    {
        Some(Ok(Some(doc))) => doc,
        Some(Ok(None)) => return not_found(),
        Some(Err(e)) => {
            tracing::error!("Lecture avant écriture échouée : {}", e);
            return internal();
        }
        None => return internal(),
    };

    if is_subscribed_template(&existing) {
        return read_only();
    }

    // Le type d'un document ne change jamais : celui du corps est ignoré au profit du stocké.
    let kind = existing.kind.clone();
    let data = body.data.to_string();
    if let Err(e) = validate_document(&kind, &body.name, &data) {
        return e;
    }

    let account_id = account.id.clone();
    let new_name = body.name.trim().to_string();
    let previous_name = existing.name.clone();
    let doc_id_for_write = doc_id.clone();

    let outcome = with_db(&state, move |conn| {
        if new_name != previous_name {
            match accounts::rename_document(conn, &account_id, &doc_id_for_write, &new_name)? {
                RenameOutcome::Conflict => return Ok(None),
                RenameOutcome::NotFound => return Ok(None),
                RenameOutcome::Renamed => {}
            }
        }
        accounts::upsert_document(conn, &account_id, &kind, &new_name, &data).map(Some)
    })
    .await;

    match outcome {
        Some(Ok(Some(UpsertOutcome::Saved(doc)))) => (StatusCode::OK, Json(document_json(&doc))),
        Some(Ok(Some(UpsertOutcome::QuotaExceeded))) => (
            StatusCode::INSUFFICIENT_STORAGE,
            Json(json!({ "error": "Document quota reached" })),
        ),
        Some(Ok(None)) => (
            StatusCode::CONFLICT,
            Json(json!({ "error": "name_already_used" })),
        ),
        Some(Err(e)) => {
            tracing::error!("Écriture de document depuis Connect échouée : {}", e);
            internal()
        }
        None => internal(),
    }
}

async fn delete_document(
    State(state): State<AppState>,
    Path((identifier, doc_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> (StatusCode, Json<Value>) {
    let account = match guard(&state, &headers, &identifier).await {
        Ok(a) => a,
        Err(e) => return e,
    };

    // Vérification de propriété avant suppression : un modèle abonné n'est pas au propriétaire
    // du coffre, il ne peut pas être retiré depuis le web.
    let account_id = account.id.clone();
    let doc_id_for_lookup = doc_id.clone();
    match with_db(&state, move |conn| {
        accounts::get_document_by_id(conn, &account_id, &doc_id_for_lookup)
    })
    .await
    {
        Some(Ok(Some(doc))) if is_subscribed_template(&doc) => return read_only(),
        Some(Ok(None)) => return not_found(),
        Some(Err(e)) => {
            tracing::error!("Lecture avant suppression échouée : {}", e);
            return internal();
        }
        None => return internal(),
        _ => {}
    }

    let account_id = account.id.clone();
    match with_db(&state, move |conn| {
        accounts::delete_document_by_id(conn, &account_id, &doc_id)
    })
    .await
    {
        Some(Ok(true)) => (StatusCode::OK, Json(json!({ "deleted": true }))),
        Some(Ok(false)) => not_found(),
        Some(Err(e)) => {
            tracing::error!("Suppression depuis Connect échouée : {}", e);
            internal()
        }
        None => internal(),
    }
}

pub fn router() -> Router<AppState> {
    Router::new()
        .route("/api/connect/account/{identifier}", get(get_account))
        .route(
            "/api/connect/documents/{identifier}",
            get(list_documents).post(create_document),
        )
        .route(
            "/api/connect/documents/{identifier}/{doc_id}",
            get(get_document)
                .put(update_document)
                .delete(delete_document),
        )
}
