pub mod broadcast;
pub mod handlers;
pub mod session;

use std::net::SocketAddr;

use axum::{
    Router,
    extract::{ConnectInfo, State, WebSocketUpgrade},
    http::{header, HeaderMap, StatusCode},
    response::IntoResponse,
    routing::get,
};
use tracing::warn;

use crate::state::AppState;

fn extract_client_ip(headers: &HeaderMap, peer: SocketAddr) -> String {
    if let Some(xff) = headers.get("x-forwarded-for") {
        if let Ok(s) = xff.to_str() {
            if let Some(first) = s.split(',').next() {
                let trimmed = first.trim();
                if !trimmed.is_empty() {
                    return trimmed.to_string();
                }
            }
        }
    }
    peer.ip().to_string()
}

fn origin_allowed(headers: &HeaderMap, allowed: &[String]) -> bool {
    match headers.get(header::ORIGIN) {
        None => true,
        Some(value) => match value.to_str() {
            Ok(s) => allowed.iter().any(|o| o == s),
            Err(_) => false,
        },
    }
}

async fn ws_upgrade(
    ws: WebSocketUpgrade,
    State(state): State<AppState>,
    ConnectInfo(peer): ConnectInfo<SocketAddr>,
    headers: HeaderMap,
) -> axum::response::Response {
    // Validation de l'Origin (si fourni par le client)
    if !origin_allowed(&headers, &state.config.allowed_origins) {
        let origin = headers
            .get(header::ORIGIN)
            .and_then(|v| v.to_str().ok())
            .unwrap_or("?");
        warn!("WS upgrade rejeté : Origin non autorisée '{}'", origin);
        return (StatusCode::FORBIDDEN, "Forbidden origin").into_response();
    }

    // Rate limit par IP sur les upgrades
    let client_ip = extract_client_ip(&headers, peer);
    if !state.conn_rate_limiter.check(&client_ip) {
        warn!("WS upgrade rate-limited pour l'IP {}", client_ip);
        return (StatusCode::TOO_MANY_REQUESTS, "Too many connections").into_response();
    }

    let client_id = state.next_id();
    ws.max_message_size(256 * 1024)
        .on_upgrade(move |socket| session::handle_session(state, socket, client_id, client_ip))
        .into_response()
}

pub fn router() -> Router<AppState> {
    Router::new().route("/", get(ws_upgrade))
}
