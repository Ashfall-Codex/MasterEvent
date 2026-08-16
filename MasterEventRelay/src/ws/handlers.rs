use std::collections::HashSet;

use serde_json::json;
use sha2::{Digest, Sha256};
use tokio::sync::mpsc;
use tracing::{error, info, warn};
use crate::models::*;
use crate::state::*;
use crate::ws::broadcast::relay_to_room;

/// Plafond de la file d'attente, pour qu'un flot de demandes ne fasse pas enfler une room.
const MAX_PENDING: usize = 32;
const MAX_ROSTER: usize = 64;

fn hash_token(token: &str) -> [u8; 32] {
    let mut hasher = Sha256::new();
    hasher.update(token.as_bytes());
    hasher.finalize().into()
}

fn send_json<T: serde::Serialize>(sender: &mpsc::UnboundedSender<String>, payload: &T) {
    if let Ok(text) = serde_json::to_string(payload) {
        let _ = sender.send(text);
    }
}

fn reject(sender: &mpsc::UnboundedSender<String>, reason: &'static str) {
    send_json(
        sender,
        &JoinRejected {
            msg_type: "joinRejected",
            reason,
        },
    );
}

fn target_hash(msg: &IncomingMessage) -> Option<String> {
    msg.target_hash
        .as_deref()
        .map(|h| h.chars().take(32).collect())
}

fn is_moderator(room: &Room, client_id: u64) -> bool {
    room.clients
        .get(&client_id)
        .map(|c| c.info.is_leader || c.info.is_promoted)
        .unwrap_or(false)
}

/// Normalise une liste de hashes reçue du client.
fn sanitize_roster(raw: Option<&Vec<String>>) -> HashSet<String> {
    raw.into_iter()
        .flatten()
        .map(|h| h.chars().take(32).collect::<String>())
        .filter(|h| !h.is_empty())
        .take(MAX_ROSTER)
        .collect()
}

fn resolve_room_key(state: &AppState, msg: &IncomingMessage, party_id: &str) -> String {
    if let Some(code) = msg.lobby_code.as_deref() {
        let code = code.trim().to_uppercase();
        if !code.is_empty() && code.len() <= 64 {
            return code;
        }
    }

    if let Some(target) = state.lobby_index.get(party_id) {
        return target.clone();
    }

    party_id.to_string()
}

fn broadcast_pending(room: &mut Room) {
    room.pending.retain(|p| !p.sender.is_closed());

    let payload = LobbyPending {
        msg_type: "lobbyPending",
        pending: room
            .pending
            .iter()
            .map(|p| PendingMember {
                player_name: p.player_name.clone(),
                player_hash: p.player_hash.clone(),
                group_id: p.party_id.clone(),
            })
            .collect(),
    };

    let text = match serde_json::to_string(&payload) {
        Ok(t) => t,
        Err(e) => {
            error!("[lobby] sérialisation de lobbyPending impossible : {}", e);
            return;
        }
    };

    for handle in room.clients.values() {
        if handle.info.is_leader || handle.info.is_promoted {
            let _ = handle.sender.send(text.clone());
        }
    }
}

/// Champs d'adhésion validés et normalisés, extraits du message entrant.
struct JoinRequest {
    party_id: String,
    player_name: String,
    hash: String,
    client_version: String,
    wants_leader: bool,
    protocol: u32,
    roster: HashSet<String>,
    group_id: Option<String>,
}

/// Valide le message d'adhésion. Retourne None après avoir répondu au client
/// quand un champ obligatoire manque ou dépasse les bornes.
fn parse_join_request(
    msg: &IncomingMessage,
    sender: &mpsc::UnboundedSender<String>,
) -> Option<JoinRequest> {
    let party_id = match &msg.party_id {
        Some(id) if !id.is_empty() && id.len() <= 64 => id.clone(),
        _ => {
            reject(sender, "invalidRequest");
            return None;
        }
    };

    let player_name = match &msg.player_name {
        Some(n) if !n.is_empty() && n.len() <= 128 => n.clone(),
        _ => {
            reject(sender, "invalidRequest");
            return None;
        }
    };

    Some(JoinRequest {
        party_id,
        player_name,
        hash: msg
            .player_hash
            .as_deref()
            .unwrap_or("anon")
            .chars()
            .take(32)
            .collect(),
        client_version: msg.version.clone().unwrap_or_else(|| "0".into()),
        wants_leader: msg.is_leader.unwrap_or(false),
        protocol: msg.protocol.unwrap_or(PROTOCOL_LEGACY),
        roster: sanitize_roster(msg.roster.as_ref()),
        group_id: msg.group_id.clone(),
    })
}

/// Place l'arrivant dans la file d'attente d'une room déjà établie qui ne le couvre pas.
/// Retourne true quand la demande a été mise en attente et que l'adhésion s'arrête là.
fn try_enqueue_pending(
    state: &AppState,
    sender: &mpsc::UnboundedSender<String>,
    msg: &IncomingMessage,
    room_key: &str,
    req: &JoinRequest,
) -> bool {
    if req.protocol < PROTOCOL_LOBBY {
        return false;
    }

    let Some(mut room_entry) = state.rooms.get_mut(room_key) else {
        return false;
    };
    let room = room_entry.value_mut();

    let established = !room.entries.is_empty();
    let covered = room.entries.contains_key(&req.party_id) || room.is_covered(&req.hash);
    let owns_room = req.wants_leader
        && match (
            room.leader_token_hash,
            msg.leader_token.as_deref().map(hash_token),
        ) {
            // Salle pas encore verrouillée : la revendication reste ouverte.
            (None, _) => true,
            (Some(stored), Some(provided)) => stored == provided,
            (Some(_), None) => false,
        };

    if !established || covered || owns_room {
        return false;
    }

    room.pending.retain(|p| p.player_hash != req.hash);
    if room.pending.len() >= MAX_PENDING {
        room.pending.remove(0);
    }
    room.pending.push(PendingJoin {
        player_name: req.player_name.clone(),
        player_hash: req.hash.clone(),
        party_id: Some(req.party_id.clone()),
        roster: req.roster.clone(),
        sender: sender.clone(),
    });

    send_json(
        sender,
        &JoinPending {
            msg_type: "joinPending",
            room_key: room_key.to_string(),
        },
    );
    broadcast_pending(room);

    info!(
        "{} en attente d'approbation pour le lobby {} (party {})",
        req.hash, room_key, req.party_id
    );
    true
}

/// Enregistre le client dans la room et retourne l'effectif après insertion.
fn register_client(
    room: &mut Room,
    client_id: u64,
    sender: &mpsc::UnboundedSender<String>,
    req: &JoinRequest,
    grant_leader: bool,
) -> usize {
    room.clients.insert(
        client_id,
        ClientHandle {
            sender: sender.clone(),
            info: ClientInfo {
                player_name: req.player_name.clone(),
                player_hash: req.hash.clone(),
                is_leader: grant_leader,
                is_promoted: false,
                version: req.client_version.clone(),
                group_id: req.group_id.clone(),
                party_id: req.party_id.clone(),
                protocol: req.protocol,
            },
        },
    );
    room.last_activity = AppState::now_ms();
    room.clients.len()
}

/// Annonce l'arrivée du joueur à tous les autres clients de la room.
fn notify_player_joined(
    room: &Room,
    client_id: u64,
    req: &JoinRequest,
    player_count: usize,
    room_key: &str,
) {
    let payload = PlayerJoined {
        msg_type: "playerJoined",
        player_name: req.player_name.clone(),
        player_hash: req.hash.clone(),
        player_count,
        group_id: req.group_id.clone(),
    };

    let Ok(joined_msg) = serde_json::to_string(&payload) else {
        error!("Failed to serialize PlayerJoined for room {}", room_key);
        return;
    };

    for (&id, other) in &room.clients {
        if id != client_id {
            let _ = other.sender.send(joined_msg.clone());
        }
    }
}

/// Rattache le sous-groupe au lobby et indexe la correspondance party -> room.
/// Retourne true quand l'index a changé et qu'il faut prévenir le sous-groupe.
fn attach_party_to_lobby(
    state: &AppState,
    room: &mut Room,
    req: &JoinRequest,
    room_key: &str,
) -> bool {
    if req.protocol < PROTOCOL_LOBBY || req.roster.is_empty() {
        return false;
    }

    room.attach_party(&req.party_id, req.roster.clone());
    if room_key == req.party_id {
        return false;
    }

    state
        .lobby_index
        .insert(req.party_id.clone(), room_key.to_string());
    true
}

/// Messages envoyés à l'arrivant et à la room une fois l'adhésion acquise.
fn announce_join(
    room: &Room,
    client_id: u64,
    sender: &mpsc::UnboundedSender<String>,
    req: &JoinRequest,
    grant_leader: bool,
    player_count: usize,
    room_key: &str,
) {
    warn_version_mismatch(
        room,
        client_id,
        sender,
        &req.hash,
        &req.client_version,
        room_key,
    );
    notify_player_joined(room, client_id, req, player_count, room_key);
    send_cached_state(room, client_id, sender, grant_leader);

    send_json(
        sender,
        &JoinConfirm {
            msg_type: "joinConfirm",
            room_key: room_key.to_string(),
            player_count,
            is_leader: grant_leader,
        },
    );
}

/// Refuse la connexion si le client est plus ancien que la version minimale exigée.
/// Retourne true quand l'adhésion peut continuer.
fn accepts_version(
    state: &AppState,
    sender: &mpsc::UnboundedSender<String>,
    hash: &str,
    client_version: &str,
) -> bool {
    let min_ver = &state.config.min_version;
    if min_ver.is_empty() || compare_versions(client_version, min_ver) >= 0 {
        return true;
    }

    send_json(
        sender,
        &VersionRejected {
            msg_type: "versionRejected",
            min_version: min_ver.clone(),
        },
    );
    warn!(
        "Version rejected: {} (v{}) < min v{} — connexion refusée",
        hash, client_version, min_ver
    );
    false
}

/// Plafond global de rooms et quota de création par IP, vérifiés avant toute création.
fn may_create_room(
    state: &AppState,
    sender: &mpsc::UnboundedSender<String>,
    room_key: &str,
    client_ip: &str,
) -> bool {
    if state.rooms.len() >= state.config.max_rooms {
        warn!(
            "Création de room '{}' refusée : plafond global atteint ({}), IP {}",
            room_key, state.config.max_rooms, client_ip
        );
        reject(sender, "roomLimit");
        return false;
    }

    if !state.room_create_rate_limiter.check(client_ip) {
        warn!(
            "Création de room '{}' refusée : IP {} a atteint le quota (5/heure)",
            room_key, client_ip
        );
        reject(sender, "rateLimited");
        return false;
    }

    true
}

/// Décide si le client obtient le leadership. La première revendication avec token
/// verrouille la room ; ensuite seul ce token rouvre le droit.
fn resolve_leadership(
    room: &mut Room,
    room_key: &str,
    hash: &str,
    wants_leader: bool,
    provided: Option<[u8; 32]>,
) -> bool {
    if !wants_leader || room.clients.values().any(|c| c.info.is_leader) {
        return false;
    }

    match (room.leader_token_hash, provided) {
        (Some(stored), Some(p)) if stored == p => true,
        (Some(_), Some(_)) => {
            warn!(
                "Leadership refusé pour '{}' : token invalide (hash {} ne correspond pas)",
                room_key, hash
            );
            false
        }
        (Some(_), None) => {
            warn!(
                "Leadership refusé pour '{}' : token requis mais absent (hash {})",
                room_key, hash
            );
            false
        }
        (None, Some(p)) => {
            // Première revendication de leadership : le token fourni verrouille la room.
            room.leader_token_hash = Some(p);
            info!(
                "Leadership initialisé pour '{}' par {} (token enregistré)",
                room_key, hash
            );
            true
        }
        (None, None) => {
            // Legacy : aucun token disponible côté client. Grant sans verrouillage.
            warn!(
                "Leadership accordé à {} pour '{}' sans token (client legacy — room non verrouillée)",
                hash, room_key
            );
            true
        }
    }
}

/// Prévient l'arrivant qu'un autre client de la room tourne sur une version différente.
fn warn_version_mismatch(
    room: &Room,
    client_id: u64,
    sender: &mpsc::UnboundedSender<String>,
    hash: &str,
    client_version: &str,
    room_key: &str,
) {
    for (id, other) in &room.clients {
        if *id == client_id || other.info.version == client_version {
            continue;
        }

        send_json(
            sender,
            &VersionMismatch {
                msg_type: "versionMismatch",
                player_name: other.info.player_name.clone(),
                version: other.info.version.clone(),
            },
        );
        warn!(
            "Version mismatch: {} (v{}) vs {} (v{}) in room {}",
            hash, client_version, other.info.player_hash, other.info.version, room_key
        );
        break;
    }
}

/// Envoie l'état mis en cache à l'arrivant : en `cachedState` pour un MJ qui se reconnecte,
/// en `update` pour un joueur qui rejoint une room sans MJ présent.
fn send_cached_state(
    room: &Room,
    client_id: u64,
    sender: &mpsc::UnboundedSender<String>,
    grant_leader: bool,
) {
    let Some(cached) = &room.cached_state else {
        return;
    };

    if !grant_leader {
        let has_leader = room
            .clients
            .iter()
            .any(|(id, c)| *id != client_id && c.info.is_leader);
        if has_leader {
            return;
        }
    }

    let mut state_msg = cached.clone();
    state_msg["type"] = json!(if grant_leader { "cachedState" } else { "update" });
    if let Ok(payload) = serde_json::to_string(&state_msg) {
        let _ = sender.send(payload);
    }
}

// Gère l'adhésion d'un client à une room.
pub fn handle_join(
    state: &AppState,
    client_id: u64,
    client_ip: &str,
    sender: &mpsc::UnboundedSender<String>,
    msg: &IncomingMessage,
    current_room: &mut Option<String>,
) {
    let Some(req) = parse_join_request(msg, sender) else {
        return;
    };

    if !accepts_version(state, sender, &req.hash, &req.client_version) {
        return;
    }

    // Quitter la room précédente si nécessaire
    handle_leave(state, client_id, current_room, true);

    let room_key = resolve_room_key(state, msg, &req.party_id);

    // Room déjà établie qui ne couvre pas l'arrivant : il passe par la file d'attente.
    if try_enqueue_pending(state, sender, msg, &room_key, &req) {
        return;
    }

    // Le plafond global et le quota par IP ne s'appliquent qu'à la création.
    if !state.rooms.contains_key(&room_key) && !may_create_room(state, sender, &room_key, client_ip)
    {
        return;
    }

    let mut room_entry = state
        .rooms
        .entry(room_key.clone())
        .or_insert_with(Room::new);
    let room = room_entry.value_mut();

    let provided_token_hash = msg.leader_token.as_deref().map(hash_token);
    let grant_leader = resolve_leadership(
        room,
        &room_key,
        &req.hash,
        req.wants_leader,
        provided_token_hash,
    );

    let player_count = register_client(room, client_id, sender, &req, grant_leader);
    let party_attached = attach_party_to_lobby(state, room, &req, &room_key);

    announce_join(
        room,
        client_id,
        sender,
        &req,
        grant_leader,
        player_count,
        &room_key,
    );

    if grant_leader && !room.pending.is_empty() {
        broadcast_pending(room);
    }

    drop(room_entry);

    if party_attached {
        notify_lobby_moved(state, &req.party_id, &room_key);
    }

    *current_room = Some(room_key.clone());

    info!(
        "{} joined room {} ({} members, leader: {}, v{})",
        req.hash, room_key, player_count, grant_leader, req.client_version
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
        let left_payload = PlayerLeft {
            msg_type: "playerLeft",
            player_name,
            player_hash: player_hash.clone(),
            player_count: remaining,
            voluntary,
        };
        if let Ok(left_msg) = serde_json::to_string(&left_payload) {
            let mut dead = Vec::new();
            for (&id, handle) in &room.clients {
                if handle.sender.send(left_msg.clone()).is_err() {
                    dead.push(id);
                }
            }
            for id in dead {
                room.clients.remove(&id);
            }
        } else {
            error!("Failed to serialize PlayerLeft for room {}", room_key);
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
        state.purge_lobby_index(&room_key);
        info!("Room {} deleted (empty)", room_key);
    }
}

fn notify_lobby_moved(state: &AppState, party_room: &str, lobby_code: &str) {
    let room_entry = match state.rooms.get(party_room) {
        Some(r) => r,
        None => return,
    };
    let room = room_entry.value();
    if room.clients.is_empty() {
        return;
    }

    let payload = LobbyMoved {
        msg_type: "lobbyMoved",
        lobby_code: lobby_code.to_string(),
    };
    let text = match serde_json::to_string(&payload) {
        Ok(t) => t,
        Err(e) => {
            error!("[lobby] sérialisation de lobbyMoved impossible : {}", e);
            return;
        }
    };

    for handle in room.clients.values() {
        let _ = handle.sender.send(text.clone());
    }

    info!(
        "[lobby] {} client(s) de la room {} redirigés vers {}",
        room.clients.len(),
        party_room,
        lobby_code
    );
}

/// Retire de la file les demandes désormais couvertes par un sous-groupe rattaché,
/// et les retourne pour notification.
fn release_covered_pending(room: &mut Room) -> Vec<PendingJoin> {
    let mut kept = Vec::with_capacity(room.pending.len());
    let mut freed = Vec::new();

    for p in std::mem::take(&mut room.pending) {
        let covered = p
            .party_id
            .as_deref()
            .map(|id| room.entries.contains_key(id))
            .unwrap_or(false)
            || room.is_covered(&p.player_hash);

        if covered {
            freed.push(p);
        } else {
            kept.push(p);
        }
    }

    room.pending = kept;
    freed
}

/// Rattache le sous-groupe de l'admis, pour que ses coéquipiers entrent sans nouvelle demande.
fn attach_admitted_party(
    state: &AppState,
    room: &mut Room,
    approved: &PendingJoin,
    room_key: &str,
) {
    let Some(party_id) = approved.party_id.as_ref() else {
        return;
    };

    let mut roster = approved.roster.clone();
    roster.insert(approved.player_hash.clone());
    room.attach_party(party_id, roster);

    if party_id != room_key {
        state
            .lobby_index
            .insert(party_id.clone(), room_key.to_string());
    }
}

pub fn handle_admit(
    state: &AppState,
    client_id: u64,
    current_room: &Option<String>,
    msg: &IncomingMessage,
) {
    let room_key = match current_room {
        Some(k) => k,
        None => return,
    };
    let target = match target_hash(msg) {
        Some(h) => h,
        None => return,
    };

    let mut room_entry = match state.rooms.get_mut(room_key) {
        Some(r) => r,
        None => return,
    };
    let room = room_entry.value_mut();

    if !is_moderator(room, client_id) {
        warn!("[lobby] admit refusé : client {} n'est ni MJ ni promu", client_id);
        return;
    }

    let index = match room.pending.iter().position(|p| p.player_hash == target) {
        Some(i) => i,
        None => return,
    };
    let approved = room.pending.remove(index);

    attach_admitted_party(state, room, &approved, room_key);

    send_json(
        &approved.sender,
        &JoinAdmitted {
            msg_type: "joinAdmitted",
            room_key: room_key.clone(),
        },
    );
    let released = release_covered_pending(room);

    for p in &released {
        send_json(
            &p.sender,
            &JoinAdmitted {
                msg_type: "joinAdmitted",
                room_key: room_key.clone(),
            },
        );
    }

    broadcast_pending(room);

    info!(
        "[lobby] {} approuvé pour {} (party {:?}), {} demande(s) du même groupe libérée(s)",
        approved.player_hash,
        room_key,
        approved.party_id,
        released.len()
    );
}

pub fn handle_deny(
    state: &AppState,
    client_id: u64,
    current_room: &Option<String>,
    msg: &IncomingMessage,
) {
    let room_key = match current_room {
        Some(k) => k,
        None => return,
    };
    let target = match target_hash(msg) {
        Some(h) => h,
        None => return,
    };

    let mut room_entry = match state.rooms.get_mut(room_key) {
        Some(r) => r,
        None => return,
    };
    let room = room_entry.value_mut();

    if !is_moderator(room, client_id) {
        return;
    }

    if let Some(index) = room.pending.iter().position(|p| p.player_hash == target) {
        let denied = room.pending.remove(index);
        reject(&denied.sender, "denied");
        broadcast_pending(room);
        info!("[lobby] {} refusé pour {}", denied.player_hash, room_key);
    }
}

pub fn handle_roster_update(
    state: &AppState,
    client_id: u64,
    current_room: &Option<String>,
    msg: &IncomingMessage,
) {
    let room_key = match current_room {
        Some(k) => k,
        None => return,
    };

    let mut room_entry = match state.rooms.get_mut(room_key) {
        Some(r) => r,
        None => return,
    };
    let room = room_entry.value_mut();

    let own_party = match room.clients.get(&client_id) {
        Some(handle) if handle.info.protocol >= PROTOCOL_LOBBY => handle.info.party_id.clone(),
        _ => return,
    };

    // Une party non rattachée ne se crée pas par ce chemin : seul `join` établit un lobby.
    if !room.entries.contains_key(&own_party) {
        return;
    }

    let roster = sanitize_roster(msg.roster.as_ref());
    if roster.is_empty() {
        return;
    }

    room.attach_party(&own_party, roster);
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

/// Compare deux versions au format "major.minor.patch".
/// Retourne < 0 si a < b, 0 si a == b, > 0 si a > b.
fn compare_versions(a: &str, b: &str) -> i32 {
    let parse = |s: &str| -> Vec<u32> {
        s.split('.').filter_map(|p| p.parse().ok()).collect()
    };
    let va = parse(a);
    let vb = parse(b);
    let len = va.len().max(vb.len());
    for i in 0..len {
        let pa = va.get(i).copied().unwrap_or(0);
        let pb = vb.get(i).copied().unwrap_or(0);
        if pa != pb {
            return if pa < pb { -1 } else { 1 };
        }
    }
    0
}
