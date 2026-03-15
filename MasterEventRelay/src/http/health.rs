use axum::{Router, routing::get, Json};
use serde_json::{json, Value};

async fn health() -> Json<Value> {
    Json(json!({ "status": "ok" }))
}

pub fn router() -> Router<crate::state::AppState> {
    Router::new().route("/health", get(health))
}
