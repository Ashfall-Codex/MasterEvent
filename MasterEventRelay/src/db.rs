use rusqlite::{Connection, params};
use rand::Rng;

const TEMPLATE_CODE_LENGTH: usize = 6;
const TEMPLATE_MAX_COUNT: usize = 10_000;
const CODE_CHARS: &[u8] = b"ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

/// Initialise le schéma SQLite.
/// Migration: ajoute version + creator_token_hash sur les bases existantes.
pub fn init_db(conn: &Connection) -> rusqlite::Result<()> {
    conn.execute_batch(
        "CREATE TABLE IF NOT EXISTS templates (
            code                TEXT PRIMARY KEY,
            data                TEXT NOT NULL,
            created_at          INTEGER NOT NULL,
            permanent           INTEGER NOT NULL DEFAULT 0,
            version             INTEGER NOT NULL DEFAULT 1,
            creator_token_hash  BLOB
        );",
    )?;

    // Migration idempotente pour les installations existantes (avant ajout des colonnes)
    let has_version: bool = conn
        .query_row(
            "SELECT 1 FROM pragma_table_info('templates') WHERE name = 'version'",
            [],
            |_| Ok(true),
        )
        .unwrap_or(false);
    if !has_version {
        conn.execute_batch(
            "ALTER TABLE templates ADD COLUMN version INTEGER NOT NULL DEFAULT 1;
             ALTER TABLE templates ADD COLUMN creator_token_hash BLOB;",
        )?;
    }
    Ok(())
}

/// Nombre total de templates en base.
pub fn count(conn: &Connection) -> rusqlite::Result<usize> {
    conn.query_row("SELECT COUNT(*) FROM templates", [], |row| row.get::<_, usize>(0))
}

/// Génère un code unique de 6 caractères.
fn generate_code(conn: &Connection) -> String {
    let mut rng = rand::thread_rng();
    loop {
        let code: String = (0..TEMPLATE_CODE_LENGTH)
            .map(|_| CODE_CHARS[rng.gen_range(0..CODE_CHARS.len())] as char)
            .collect();
        let exists: bool = conn
            .query_row(
                "SELECT EXISTS(SELECT 1 FROM templates WHERE code = ?1)",
                params![code],
                |row| row.get(0),
            )
            .unwrap_or(false);
        if !exists {
            return code;
        }
    }
}

/// Insère un template et retourne le code généré.
/// `creator_token_hash` : SHA-256 du LeaderToken du créateur (pour autoriser les PUT ultérieurs).
pub fn insert_template(
    conn: &Connection,
    data: &str,
    permanent: bool,
    creator_token_hash: Option<&[u8]>,
) -> rusqlite::Result<String> {
    let n = count(conn)?;
    if n >= TEMPLATE_MAX_COUNT {
        return Err(rusqlite::Error::QueryReturnedNoRows); // sera intercepté par le handler
    }
    let code = generate_code(conn);
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap()
        .as_millis() as i64;
    conn.execute(
        "INSERT INTO templates (code, data, created_at, permanent, version, creator_token_hash)
         VALUES (?1, ?2, ?3, ?4, 1, ?5)",
        params![code, data, now, permanent as i32, creator_token_hash],
    )?;
    Ok(code)
}

/// Résultat d'un template : données + version actuelle.
pub struct TemplateRecord {
    pub data: String,
    pub version: i64,
}

/// Récupère les données complètes d'un template (data + version).
pub fn get_template(conn: &Connection, code: &str) -> rusqlite::Result<TemplateRecord> {
    conn.query_row(
        "SELECT data, version FROM templates WHERE code = ?1",
        params![code],
        |row| {
            Ok(TemplateRecord {
                data: row.get::<_, String>(0)?,
                version: row.get::<_, i64>(1)?,
            })
        },
    )
}

/// Récupère juste la version (endpoint léger pour vérif de mise à jour).
pub fn get_version(conn: &Connection, code: &str) -> rusqlite::Result<i64> {
    conn.query_row(
        "SELECT version FROM templates WHERE code = ?1",
        params![code],
        |row| row.get::<_, i64>(0),
    )
}

/// Résultat d'une tentative de mise à jour de template.
pub enum UpdateResult {
    /// Succès : nouvelle version après incrément.
    Updated(i64),
    /// Template introuvable.
    NotFound,
    /// Le hash du token ne correspond pas au créateur.
    Forbidden,
}

/// Met à jour un template existant. Le hash du token doit correspondre au créateur original.
/// Incrémente automatiquement la version.
pub fn update_template(
    conn: &Connection,
    code: &str,
    new_data: &str,
    requester_token_hash: &[u8],
) -> rusqlite::Result<UpdateResult> {
    // Récupérer le hash du créateur (None = pas de créateur stocké, template pre-migration).
    let stored_hash: Option<Vec<u8>> = match conn.query_row(
        "SELECT creator_token_hash FROM templates WHERE code = ?1",
        params![code],
        |row| row.get::<_, Option<Vec<u8>>>(0),
    ) {
        Ok(h) => h,
        Err(rusqlite::Error::QueryReturnedNoRows) => return Ok(UpdateResult::NotFound),
        Err(e) => return Err(e),
    };

    // Vérification d'autorisation : le hash stocké doit matcher le hash du requester.
    match stored_hash {
        Some(stored) if stored.as_slice() == requester_token_hash => {}
        _ => return Ok(UpdateResult::Forbidden),
    }

    // Incrément de version + replace des données.
    let new_version: i64 = conn.query_row(
        "UPDATE templates SET data = ?1, version = version + 1 WHERE code = ?2
         RETURNING version",
        params![new_data, code],
        |row| row.get::<_, i64>(0),
    )?;
    Ok(UpdateResult::Updated(new_version))
}

/// Supprime les templates non permanents expirés.
pub fn cleanup_expired(conn: &Connection, expiry_ms: u64) -> rusqlite::Result<usize> {
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap()
        .as_millis() as i64;
    let cutoff = now - expiry_ms as i64;
    conn.execute(
        "DELETE FROM templates WHERE permanent = 0 AND created_at < ?1",
        params![cutoff],
    )
}
