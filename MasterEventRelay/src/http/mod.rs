pub mod health;
pub mod metrics;
pub mod templates;

use axum::Router;
use crate::state::AppState;

/// Construit le routeur HTTP (health + metrics + templates).
pub fn router() -> Router<AppState> {
    Router::new()
        .merge(health::router())
        .merge(metrics::router())
        .merge(templates::router())
}
