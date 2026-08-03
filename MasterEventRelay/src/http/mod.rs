pub mod cloud;
pub mod connect;
pub mod health;
pub mod metrics;
pub mod templates;

use axum::Router;
use crate::state::AppState;

pub fn router() -> Router<AppState> {
    Router::new()
        .merge(health::router())
        .merge(metrics::router())
        .merge(templates::router())
        .merge(cloud::router())
        .merge(connect::router())
}
