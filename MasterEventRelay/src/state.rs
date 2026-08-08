use std::collections::{HashMap, HashSet};
use std::sync::atomic::AtomicU64;
use std::sync::Arc;
use std::time::{Duration, Instant};
use dashmap::DashMap;
use rusqlite::Connection;
use tokio::sync::{mpsc, Mutex, Notify};
use crate::config::Config;
use crate::connect_client::ConnectClient;
use crate::models::ClientInfo;
use crate::rate_limit::RateLimiter;

/// Handle vers un client connecté : sender pour envoyer des messages + métadonnées.
#[derive(Debug, Clone)]
pub struct ClientHandle {
    pub sender: mpsc::UnboundedSender<String>,
    pub info: ClientInfo,
}

#[derive(Debug, Clone, Default)]
pub struct PartyEntry {
    pub roster: HashSet<String>,
}

#[derive(Debug)]
pub struct PendingJoin {
    pub player_name: String,
    pub player_hash: String,
    pub party_id: Option<String>,
    pub roster: HashSet<String>,
    pub sender: mpsc::UnboundedSender<String>,
}

/// Représentation d'une room active.
#[derive(Debug)]
pub struct Room {
    pub clients: HashMap<u64, ClientHandle>,
    pub last_activity: u64,
    pub cached_state: Option<serde_json::Value>,
    pub leader_token_hash: Option<[u8; 32]>,
    pub entries: HashMap<String, PartyEntry>,
    pub pending: Vec<PendingJoin>,
}

impl Room {
    pub fn new() -> Self {
        Self {
            clients: HashMap::new(),
            last_activity: AppState::now_ms(),
            cached_state: None,
            leader_token_hash: None,
            entries: HashMap::new(),
            pending: Vec::new(),
        }
    }

    pub fn is_covered(&self, player_hash: &str) -> bool {
        self.entries
            .values()
            .any(|entry| entry.roster.contains(player_hash))
    }

    pub fn attach_party(&mut self, party_id: &str, roster: HashSet<String>) {
        self.entries
            .entry(party_id.to_string())
            .or_default()
            .roster
            .extend(roster);
    }
}

/// État global partagé entre tous les handlers.
#[derive(Clone)]
pub struct AppState {
    pub rooms: Arc<DashMap<String, Room>>,
    pub lobby_index: Arc<DashMap<String, String>>,
    pub db: Arc<Mutex<Connection>>,
    pub config: Config,
    pub next_client_id: Arc<AtomicU64>,
    // Instant de démarrage du serveur (pour uptime dans /metrics)
    pub start_time: Instant,
    // Compteur total de messages WebSocket reçus (monotone)
    pub messages_total: Arc<AtomicU64>,
    // Compteur total d'erreurs de sérialisation/traitement (monotone)
    pub errors_total: Arc<AtomicU64>,
    // Rate limiter sur les upgrades WebSocket par IP (10/min).
    pub conn_rate_limiter: RateLimiter,
    // Rate limiter sur la création de nouvelles rooms par IP (5/h).
    pub room_create_rate_limiter: RateLimiter,
    // Rate limiter sur l'enregistrement de comptes MasterEvent par IP (10/h).
    pub account_rate_limiter: RateLimiter,
    // Rate limiter sur la génération de codes de liaison Connect par IP (10/h).
    pub connect_rate_limiter: RateLimiter,
    // Client sortant vers Ashfall Connect.
    pub connect: Arc<ConnectClient>,
    // Notifié au shutdown pour permettre aux sessions WS de se fermer proprement.
    pub shutdown_notify: Arc<Notify>,
}

impl AppState {
    pub fn new(db: Connection, config: Config) -> Self {
        let connect = Arc::new(ConnectClient::new(&config));
        Self {
            rooms: Arc::new(DashMap::new()),
            lobby_index: Arc::new(DashMap::new()),
            db: Arc::new(Mutex::new(db)),
            config,
            next_client_id: Arc::new(AtomicU64::new(1)),
            start_time: Instant::now(),
            messages_total: Arc::new(AtomicU64::new(0)),
            errors_total: Arc::new(AtomicU64::new(0)),
            conn_rate_limiter: RateLimiter::new("ws_connect", 10, Duration::from_secs(60)),
            room_create_rate_limiter: RateLimiter::new(
                "room_create",
                5,
                Duration::from_secs(3600),
            ),
            account_rate_limiter: RateLimiter::new("account_register", 10, Duration::from_secs(3600)),
            connect_rate_limiter: RateLimiter::new("connect_link", 10, Duration::from_secs(3600)),
            connect,
            shutdown_notify: Arc::new(Notify::new()),
        }
    }

    pub fn next_id(&self) -> u64 {
        self.next_client_id
            .fetch_add(1, std::sync::atomic::Ordering::Relaxed)
    }

    pub fn purge_lobby_index(&self, room_key: &str) {
        self.lobby_index.retain(|_, target| target != room_key);
    }

    /// Timestamp courant en millisecondes.
    pub fn now_ms() -> u64 {
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_millis() as u64
    }
}
