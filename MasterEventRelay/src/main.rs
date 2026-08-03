mod accounts;
mod config;
mod connect_client;
mod db;
mod http;
mod models;
mod rate_limit;
mod state;
mod ws;

use std::net::SocketAddr;

use axum::http::{HeaderValue, Method};
use tokio::net::TcpListener;
use tower_http::cors::CorsLayer;
use tracing::{info, warn};
use tracing_appender::rolling::{RollingFileAppender, Rotation};
use tracing_subscriber::{fmt, layer::SubscriberExt, util::SubscriberInitExt, EnvFilter};

use config::Config;
use state::AppState;

#[tokio::main]
async fn main() {
    // Charger les variables d'environnement
    let _ = dotenvy::dotenv();
    let config = Config::from_env();

    // Initialiser le logging (console + fichier rotatif, rétention 7 jours)
    let file_appender = RollingFileAppender::builder()
        .rotation(Rotation::DAILY)
        .filename_prefix("relay")
        .filename_suffix("log")
        .max_log_files(7)
        .build(".")
        .expect("Impossible d'initialiser le log rotatif");
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
    accounts::init_schema(&conn).expect("Impossible d'initialiser le schéma des comptes");

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

    {
        let state = state.clone();
        let retention = config.tombstone_retention_ms;
        tokio::spawn(async move {
            let mut interval = tokio::time::interval(std::time::Duration::from_secs(6 * 3600));
            loop {
                interval.tick().await;
                cleanup_tombstones(&state, retention).await;
            }
        });
    }

    // Tâche périodique : nettoyage des buckets de rate limiting (toutes les 10 min)
    {
        let state = state.clone();
        tokio::spawn(async move {
            let mut interval = tokio::time::interval(std::time::Duration::from_secs(600));
            loop {
                interval.tick().await;
                state.conn_rate_limiter.cleanup();
                state.room_create_rate_limiter.cleanup();
                state.account_rate_limiter.cleanup();
                state.connect_rate_limiter.cleanup();
            }
        });
    }
    let cors = build_cors_layer(&config);

    // Clone conservé pour le hook de shutdown (state sera consommé par with_state)
    let shutdown_state = state.clone();

    // Construire le routeur
    let app = axum::Router::new()
        .merge(http::router())
        .merge(ws::router())
        .layer(cors)
        .with_state(state);

    let addr: SocketAddr = format!("{}:{}", config.host, config.port)
        .parse()
        .expect("Adresse invalide");

    info!("MasterEvent Relay listening on {}", addr);
    info!(
        "CORS origins autorisées : {:?}, MAX_ROOMS = {}",
        config.allowed_origins, config.max_rooms
    );

    let listener = TcpListener::bind(addr).await.expect("Impossible d'écouter sur le port");

    // Hook de shutdown : on notifie les sessions WS puis on accorde 2s de grâce pour
    // que les cleanup (handle_leave) se terminent avant le retour de axum::serve.
    let shutdown_hook = async move {
        shutdown_signal().await;
        info!("Signal d'arrêt reçu, notification des sessions WebSocket...");
        shutdown_state.shutdown_notify.notify_waiters();
        tokio::time::sleep(std::time::Duration::from_secs(2)).await;
        info!("Arrêt gracieux terminé");
    };

    // `into_make_service_with_connect_info` permet d'extraire l'IP du peer via ConnectInfo.
    axum::serve(
        listener,
        app.into_make_service_with_connect_info::<SocketAddr>(),
    )
    .with_graceful_shutdown(shutdown_hook)
    .await
    .expect("Erreur du serveur");
}

/// Attend Ctrl-C (SIGINT) ou SIGTERM (systemd / `pm2 stop`).
async fn shutdown_signal() {
    let ctrl_c = async {
        tokio::signal::ctrl_c()
            .await
            .expect("Impossible d'installer le handler Ctrl-C");
    };
    #[cfg(unix)]
    let terminate = async {
        tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())
            .expect("Impossible d'installer le handler SIGTERM")
            .recv()
            .await;
    };
    #[cfg(not(unix))]
    let terminate = std::future::pending::<()>();

    tokio::select! {
        _ = ctrl_c => {},
        _ = terminate => {},
    }
}

fn build_cors_layer(config: &Config) -> CorsLayer {
    let origins: Vec<HeaderValue> = config
        .allowed_origins
        .iter()
        .filter_map(|o| match HeaderValue::from_str(o) {
            Ok(v) => Some(v),
            Err(_) => {
                warn!("Origine invalide ignorée dans ALLOWED_ORIGINS : {}", o);
                None
            }
        })
        .collect();

    CorsLayer::new()
        .allow_origin(origins)
        // PUT/DELETE sont utilisés par la mise à jour de template et par le coffre cloud.
        .allow_methods([Method::GET, Method::POST, Method::PUT, Method::DELETE])
        .allow_headers(tower_http::cors::Any)
}

// Supprime les rooms inactives et ferme les connexions associées.
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

async fn cleanup_tombstones(state: &AppState, retention_ms: i64) {
    let db = state.db.clone();
    let result = tokio::task::spawn_blocking(move || {
        let conn = db.blocking_lock();
        accounts::purge_old_tombstones(&conn, retention_ms)
    })
    .await;

    match result {
        Ok(Ok(count)) if count > 0 => info!("{} tombstones de documents purgés", count),
        Ok(Err(e)) => tracing::error!("Purge des tombstones échouée : {}", e),
        _ => {}
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
