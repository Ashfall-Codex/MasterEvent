pub mod cloud;
pub mod connect;
pub mod health;
pub mod metrics;
pub mod templates;

use axum::http::StatusCode;
use axum::Json;
use axum::Router;
use serde_json::{json, Value};

use crate::state::AppState;

/// Réponse d'erreur interne, partagée par les handlers du module.
pub(crate) fn internal() -> (StatusCode, Json<Value>) {
    (
        StatusCode::INTERNAL_SERVER_ERROR,
        Json(json!({ "error": "Internal error" })),
    )
}

/// Exécute une opération SQLite hors du runtime async, sur le pool blocking de Tokio.
pub(crate) async fn with_db<T, F>(state: &AppState, op: F) -> Option<rusqlite::Result<T>>
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

pub fn router() -> Router<AppState> {
    Router::new()
        .merge(health::router())
        .merge(metrics::router())
        .merge(templates::router())
        .merge(cloud::router())
        .merge(connect::router())
}
