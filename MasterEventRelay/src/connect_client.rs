use serde_json::{json, Value};

use crate::config::Config;

const SERVICE_NAME: &str = "master-event";

#[derive(Clone)]
pub struct ConnectClient {
    http: reqwest::Client,
    base_url: String,
    service_token: String,
}

/// Erreurs remontées telles quelles au plugin pour qu'il affiche un message utile.
pub enum ConnectError {
    /// Connect n'est pas configuré sur ce relay.
    NotConfigured,
    /// Connect est injoignable ou répond en erreur.
    Unreachable,
}

impl ConnectClient {
    pub fn new(config: &Config) -> Self {
        let http = reqwest::Client::builder()
            .timeout(std::time::Duration::from_secs(10))
            .user_agent(concat!("MasterEventRelay/", env!("CARGO_PKG_VERSION")))
            .build()
            .expect("Impossible de construire le client HTTP vers Connect");

        Self {
            http,
            base_url: config.connect_base_url.trim_end_matches('/').to_string(),
            service_token: config.connect_service_token.clone(),
        }
    }

    pub fn is_configured(&self) -> bool {
        !self.base_url.is_empty() && !self.service_token.is_empty()
    }

    /// Demande à Connect un code de liaison à 8 caractères pour cet identifier.
    /// Aucune metadata n'est transmise : Connect n'accepte qu'un schéma `characters`,
    /// qui n'a pas de sens pour MasterEvent.
    pub async fn create_link_code(
        &self,
        identifier: &str,
        alias: Option<&str>,
    ) -> Result<Value, ConnectError> {
        if !self.is_configured() {
            return Err(ConnectError::NotConfigured);
        }

        let res = self
            .http
            .post(format!("{}/api/v1/link-codes", self.base_url))
            .bearer_auth(&self.service_token)
            .json(&json!({ "identifier": identifier, "alias": alias }))
            .send()
            .await
            .map_err(|e| {
                tracing::warn!("Connect injoignable (create_link_code) : {}", e);
                ConnectError::Unreachable
            })?;

        if !res.status().is_success() {
            tracing::warn!(
                "Connect a refusé la génération de code (HTTP {})",
                res.status().as_u16()
            );
            return Err(ConnectError::Unreachable);
        }

        res.json::<Value>().await.map_err(|e| {
            tracing::warn!("Réponse illisible de Connect (create_link_code) : {}", e);
            ConnectError::Unreachable
        })
    }

    /// État d'un code : `pending` / `consumed` / `expired` / `not_found`.
    /// Sert au polling du plugin pendant que l'utilisateur colle son code sur le site.
    pub async fn get_link_code_status(&self, code: &str) -> Result<Value, ConnectError> {
        if !self.is_configured() {
            return Err(ConnectError::NotConfigured);
        }

        let res = self
            .http
            .get(format!(
                "{}/api/v1/link-codes/{}/status",
                self.base_url,
                urlencode(code)
            ))
            .bearer_auth(&self.service_token)
            .send()
            .await
            .map_err(|e| {
                tracing::warn!("Connect injoignable (link status) : {}", e);
                ConnectError::Unreachable
            })?;

        // 404 = code inconnu : c'est une réponse métier valide, pas une panne.
        if res.status() == reqwest::StatusCode::NOT_FOUND {
            return Ok(json!({ "status": "not_found" }));
        }
        if !res.status().is_success() {
            return Err(ConnectError::Unreachable);
        }

        res.json::<Value>().await.map_err(|_| ConnectError::Unreachable)
    }

    /// Badge de vérification du compte : permet au plugin d'afficher « lié à … ».
    pub async fn get_verification(&self, identifier: &str) -> Result<Value, ConnectError> {
        if !self.is_configured() {
            return Err(ConnectError::NotConfigured);
        }

        let res = self
            .http
            .get(format!(
                "{}/api/v1/verification/{}/{}",
                self.base_url,
                SERVICE_NAME,
                urlencode(identifier)
            ))
            .bearer_auth(&self.service_token)
            .send()
            .await
            .map_err(|e| {
                tracing::warn!("Connect injoignable (verification) : {}", e);
                ConnectError::Unreachable
            })?;

        if !res.status().is_success() {
            return Err(ConnectError::Unreachable);
        }

        res.json::<Value>().await.map_err(|_| ConnectError::Unreachable)
    }
}

/// Encodage minimal des segments de path. Les codes et identifiers sont alphanumériques,
/// mais on ne fait pas confiance à une entrée externe pour construire une URL.
fn urlencode(input: &str) -> String {
    input
        .bytes()
        .map(|b| match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                (b as char).to_string()
            }
            _ => format!("%{b:02X}"),
        })
        .collect()
}
