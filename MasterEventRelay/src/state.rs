use std::collections::HashMap;
use std::sync::Arc;

use dashmap::DashMap;
use rusqlite::Connection;
use tokio::sync::{mpsc, Mutex};

use crate::config::Config;
use crate::models::ClientInfo;

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
    #[allow(dead_code)]
    pub config: Config,
    pub next_client_id: Arc<std::sync::atomic::AtomicU64>,
}

impl AppState {
    pub fn new(db: Connection, config: Config) -> Self {
        Self {
            rooms: Arc::new(DashMap::new()),
            db: Arc::new(Mutex::new(db)),
            config,
            next_client_id: Arc::new(std::sync::atomic::AtomicU64::new(1)),
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
