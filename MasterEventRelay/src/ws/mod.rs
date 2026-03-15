pub mod broadcast;
pub mod handlers;
pub mod session;

use axum::{
    Router,
    extract::{State, WebSocketUpgrade},
    response::IntoResponse,
    routing::get,
};

use crate::state::AppState;

async fn ws_upgrade(
    ws: WebSocketUpgrade,
    State(state): State<AppState>,
) -> impl IntoResponse {
    let client_id = state.next_id();
    ws.max_message_size(256 * 1024)
        .on_upgrade(move |socket| session::handle_session(state, socket, client_id))
}

pub fn router() -> Router<AppState> {
    Router::new().route("/", get(ws_upgrade))
}
