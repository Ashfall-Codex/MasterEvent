use std::env;

/// Configuration du serveur relay, chargée depuis les variables d'environnement.
#[derive(Debug, Clone)]
pub struct Config {
    pub port: u16,
    pub host: String,
    pub room_expiry_ms: u64,
    pub template_expiry_ms: u64,
    pub log_level: String,
    pub db_path: String,
}

impl Config {
    pub fn from_env() -> Self {
        Self {
            port: env::var("PORT")
                .ok()
                .and_then(|v| v.parse().ok())
                .unwrap_or(8765),
            host: env::var("HOST").unwrap_or_else(|_| "0.0.0.0".into()),
            room_expiry_ms: env::var("ROOM_EXPIRY_MS")
                .ok()
                .and_then(|v| v.parse().ok())
                .unwrap_or(3_600_000),
            template_expiry_ms: env::var("TEMPLATE_EXPIRY_MS")
                .ok()
                .and_then(|v| v.parse().ok())
                .unwrap_or(7 * 24 * 3_600_000),
            log_level: env::var("LOG_LEVEL").unwrap_or_else(|_| "info".into()),
            db_path: env::var("DATABASE_PATH").unwrap_or_else(|_| "relay.db".into()),
        }
    }
}
