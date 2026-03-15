use axum::extract::ws::{Message, WebSocket};
use futures_util::{SinkExt, StreamExt};
use tokio::sync::mpsc;
use tracing::{error, warn};

use crate::models::IncomingMessage;
use crate::state::AppState;
use crate::ws::broadcast::relay_to_room;
use crate::ws::handlers;

const WS_RATE_LIMIT: u32 = 30;
const WS_RATE_WINDOW_MS: u64 = 1000;

/// Gère une session WebSocket complète (read + write loops).
pub async fn handle_session(state: AppState, socket: WebSocket, client_id: u64) {
    let (mut ws_sink, mut ws_stream) = socket.split();
    let (tx, mut rx) = mpsc::unbounded_channel::<String>();

    // Tâche d'écriture : transmet les messages du channel vers le WebSocket
    let write_task = tokio::spawn(async move {
        while let Some(msg) = rx.recv().await {
            if ws_sink.send(Message::Text(msg.into())).await.is_err() {
                break;
            }
        }
    });

    // Boucle de lecture
    let mut current_room: Option<String> = None;
    let mut message_count: u32 = 0;
    let mut rate_window_start = AppState::now_ms();

    while let Some(result) = ws_stream.next().await {
        let raw = match result {
            Ok(Message::Text(t)) => t.to_string(),
            Ok(Message::Close(_)) => break,
            Err(e) => {
                error!("WebSocket error: {}", e);
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
            "update" | "clear" | "playerUpdate" | "templateShare" | "turnUpdate" | "turnClear" => {
                if let Some(ref room_key) = current_room {
                    if let Some(mut room) = state.rooms.get_mut(room_key) {
                        let authorized = room
                            .clients
                            .get(&client_id)
                            .map(|c| c.info.is_leader || c.info.is_promoted)
                            .unwrap_or(false);
                        if authorized {
                            relay_to_room(room.value_mut(), client_id, &raw_value);
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

    // Déconnexion involontaire : nettoyage
    handlers::handle_leave(&state, client_id, &mut current_room, false);

    write_task.abort();
}
