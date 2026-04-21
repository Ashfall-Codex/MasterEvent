use std::sync::Arc;
use std::time::{Duration, Instant};

use dashmap::DashMap;

#[derive(Clone)]
pub struct RateLimiter {
    buckets: Arc<DashMap<String, (u32, Instant)>>,
    window: Duration,
    max: u32,
    name: &'static str,
}

impl RateLimiter {
    pub fn new(name: &'static str, max: u32, window: Duration) -> Self {
        Self {
            buckets: Arc::new(DashMap::new()),
            window,
            max,
            name,
        }
    }

    pub fn check(&self, key: &str) -> bool {
        let now = Instant::now();
        let mut entry = self
            .buckets
            .entry(key.to_string())
            .or_insert_with(|| (0, now));

        if now.duration_since(entry.1) > self.window {
            *entry = (1, now);
            true
        } else if entry.0 < self.max {
            entry.0 += 1;
            true
        } else {
            false
        }
    }

    pub fn cleanup(&self) {
        let now = Instant::now();
        let window = self.window;
        let mut removed = 0usize;
        self.buckets.retain(|_, (_, start)| {
            let keep = now.duration_since(*start) <= window * 2;
            if !keep {
                removed += 1;
            }
            keep
        });
        if removed > 0 {
            tracing::debug!(
                "[rate_limit:{}] cleaned up {} stale buckets, {} remaining",
                self.name,
                removed,
                self.buckets.len()
            );
        }
    }

    #[allow(dead_code)]
    pub fn name(&self) -> &'static str {
        self.name
    }
}
