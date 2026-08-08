use serde::{Deserialize, Serialize};

/// Message entrant brut depuis un client WebSocket.
#[derive(Debug, Deserialize)]
pub struct IncomingMessage {
    #[serde(rename = "type")]
    #[allow(dead_code)]
    pub msg_type: String,
    #[serde(rename = "partyId")]
    pub party_id: Option<String>,
    #[serde(rename = "playerName")]
    pub player_name: Option<String>,
    #[serde(rename = "playerHash")]
    pub player_hash: Option<String>,
    #[serde(rename = "isLeader")]
    pub is_leader: Option<bool>,
    pub version: Option<String>,
    #[serde(rename = "targetHash")]
    pub target_hash: Option<String>,
    #[serde(rename = "canEdit")]
    pub can_edit: Option<bool>,
    #[serde(rename = "groupId")]
    pub group_id: Option<String>,
    #[serde(rename = "leaderToken")]
    pub leader_token: Option<String>,
    pub protocol: Option<u32>,
    #[serde(rename = "lobbyCode")]
    pub lobby_code: Option<String>,
    pub roster: Option<Vec<String>>,
}
pub const PROTOCOL_LEGACY: u32 = 1;
pub const PROTOCOL_LOBBY: u32 = 2;

/// Informations d'un client connecté dans une room.
#[derive(Debug, Clone)]
pub struct ClientInfo {
    pub player_name: String,
    pub player_hash: String,
    pub is_leader: bool,
    pub is_promoted: bool,
    pub version: String,
    #[allow(dead_code)]
    pub group_id: Option<String>,
    pub party_id: String,
    pub protocol: u32,
}

/// Confirmation d'adhésion à une room.
#[derive(Serialize)]
pub struct JoinConfirm {
    #[serde(rename = "type")]
    pub msg_type: &'static str,
    #[serde(rename = "roomKey")]
    pub room_key: String,
    #[serde(rename = "playerCount")]
    pub player_count: usize,
    #[serde(rename = "isLeader")]
    pub is_leader: bool,
}

/// Notification d'arrivée d'un joueur.
#[derive(Serialize)]
pub struct PlayerJoined {
    #[serde(rename = "type")]
    pub msg_type: &'static str,
    #[serde(rename = "playerName")]
    pub player_name: String,
    #[serde(rename = "playerHash")]
    pub player_hash: String,
    #[serde(rename = "playerCount")]
    pub player_count: usize,
    #[serde(rename = "groupId", skip_serializing_if = "Option::is_none")]
    pub group_id: Option<String>,
}

/// Notification de départ d'un joueur.
#[derive(Serialize)]
pub struct PlayerLeft {
    #[serde(rename = "type")]
    pub msg_type: &'static str,
    #[serde(rename = "playerName")]
    pub player_name: String,
    #[serde(rename = "playerHash")]
    pub player_hash: String,
    #[serde(rename = "playerCount")]
    pub player_count: usize,
    pub voluntary: bool,
}

/// Avertissement de différence de version.
#[derive(Serialize)]
pub struct VersionMismatch {
    #[serde(rename = "type")]
    pub msg_type: &'static str,
    #[serde(rename = "playerName")]
    pub player_name: String,
    pub version: String,
}

/// Rejet de connexion pour version trop ancienne.
#[derive(Serialize)]
pub struct VersionRejected {
    #[serde(rename = "type")]
    pub msg_type: &'static str,
    #[serde(rename = "minVersion")]
    pub min_version: String,
}

#[derive(Serialize)]
pub struct JoinRejected {
    #[serde(rename = "type")]
    pub msg_type: &'static str,
    /// Motif machine : `invalidRequest`, `roomLimit`, `rateLimited`, `denied`.
    pub reason: &'static str,
}

/// Adhésion mise en attente d'approbation par le MJ.
#[derive(Serialize)]
pub struct JoinPending {
    #[serde(rename = "type")]
    pub msg_type: &'static str,
    #[serde(rename = "roomKey")]
    pub room_key: String,
}

/// Approbation reçue : le client doit renvoyer son `join`, qui passera cette fois le contrôle
/// d'admission. Passer par un re-join évite d'avoir à muter l'état d'une autre session.
#[derive(Serialize)]
pub struct JoinAdmitted {
    #[serde(rename = "type")]
    pub msg_type: &'static str,
    #[serde(rename = "roomKey")]
    pub room_key: String,
}

/// Un membre en file d'attente, tel que présenté au MJ.
#[derive(Serialize, Clone)]
pub struct PendingMember {
    #[serde(rename = "playerName")]
    pub player_name: String,
    #[serde(rename = "playerHash")]
    pub player_hash: String,
    #[serde(rename = "groupId")]
    pub group_id: Option<String>,
}

/// État de la file d'admission, poussé au MJ à chaque changement.
#[derive(Serialize)]
pub struct LobbyPending {
    #[serde(rename = "type")]
    pub msg_type: &'static str,
    pub pending: Vec<PendingMember>,
}

#[derive(Serialize)]
pub struct LobbyMoved {
    #[serde(rename = "type")]
    pub msg_type: &'static str,
    #[serde(rename = "lobbyCode")]
    pub lobby_code: String,
}
