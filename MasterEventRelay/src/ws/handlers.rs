use serde_json::json;
use tokio::sync::mpsc;
use tracing::{info, warn};

use crate::models::*;
use crate::state::*;
use crate::ws::broadcast::relay_to_room;

/// Gère l'adhésion d'un client à une room.
pub fn handle_join(
    state: &AppState,
    client_id: u64,
    sender: &mpsc::UnboundedSender<String>,
    msg: &IncomingMessage,
    current_room: &mut Option<String>,
) {
    let party_id = match &msg.party_id {
        Some(id) if !id.is_empty() && id.len() <= 64 => id.clone(),
        _ => return,
    };
    let player_name = match &msg.player_name {
        Some(n) if !n.is_empty() && n.len() <= 128 => n.clone(),
        _ => return,
    };

    let hash = msg
        .player_hash
        .as_deref()
        .unwrap_or("anon")
        .chars()
        .take(32)
        .collect::<String>();

    let client_version = msg.version.clone().unwrap_or_else(|| "0".into());
    let wants_leader = msg.is_leader.unwrap_or(false);

    // Quitter la room précédente si nécessaire
    handle_leave(state, client_id, current_room, true);

    let room_key = party_id;

    // Créer ou récupérer la room
    let mut room_entry = state.rooms.entry(room_key.clone()).or_insert_with(|| Room {
        clients: std::collections::HashMap::new(),
        last_activity: AppState::now_ms(),
        cached_state: None,
    });
    let room = room_entry.value_mut();

    // Leadership : accordé seulement si demandé ET aucun leader existant
    let existing_leader = room.clients.values().any(|c| c.info.is_leader);
    let grant_leader = wants_leader && !existing_leader;

    let info = ClientInfo {
        player_name: player_name.clone(),
        player_hash: hash.clone(),
        is_leader: grant_leader,
        is_promoted: false,
        version: client_version.clone(),
    };

    let handle = ClientHandle {
        sender: sender.clone(),
        info: info.clone(),
    };

    room.clients.insert(client_id, handle);
    room.last_activity = AppState::now_ms();

    let player_count = room.clients.len();

    // Vérification de version
    for (id, other) in &room.clients {
        if *id != client_id && other.info.version != client_version {
            let mismatch = VersionMismatch {
                msg_type: "versionMismatch",
                player_name: other.info.player_name.clone(),
                version: other.info.version.clone(),
            };
            let _ = sender.send(serde_json::to_string(&mismatch).unwrap());
            warn!(
                "Version mismatch: {} (v{}) vs {} (v{}) in room {}",
                hash, client_version, other.info.player_hash, other.info.version, room_key
            );
            break;
        }
    }

    // Notifier les autres joueurs de l'arrivée
    let joined_msg = serde_json::to_string(&PlayerJoined {
        msg_type: "playerJoined",
        player_name: player_name.clone(),
        player_hash: hash.clone(),
        player_count,
    })
    .unwrap();

    for (&id, other) in &room.clients {
        if id != client_id {
            let _ = other.sender.send(joined_msg.clone());
        }
    }

    // Envoi de l'état en cache si disponible
    if let Some(cached) = &room.cached_state {
        if grant_leader {
            // Leader qui se reconnecte : envoyer comme cachedState
            let mut state_msg = cached.clone();
            state_msg["type"] = json!("cachedState");
            let _ = sender.send(serde_json::to_string(&state_msg).unwrap());
        } else {
            // Joueur sans leader présent : envoyer comme update
            let has_leader = room
                .clients
                .iter()
                .any(|(id, c)| *id != client_id && c.info.is_leader);
            if !has_leader {
                let mut state_msg = cached.clone();
                state_msg["type"] = json!("update");
                let _ = sender.send(serde_json::to_string(&state_msg).unwrap());
            }
        }
    }

    // Confirmation
    let confirm = JoinConfirm {
        msg_type: "joinConfirm",
        room_key: room_key.clone(),
        player_count,
        is_leader: grant_leader,
    };
    let _ = sender.send(serde_json::to_string(&confirm).unwrap());

    *current_room = Some(room_key.clone());

    info!(
        "{} joined room {} ({} members, leader: {}, v{})",
        hash, room_key, player_count, grant_leader, client_version
    );
}

/// Gère le départ d'un client.
pub fn handle_leave(
    state: &AppState,
    client_id: u64,
    current_room: &mut Option<String>,
    voluntary: bool,
) {
    let room_key = match current_room.take() {
        Some(k) => k,
        None => return,
    };

    let mut should_remove = false;

    if let Some(mut room_entry) = state.rooms.get_mut(&room_key) {
        let room = room_entry.value_mut();
        let info = room.clients.remove(&client_id);

        let player_name = info
            .as_ref()
            .map(|i| i.info.player_name.clone())
            .unwrap_or_else(|| "?".into());
        let player_hash = info
            .as_ref()
            .map(|i| i.info.player_hash.clone())
            .unwrap_or_else(|| "?".into());
        let remaining = room.clients.len();

        // Notifier les clients restants
        let left_msg = serde_json::to_string(&PlayerLeft {
            msg_type: "playerLeft",
            player_name,
            player_hash: player_hash.clone(),
            player_count: remaining,
        })
        .unwrap();

        let mut dead = Vec::new();
        for (&id, handle) in &room.clients {
            if handle.sender.send(left_msg.clone()).is_err() {
                dead.push(id);
            }
        }
        for id in dead {
            room.clients.remove(&id);
        }

        let remaining = room.clients.len();

        info!(
            "{} left room {} ({} remaining, voluntary: {})",
            player_hash, room_key, remaining, voluntary
        );

        if remaining == 0 {
            if !voluntary && room.cached_state.is_some() {
                info!("Room {} empty but keeping cached state for crash recovery", room_key);
            } else {
                should_remove = true;
            }
        }
    }

    if should_remove {
        state.rooms.remove(&room_key);
        info!("Room {} deleted (empty)", room_key);
    }
}

/// Gère la promotion/dégradation d'un joueur.
pub fn handle_promote(
    state: &AppState,
    client_id: u64,
    current_room: &Option<String>,
    msg: &IncomingMessage,
    raw_msg: &serde_json::Value,
) {
    let room_key = match current_room {
        Some(k) => k,
        None => return,
    };

    if let Some(mut room_entry) = state.rooms.get_mut(room_key) {
        let room = room_entry.value_mut();

        // Vérifier que l'émetteur est leader
        let is_leader = room
            .clients
            .get(&client_id)
            .map(|c| c.info.is_leader)
            .unwrap_or(false);

        if !is_leader {
            return;
        }

        // Mettre à jour le statut du joueur ciblé
        let target_hash = msg
            .target_hash
            .as_deref()
            .map(|h| h.chars().take(32).collect::<String>());

        if let Some(ref target) = target_hash {
            for handle in room.clients.values_mut() {
                if handle.info.player_hash == *target {
                    handle.info.is_promoted = msg.can_edit.unwrap_or(false);
                    info!(
                        "Player {} {} in room {}",
                        target,
                        if handle.info.is_promoted { "promoted" } else { "demoted" },
                        room_key
                    );
                    break;
                }
            }
        }

        // Relayer le message
        relay_to_room(room, client_id, raw_msg);
    }
}
