use axum::extract::ws::{Message, WebSocket};
use futures_util::{SinkExt, StreamExt};
use tokio::sync::mpsc;
use tokio::time::{interval, Duration};
use tracing::{error, warn};

use crate::models::IncomingMessage;
use crate::state::AppState;
use crate::ws::broadcast::relay_to_room;
use crate::ws::handlers;

const WS_RATE_LIMIT: u32 = 30;
const WS_RATE_WINDOW_MS: u64 = 1000;
const PING_INTERVAL_SECS: u64 = 30;
pub async fn handle_session(state: AppState, socket: WebSocket, client_id: u64) {
    let (mut ws_sink, mut ws_stream) = socket.split();
    let (tx, mut rx) = mpsc::unbounded_channel::<String>();

    let mut current_room: Option<String> = None;
    let mut message_count: u32 = 0;
    let mut rate_window_start = AppState::now_ms();

    // Keepalive : ping périodique pour détecter les connexions mortes
    let mut ping_timer = interval(Duration::from_secs(PING_INTERVAL_SECS));
    ping_timer.tick().await; // Le premier tick est immédiat, on le skip
    let mut awaiting_pong = false;

    loop {
        tokio::select! {
            // Lecture des messages entrants depuis le client
            result = ws_stream.next() => {
                let raw = match result {
                    Some(Ok(Message::Text(t))) => t.to_string(),
                    Some(Ok(Message::Pong(_))) => {
                        awaiting_pong = false;
                        continue;
                    }
                    Some(Ok(Message::Close(_))) | None => break,
                    Some(Err(e)) => {
                        error!("WebSocket error for client {}: {}", client_id, e);
                        break;
                    }
                    _ => continue,
                };

                // Rate limiting
                let now = AppState::now_ms();
                if now - rate_window_start > WS_RATE_WINDOW_MS {
                    message_count = 0;
                    rate_window_start = now;
                }
                message_count += 1;
                if message_count > WS_RATE_LIMIT {
                    warn!("Rate limit exceeded for client {}", client_id);
                    continue;
                }

                // Parsing JSON
                let raw_value: serde_json::Value = match serde_json::from_str(&raw) {
                    Ok(v) => v,
                    Err(_) => continue,
                };

                let msg_type = match raw_value.get("type").and_then(|t| t.as_str()) {
                    Some(t) => t.to_string(),
                    None => continue,
                };

                let parsed: IncomingMessage = match serde_json::from_value(raw_value.clone()) {
                    Ok(m) => m,
                    Err(_) => continue,
                };

                match msg_type.as_str() {
                    "join" => {
                        handlers::handle_join(&state, client_id, &tx, &parsed, &mut current_room);
                    }
                    "leave" => {
                        handlers::handle_leave(&state, client_id, &mut current_room, true);
                    }
                    // Messages nécessitant le statut leader ou promu
                    "update" | "clear" | "playerUpdate" | "templateShare" | "turnUpdate" | "turnClear" | "weatherUpdate" | "timeUpdate" | "allianceKick" => {
                        if let Some(ref room_key) = current_room {
                            if let Some(mut room) = state.rooms.get_mut(room_key) {
                                let authorized = room
                                    .clients
                                    .get(&client_id)
                                    .map(|c| c.info.is_leader || c.info.is_promoted)
                                    .unwrap_or(false);
                                if authorized {
                                    relay_to_room(room.value_mut(), client_id, &raw_value);
                                } else {
                                    warn!("[relay] {} from client {} rejected — not leader/promoted in room {}", msg_type, client_id, room_key);
                                }
                            }
                        }
                    }
                    // Messages ouverts à tous
                    "requestUpdate" | "roll" | "statRoll" | "playerStatUpdate" => {
                        if let Some(ref room_key) = current_room {
                            if let Some(mut room) = state.rooms.get_mut(room_key) {
                                relay_to_room(room.value_mut(), client_id, &raw_value);
                            }
                        }
                    }
                    "promote" => {
                        handlers::handle_promote(&state, client_id, &current_room, &parsed, &raw_value);
                    }
                    _ => {}
                }
            }

            // Envoi des messages sortants (relayés depuis d'autres clients)
            msg = rx.recv() => {
                match msg {
                    Some(text) => {
                        if ws_sink.send(Message::Text(text.into())).await.is_err() {
                            break;
                        }
                    }
                    None => break,
                }
            }

            // Keepalive : envoi de ping périodique
            _ = ping_timer.tick() => {
                if awaiting_pong {
                    // Pas de pong reçu depuis le dernier ping → connexion morte
                    warn!("Client {} pong timeout, disconnecting", client_id);
                    break;
                }
                if ws_sink.send(Message::Ping(vec![].into())).await.is_err() {
                    break;
                }
                awaiting_pong = true;
            }
        }
    }

    // Nettoyage à la déconnexion
    handlers::handle_leave(&state, client_id, &mut current_room, false);
}
