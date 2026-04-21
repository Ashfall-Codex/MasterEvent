use axum::{extract::State, routing::get, Json, Router};
use serde_json::{json, Value};

use crate::state::AppState;

async fn health(State(state): State<AppState>) -> Json<Value> {
    let active_sessions = state.rooms.len();
    Json(json!({
        "status": "ok",
        "activeSessions": active_sessions,
    }))
}

pub fn router() -> Router<AppState> {
    Router::new().route("/health", get(health))
}
