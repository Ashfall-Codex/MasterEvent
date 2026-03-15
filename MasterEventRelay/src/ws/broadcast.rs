use crate::state::{AppState, Room};

/// Envoie un message à tous les clients d'une room sauf l'émetteur.
/// Met à jour le cache si nécessaire.
pub fn relay_to_room(room: &mut Room, exclude_id: u64, msg: &serde_json::Value) {
    room.last_activity = AppState::now_ms();

    // Mise à jour du cache selon le type de message
    if let Some(msg_type) = msg.get("type").and_then(|t| t.as_str()) {
        match msg_type {
            "update" => room.cached_state = Some(msg.clone()),
            "clear" => room.cached_state = None,
            _ => {}
        }
    }

    let payload = serde_json::to_string(msg).unwrap();

    // Collecter les IDs des clients déconnectés pour nettoyage
    let mut dead_clients = Vec::new();

    for (&id, handle) in &room.clients {
        if id != exclude_id && handle.sender.send(payload.clone()).is_err() {
            dead_clients.push(id);
        }
    }

    for id in dead_clients {
        room.clients.remove(&id);
    }
}
