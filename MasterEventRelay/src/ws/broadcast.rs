use tracing::{error, info};

use crate::state::{AppState, Room};

// Envoie un message à tous les clients d'une room sauf l'émetteur.
// Met à jour le cache si nécessaire.
// Log le résultat de la transmission pour le diagnostic.
pub fn relay_to_room(room: &mut Room, exclude_id: u64, msg: &serde_json::Value) {
    room.last_activity = AppState::now_ms();

    let msg_type = msg
        .get("type")
        .and_then(|t| t.as_str())
        .unwrap_or("unknown");

    // Mise à jour du cache selon le type de message
    match msg_type {
        "update" => room.cached_state = Some(msg.clone()),
        "clear" => room.cached_state = None,
        _ => {}
    }

    let payload = match serde_json::to_string(msg) {
        Ok(p) => p,
        Err(e) => {
            error!("[relay] Failed to serialize {} message: {}", msg_type, e);
            return;
        }
    };

    // Identifier l'émetteur
    let sender_name = room
        .clients
        .get(&exclude_id)
        .map(|c| c.info.player_name.as_str())
        .unwrap_or("?");

    // Compter les destinataires (tous sauf l'émetteur)
    let recipients: Vec<u64> = room
        .clients
        .keys()
        .filter(|&&id| id != exclude_id)
        .copied()
        .collect();

    if recipients.is_empty() {
        info!(
            "[relay] {} from {} (client {}) — no other members in room, message not forwarded",
            msg_type, sender_name, exclude_id
        );
        return;
    }

    // Collecter les IDs des clients déconnectés pour nettoyage
    let mut delivered = 0u32;
    let mut failed = 0u32;
    let mut dead_clients = Vec::new();

    for &id in &recipients {
        if let Some(handle) = room.clients.get(&id) {
            if handle.sender.send(payload.clone()).is_ok() {
                delivered += 1;
            } else {
                failed += 1;
                dead_clients.push(id);
            }
        }
    }

    info!(
        "[relay] {} from {} (client {}) — delivered to {}/{} members{}",
        msg_type,
        sender_name,
        exclude_id,
        delivered,
        recipients.len(),
        if failed > 0 {
            format!(" ({} failed)", failed)
        } else {
            String::new()
        }
    );

    for id in dead_clients {
        room.clients.remove(&id);
    }
}
