use std::sync::atomic::Ordering;

use axum::{extract::State, http::header, response::IntoResponse, routing::get, Router};

use crate::db;
use crate::state::AppState;

async fn metrics(State(state): State<AppState>) -> impl IntoResponse {
    let active_sessions = state.rooms.len();
    let connected_clients: usize = state
        .rooms
        .iter()
        .map(|r| r.value().clients.len())
        .sum();
    let uptime_seconds = state.start_time.elapsed().as_secs();
    let messages_total = state.messages_total.load(Ordering::Relaxed);
    let errors_total = state.errors_total.load(Ordering::Relaxed);

    let db_handle = state.db.clone();
    let templates_total = tokio::task::spawn_blocking(move || {
        let conn = db_handle.blocking_lock();
        db::count(&conn).unwrap_or(0)
    })
    .await
    .unwrap_or(0);

    let body = format!(
        "# HELP masterevent_active_sessions Nombre de rooms actives (sessions GM).\n\
         # TYPE masterevent_active_sessions gauge\n\
         masterevent_active_sessions {active_sessions}\n\
         # HELP masterevent_connected_clients Nombre total de clients connectés toutes rooms confondues.\n\
         # TYPE masterevent_connected_clients gauge\n\
         masterevent_connected_clients {connected_clients}\n\
         # HELP masterevent_uptime_seconds Secondes écoulées depuis le démarrage du serveur.\n\
         # TYPE masterevent_uptime_seconds gauge\n\
         masterevent_uptime_seconds {uptime_seconds}\n\
         # HELP masterevent_templates_total Nombre de templates actuellement stockés en base.\n\
         # TYPE masterevent_templates_total gauge\n\
         masterevent_templates_total {templates_total}\n\
         # HELP masterevent_messages_total Compteur cumulé de messages WebSocket valides reçus.\n\
         # TYPE masterevent_messages_total counter\n\
         masterevent_messages_total {messages_total}\n\
         # HELP masterevent_errors_total Compteur cumulé d'erreurs de parsing/validation.\n\
         # TYPE masterevent_errors_total counter\n\
         masterevent_errors_total {errors_total}\n",
    );

    (
        [(header::CONTENT_TYPE, "text/plain; version=0.0.4; charset=utf-8")],
        body,
    )
}

pub fn router() -> Router<AppState> {
    Router::new().route("/metrics", get(metrics))
}
