pub mod health;
pub mod templates;

use axum::Router;
use crate::state::AppState;

/// Construit le routeur HTTP (health + templates).
pub fn router() -> Router<AppState> {
    Router::new()
        .merge(health::router())
        .merge(templates::router())
}
