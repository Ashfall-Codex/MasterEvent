use std::env;

/// Configuration du serveur relay, chargée depuis les variables d'environnement.
/// `Debug` est implémenté à la main pour que les secrets Connect ne puissent pas
/// se retrouver dans un log par simple `{:?}`.
#[derive(Clone)]
pub struct Config {
    pub port: u16,
    pub host: String,
    pub room_expiry_ms: u64,
    pub template_expiry_ms: u64,
    pub log_level: String,
    pub db_path: String,
    pub min_version: String,
    pub max_rooms: usize,
    pub allowed_origins: Vec<String>,
    pub connect_base_url: String,
    pub connect_service_token: String,
    pub connect_incoming_token: String,
    pub tombstone_retention_ms: i64,
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
            min_version: env::var("MIN_VERSION").unwrap_or_default(),
            max_rooms: env::var("MAX_ROOMS")
                .ok()
                .and_then(|v| v.parse().ok())
                .unwrap_or(1000),
            allowed_origins: env::var("ALLOWED_ORIGINS")
                .unwrap_or_else(|_| "https://masterevent.ashfall-codex.dev".into())
                .split(',')
                .map(|s| s.trim().to_string())
                .filter(|s| !s.is_empty())
                .collect(),
            connect_base_url: env::var("CONNECT_BASE_URL").unwrap_or_default(),
            connect_service_token: env::var("CONNECT_SERVICE_TOKEN").unwrap_or_default(),
            connect_incoming_token: env::var("CONNECT_INCOMING_TOKEN").unwrap_or_default(),
            tombstone_retention_ms: env::var("TOMBSTONE_RETENTION_MS")
                .ok()
                .and_then(|v| v.parse().ok())
                .unwrap_or(30 * 24 * 3_600_000),
        }
    }
}

impl std::fmt::Debug for Config {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Config")
            .field("port", &self.port)
            .field("host", &self.host)
            .field("room_expiry_ms", &self.room_expiry_ms)
            .field("template_expiry_ms", &self.template_expiry_ms)
            .field("log_level", &self.log_level)
            .field("db_path", &self.db_path)
            .field("min_version", &self.min_version)
            .field("max_rooms", &self.max_rooms)
            .field("allowed_origins", &self.allowed_origins)
            .field("connect_base_url", &self.connect_base_url)
            .field("connect_service_token", &redacted(&self.connect_service_token))
            .field("connect_incoming_token", &redacted(&self.connect_incoming_token))
            .field("tombstone_retention_ms", &self.tombstone_retention_ms)
            .finish()
    }
}

fn redacted(secret: &str) -> &'static str {
    if secret.is_empty() {
        "<vide>"
    } else {
        "<masqué>"
    }
}
