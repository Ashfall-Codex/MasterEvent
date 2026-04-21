use std::collections::HashMap;
use std::sync::atomic::AtomicU64;
use std::sync::Arc;
use std::time::{Duration, Instant};
use dashmap::DashMap;
use rusqlite::Connection;
use tokio::sync::{mpsc, Mutex};
use crate::config::Config;
use crate::models::ClientInfo;
use crate::rate_limit::RateLimiter;

/// Handle vers un client connecté : sender pour envoyer des messages + métadonnées.
#[derive(Debug, Clone)]
pub struct ClientHandle {
    pub sender: mpsc::UnboundedSender<String>,
    pub info: ClientInfo,
}

/// Représentation d'une room active.
#[derive(Debug)]
pub struct Room {
    pub clients: HashMap<u64, ClientHandle>,
    pub last_activity: u64,
    pub cached_state: Option<serde_json::Value>,
}

/// État global partagé entre tous les handlers.
#[derive(Clone)]
pub struct AppState {
    pub rooms: Arc<DashMap<String, Room>>,
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
}

impl AppState {
    pub fn new(db: Connection, config: Config) -> Self {
        Self {
            rooms: Arc::new(DashMap::new()),
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
        }
    }

    pub fn next_id(&self) -> u64 {
        self.next_client_id
            .fetch_add(1, std::sync::atomic::Ordering::Relaxed)
    }

    /// Timestamp courant en millisecondes.
    pub fn now_ms() -> u64 {
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_millis() as u64
    }
}
