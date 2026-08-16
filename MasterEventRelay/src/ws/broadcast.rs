use tracing::{error, info};

use crate::state::{AppState, Room};

// Envoie un message à tous les clients d'une room sauf l'émetteur.
// Met à jour le cache si nécessaire.
// Log le résultat de la transmission pour le diagnostic.
/// Diffuse la charge utile aux destinataires et retire les clients dont le canal est fermé.
/// Retourne (livrés, échecs).
fn fan_out(room: &mut Room, recipients: &[u64], payload: &str) -> (u32, u32) {
    let mut delivered = 0u32;
    let mut dead_clients = Vec::new();

    for &id in recipients {
        if let Some(handle) = room.clients.get(&id) {
            if handle.sender.send(payload.to_string()).is_ok() {
                delivered += 1;
            } else {
                dead_clients.push(id);
            }
        }
    }

    let failed = dead_clients.len() as u32;
    for id in dead_clients {
        room.clients.remove(&id);
    }

    (delivered, failed)
}

/// Suffixe de log rappelant le nombre de PNJ transportés par le message, s'il y en a.
fn npc_note(msg: &serde_json::Value) -> String {
    match msg.get("npcs").and_then(|n| n.as_array()) {
        Some(npcs) if !npcs.is_empty() => format!(" [{} PNJ]", npcs.len()),
        _ => String::new(),
    }
}

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
    // Copié plutôt qu'emprunté : la diffusion emprunte la room mutablement juste après.
    let sender_name = room
        .clients
        .get(&exclude_id)
        .map_or_else(|| "?".to_string(), |c| c.info.player_name.clone());

    let npc_note = npc_note(msg);

    // Compter les destinataires (tous sauf l'émetteur)
    let recipients: Vec<u64> = room
        .clients
        .keys()
        .filter(|&&id| id != exclude_id)
        .copied()
        .collect();

    if recipients.is_empty() {
        info!(
            "[relay] {} from {} (client {}){} — no other members in room, message not forwarded",
            msg_type, sender_name, exclude_id, npc_note
        );
        return;
    }

    let (delivered, failed) = fan_out(room, &recipients, &payload);

    info!(
        "[relay] {} from {} (client {}){} — delivered to {}/{} members{}",
        msg_type,
        sender_name,
        exclude_id,
        npc_note,
        delivered,
        recipients.len(),
        if failed > 0 {
            format!(" ({} failed)", failed)
        } else {
            String::new()
        }
    );
}
