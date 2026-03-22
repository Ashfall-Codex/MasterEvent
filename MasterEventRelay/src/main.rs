mod config;
mod db;
mod http;
mod models;
mod state;
mod ws;

use std::net::SocketAddr;

use tokio::net::TcpListener;
use tower_http::cors::CorsLayer;
use tracing::info;
use tracing_appender::rolling;
use tracing_subscriber::{fmt, layer::SubscriberExt, util::SubscriberInitExt, EnvFilter};

use config::Config;
use state::AppState;

#[tokio::main]
async fn main() {
    // Charger les variables d'environnement
    let _ = dotenvy::dotenv();
    let config = Config::from_env();

    // Initialiser le logging (console + fichier rotatif)
    let file_appender = rolling::never(".", "relay.log");
    let (non_blocking, _guard) = tracing_appender::non_blocking(file_appender);

    let filter = EnvFilter::try_new(&config.log_level).unwrap_or_else(|_| EnvFilter::new("info"));

    tracing_subscriber::registry()
        .with(filter)
        .with(fmt::layer().with_target(false))
        .with(fmt::layer().with_target(false).with_ansi(false).with_writer(non_blocking))
        .init();

    // Initialiser SQLite
    let conn = rusqlite::Connection::open(&config.db_path)
        .expect("Impossible d'ouvrir la base SQLite");
    db::init_db(&conn).expect("Impossible d'initialiser le schéma SQLite");

    let state = AppState::new(conn, config.clone());

    // Tâche périodique : nettoyage des rooms expirées (toutes les 5 min)
    {
        let state = state.clone();
        let expiry = config.room_expiry_ms;
        tokio::spawn(async move {
            let mut interval = tokio::time::interval(std::time::Duration::from_secs(5 * 60));
            loop {
                interval.tick().await;
                cleanup_rooms(&state, expiry);
            }
        });
    }

    // Tâche périodique : nettoyage des templates expirés (toutes les heures)
    {
        let state = state.clone();
        let expiry = config.template_expiry_ms;
        tokio::spawn(async move {
            let mut interval = tokio::time::interval(std::time::Duration::from_secs(3600));
            loop {
                interval.tick().await;
                cleanup_templates(&state, expiry).await;
            }
        });
    }

    // Construire le routeur
    let app = axum::Router::new()
        .merge(http::router())
        .merge(ws::router())
        .layer(CorsLayer::permissive())
        .with_state(state);

    let addr: SocketAddr = format!("{}:{}", config.host, config.port)
        .parse()
        .expect("Adresse invalide");

    info!("MasterEvent Relay listening on {}", addr);

    let listener = TcpListener::bind(addr).await.expect("Impossible d'écouter sur le port");
    axum::serve(listener, app).await.expect("Erreur du serveur");
}

/// Supprime les rooms inactives et ferme les connexions associées.
fn cleanup_rooms(state: &AppState, expiry_ms: u64) {
    let now = AppState::now_ms();
    let mut expired_keys = Vec::new();

    for entry in state.rooms.iter() {
        if now - entry.value().last_activity > expiry_ms {
            expired_keys.push(entry.key().clone());
        }
    }

    for key in expired_keys {
        if let Some((_, room)) = state.rooms.remove(&key) {
            // Les senders vont être droppés, ce qui fermera les write tasks
            // et donc les connexions WebSocket
            drop(room);
            info!("Room {} expired and cleaned up", key);
        }
    }
}

/// Supprime les templates non permanents expirés de la base.
async fn cleanup_templates(state: &AppState, expiry_ms: u64) {
    let db = state.db.clone();
    let result = tokio::task::spawn_blocking(move || {
        let conn = db.blocking_lock();
        db::cleanup_expired(&conn, expiry_ms)
    })
    .await;

    match result {
        Ok(Ok(count)) if count > 0 => {
            info!("{} expired templates cleaned up", count);
        }
        Ok(Err(e)) => {
            tracing::error!("Template cleanup error: {}", e);
        }
        _ => {}
    }
}
