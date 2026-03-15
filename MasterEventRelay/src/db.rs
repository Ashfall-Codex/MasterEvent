use rusqlite::{Connection, params};
use rand::Rng;

const TEMPLATE_CODE_LENGTH: usize = 6;
const TEMPLATE_MAX_COUNT: usize = 10_000;
const CODE_CHARS: &[u8] = b"ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

/// Initialise le schéma SQLite.
pub fn init_db(conn: &Connection) -> rusqlite::Result<()> {
    conn.execute_batch(
        "CREATE TABLE IF NOT EXISTS templates (
            code       TEXT PRIMARY KEY,
            data       TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            permanent  INTEGER NOT NULL DEFAULT 0
        );",
    )?;
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
pub fn insert_template(
    conn: &Connection,
    data: &str,
    permanent: bool,
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
        "INSERT INTO templates (code, data, created_at, permanent) VALUES (?1, ?2, ?3, ?4)",
        params![code, data, now, permanent as i32],
    )?;
    Ok(code)
}

/// Récupère les données d'un template par code.
pub fn get_template(conn: &Connection, code: &str) -> rusqlite::Result<String> {
    conn.query_row(
        "SELECT data FROM templates WHERE code = ?1",
        params![code],
        |row| row.get::<_, String>(0),
    )
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
