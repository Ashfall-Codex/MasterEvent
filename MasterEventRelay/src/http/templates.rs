use axum::{
    Router,
    extract::{Path, State},
    http::StatusCode,
    routing::{get, post},
    Json,
};
use serde_json::{json, Value};

use crate::db;
use crate::state::AppState;

const TEMPLATE_MAX_SIZE: usize = 64 * 1024;

async fn create_template(
    State(state): State<AppState>,
    body: String,
) -> (StatusCode, Json<Value>) {
    if body.len() > TEMPLATE_MAX_SIZE {
        return (
            StatusCode::BAD_REQUEST,
            Json(json!({ "error": "Body too large" })),
        );
    }

    let mut template: Value = match serde_json::from_str(&body) {
        Ok(v) => v,
        Err(e) => {
            return (
                StatusCode::BAD_REQUEST,
                Json(json!({ "error": e.to_string() })),
            );
        }
    };

    let name = match template.get("Name").and_then(|n| n.as_str()) {
        Some(n) => n.to_string(),
        None => {
            return (
                StatusCode::BAD_REQUEST,
                Json(json!({ "error": "Missing template Name" })),
            );
        }
    };

    let permanent = template
        .get("permanent")
        .and_then(|v| v.as_bool())
        .unwrap_or(false);

    // Retirer le flag permanent des données stockées
    if let Some(obj) = template.as_object_mut() {
        obj.remove("permanent");
    }

    let data_str = serde_json::to_string(&template).unwrap();
    let db = state.db.clone();

    let result = tokio::task::spawn_blocking(move || {
        let conn = db.blocking_lock();
        db::insert_template(&conn, &data_str, permanent)
    })
    .await
    .unwrap();

    match result {
        Ok(code) => {
            tracing::info!("Template stored: {code} ({name}, permanent: {permanent})");
            (StatusCode::OK, Json(json!({ "code": code })))
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
    // Validation du format du code
    if !code.chars().all(|c| c.is_ascii_alphanumeric()) {
        return (
            StatusCode::BAD_REQUEST,
            Json(json!({ "error": "Invalid code format" })),
        );
    }

    let db = state.db.clone();
    let code_clone = code.clone();

    let result = tokio::task::spawn_blocking(move || {
        let conn = db.blocking_lock();
        db::get_template(&conn, &code_clone)
    })
    .await
    .unwrap();

    match result {
        Ok(data_str) => {
            let data: Value = serde_json::from_str(&data_str).unwrap_or(Value::Null);
            (StatusCode::OK, Json(data))
        }
        Err(_) => (
            StatusCode::NOT_FOUND,
            Json(json!({ "error": "Template not found" })),
        ),
    }
}

pub fn router() -> Router<AppState> {
    Router::new()
        .route("/api/templates", post(create_template))
        .route("/api/templates/{code}", get(get_template))
}
