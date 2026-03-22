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
}

/// Informations d'un client connecté dans une room.
#[derive(Debug, Clone)]
pub struct ClientInfo {
    pub player_name: String,
    pub player_hash: String,
    pub is_leader: bool,
    pub is_promoted: bool,
    pub version: String,
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
