use rand::Rng;
use rusqlite::{params, Connection, OptionalExtension};

/// Alphabet sans caractères ambigus (pas de 0/O/1/I), identique à celui des codes de template.
const ID_CHARS: &[u8] = b"ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
const ID_LENGTH: usize = 8;
const HEX_CHARS: &[u8] = b"0123456789abcdef";

/// Plafonds par compte : borne la place qu'un seul joueur peut occuper dans la base.
pub const MAX_DOCUMENTS_PER_ACCOUNT: usize = 200;
pub const MAX_DOCUMENT_SIZE: usize = 64 * 1024;

/// Types de documents synchronisables. Volontairement fermé : tout autre `kind` est rejeté
/// avant d'atteindre la base.
pub const KIND_TEMPLATE: &str = "template";
pub const KIND_SHEET: &str = "sheet";
pub const KIND_NOTE: &str = "note";

pub fn is_valid_kind(kind: &str) -> bool {
    kind == KIND_TEMPLATE || kind == KIND_SHEET || kind == KIND_NOTE
}

/// Initialise le schéma des comptes MasterEvent et de leur coffre de documents.
/// Idempotent : appelé à chaque démarrage, comme `db::init_db`.
pub fn init_schema(conn: &Connection) -> rusqlite::Result<()> {
    conn.execute_batch(
        "CREATE TABLE IF NOT EXISTS me_account (
            id            TEXT PRIMARY KEY,
            token_hash    BLOB NOT NULL UNIQUE,
            alias         TEXT,
            created_at    INTEGER NOT NULL,
            last_seen_at  INTEGER NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS idx_me_account_token
            ON me_account(token_hash);

        CREATE TABLE IF NOT EXISTS me_document (
            id          TEXT PRIMARY KEY,
            account_id  TEXT NOT NULL,
            kind        TEXT NOT NULL,
            name        TEXT NOT NULL,
            data        TEXT NOT NULL,
            version     INTEGER NOT NULL DEFAULT 1,
            updated_at  INTEGER NOT NULL,
            deleted_at  INTEGER,
            UNIQUE(account_id, kind, name)
        );

        CREATE INDEX IF NOT EXISTS idx_me_document_account
            ON me_document(account_id, updated_at);",
    )
}

pub fn now_ms() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap()
        .as_millis() as i64
}

/// Identifiant public d'un compte : `ME-XXXXXXXX`. Opaque, non devinable depuis le LeaderToken.
fn generate_account_id(conn: &Connection) -> String {
    let mut rng = rand::thread_rng();
    loop {
        let suffix: String = (0..ID_LENGTH)
            .map(|_| ID_CHARS[rng.gen_range(0..ID_CHARS.len())] as char)
            .collect();
        let id = format!("ME-{suffix}");
        let exists: bool = conn
            .query_row(
                "SELECT EXISTS(SELECT 1 FROM me_account WHERE id = ?1)",
                params![id],
                |row| row.get(0),
            )
            .unwrap_or(false);
        if !exists {
            return id;
        }
    }
}

/// Identifiant technique d'un document (32 hex). Évite une dépendance uuid pour ce seul usage.
fn generate_document_id() -> String {
    let mut rng = rand::thread_rng();
    (0..32)
        .map(|_| HEX_CHARS[rng.gen_range(0..HEX_CHARS.len())] as char)
        .collect()
}

/// Un identifier bien formé, à valider avant toute requête paramétrée venant de l'extérieur.
pub fn is_valid_account_id(id: &str) -> bool {
    id.len() == ID_LENGTH + 3
        && id.starts_with("ME-")
        && id[3..].bytes().all(|b| ID_CHARS.contains(&b))
}

pub struct Account {
    pub id: String,
    pub alias: Option<String>,
    pub created_at: i64,
}

/// Retrouve le compte associé à ce LeaderToken, ou le crée à la première venue.
/// Le token lui-même n'est jamais stocké : seul son SHA-256 arrive ici.
pub fn get_or_create_by_token_hash(
    conn: &Connection,
    token_hash: &[u8],
    alias: Option<&str>,
) -> rusqlite::Result<Account> {
    let now = now_ms();

    let existing: Option<(String, Option<String>, i64)> = conn
        .query_row(
            "SELECT id, alias, created_at FROM me_account WHERE token_hash = ?1",
            params![token_hash],
            |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?)),
        )
        .optional()?;

    if let Some((id, stored_alias, created_at)) = existing {
        // L'alias suit ce que le plugin déclare, mais un alias vide ne doit pas écraser l'existant.
        let next_alias = match alias {
            Some(a) if !a.is_empty() => Some(a.to_string()),
            _ => stored_alias,
        };
        conn.execute(
            "UPDATE me_account SET last_seen_at = ?1, alias = ?2 WHERE id = ?3",
            params![now, next_alias, id],
        )?;
        return Ok(Account {
            id,
            alias: next_alias,
            created_at,
        });
    }

    let id = generate_account_id(conn);
    conn.execute(
        "INSERT INTO me_account (id, token_hash, alias, created_at, last_seen_at)
         VALUES (?1, ?2, ?3, ?4, ?4)",
        params![id, token_hash, alias, now],
    )?;
    Ok(Account {
        id: id.clone(),
        alias: alias.map(str::to_string),
        created_at: now,
    })
}

/// Résout un compte depuis son identifier public (chemin Connect → relay).
pub fn find_by_id(conn: &Connection, id: &str) -> rusqlite::Result<Option<Account>> {
    conn.query_row(
        "SELECT id, alias, created_at FROM me_account WHERE id = ?1",
        params![id],
        |row| {
            Ok(Account {
                id: row.get(0)?,
                alias: row.get(1)?,
                created_at: row.get(2)?,
            })
        },
    )
    .optional()
}

/// Compte les documents vivants, par type. Sert à la fois au quota et à la metadata Connect.
pub fn count_documents(conn: &Connection, account_id: &str, kind: &str) -> rusqlite::Result<usize> {
    conn.query_row(
        "SELECT COUNT(*) FROM me_document
         WHERE account_id = ?1 AND kind = ?2 AND deleted_at IS NULL",
        params![account_id, kind],
        |row| row.get::<_, usize>(0),
    )
}

fn count_all_documents(conn: &Connection, account_id: &str) -> rusqlite::Result<usize> {
    conn.query_row(
        "SELECT COUNT(*) FROM me_document WHERE account_id = ?1 AND deleted_at IS NULL",
        params![account_id],
        |row| row.get::<_, usize>(0),
    )
}

#[derive(Debug)]
pub struct Document {
    pub id: String,
    pub kind: String,
    pub name: String,
    pub data: Option<String>,
    pub version: i64,
    pub updated_at: i64,
    pub deleted: bool,
}

/// Liste les documents d'un compte.
/// `since` : ne renvoie que ce qui a bougé depuis cet instant (synchro incrémentale du plugin).
/// `include_deleted` : ajoute les tombstones, indispensables pour propager les suppressions.
/// `with_data` : à false, on ne remonte que les métadonnées (listing côté site).
pub fn list_documents(
    conn: &Connection,
    account_id: &str,
    kind: Option<&str>,
    since: i64,
    include_deleted: bool,
    with_data: bool,
) -> rusqlite::Result<Vec<Document>> {
    let mut sql = String::from(
        "SELECT id, kind, name, data, version, updated_at, deleted_at
         FROM me_document WHERE account_id = ?1 AND updated_at > ?2",
    );
    if !include_deleted {
        sql.push_str(" AND deleted_at IS NULL");
    }
    if kind.is_some() {
        sql.push_str(" AND kind = ?3");
    }
    sql.push_str(" ORDER BY kind, name");

    let mut stmt = conn.prepare(&sql)?;
    let map_row = |row: &rusqlite::Row| -> rusqlite::Result<Document> {
        let deleted_at: Option<i64> = row.get(6)?;
        Ok(Document {
            id: row.get(0)?,
            kind: row.get(1)?,
            name: row.get(2)?,
            data: if with_data { Some(row.get(3)?) } else { None },
            version: row.get(4)?,
            updated_at: row.get(5)?,
            deleted: deleted_at.is_some(),
        })
    };

    let rows = match kind {
        Some(k) => stmt
            .query_map(params![account_id, since, k], map_row)?
            .collect::<rusqlite::Result<Vec<_>>>()?,
        None => stmt
            .query_map(params![account_id, since], map_row)?
            .collect::<rusqlite::Result<Vec<_>>>()?,
    };
    Ok(rows)
}

pub fn get_document_by_id(
    conn: &Connection,
    account_id: &str,
    doc_id: &str,
) -> rusqlite::Result<Option<Document>> {
    conn.query_row(
        "SELECT id, kind, name, data, version, updated_at, deleted_at
         FROM me_document WHERE account_id = ?1 AND id = ?2 AND deleted_at IS NULL",
        params![account_id, doc_id],
        |row| {
            Ok(Document {
                id: row.get(0)?,
                kind: row.get(1)?,
                name: row.get(2)?,
                data: Some(row.get(3)?),
                version: row.get(4)?,
                updated_at: row.get(5)?,
                deleted: false,
            })
        },
    )
    .optional()
}

pub enum UpsertOutcome {
    Saved(Document),
    /// Quota de documents atteint : refus d'insérer un document supplémentaire.
    QuotaExceeded,
}

/// Crée ou met à jour le document (account_id, kind, name).
/// Un document précédemment supprimé est ressuscité : la clé métier reste le nom.
/// La version est incrémentée à chaque écriture, ce qui permet au plugin comme au site
/// de détecter une divergence sans comparer les contenus.
pub fn upsert_document(
    conn: &Connection,
    account_id: &str,
    kind: &str,
    name: &str,
    data: &str,
) -> rusqlite::Result<UpsertOutcome> {
    let now = now_ms();

    let existing: Option<(String, i64, bool)> = conn
        .query_row(
            "SELECT id, version, deleted_at IS NOT NULL FROM me_document
             WHERE account_id = ?1 AND kind = ?2 AND name = ?3",
            params![account_id, kind, name],
            |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?)),
        )
        .optional()?;

    if let Some((id, version, _)) = existing {
        let new_version = version + 1;
        conn.execute(
            "UPDATE me_document SET data = ?1, version = ?2, updated_at = ?3, deleted_at = NULL
             WHERE id = ?4",
            params![data, new_version, now, id],
        )?;
        return Ok(UpsertOutcome::Saved(Document {
            id,
            kind: kind.to_string(),
            name: name.to_string(),
            data: Some(data.to_string()),
            version: new_version,
            updated_at: now,
            deleted: false,
        }));
    }

    if count_all_documents(conn, account_id)? >= MAX_DOCUMENTS_PER_ACCOUNT {
        return Ok(UpsertOutcome::QuotaExceeded);
    }

    let id = generate_document_id();
    conn.execute(
        "INSERT INTO me_document (id, account_id, kind, name, data, version, updated_at)
         VALUES (?1, ?2, ?3, ?4, ?5, 1, ?6)",
        params![id, account_id, kind, name, data, now],
    )?;
    Ok(UpsertOutcome::Saved(Document {
        id,
        kind: kind.to_string(),
        name: name.to_string(),
        data: Some(data.to_string()),
        version: 1,
        updated_at: now,
        deleted: false,
    }))
}

/// Renomme un document en place (édition du champ Name depuis le site).
/// Échoue en `Conflict` si le nom cible est déjà pris par un autre document vivant.
pub enum RenameOutcome {
    Renamed,
    Conflict,
    NotFound,
}

pub fn rename_document(
    conn: &Connection,
    account_id: &str,
    doc_id: &str,
    new_name: &str,
) -> rusqlite::Result<RenameOutcome> {
    let current: Option<String> = conn
        .query_row(
            "SELECT kind FROM me_document
             WHERE account_id = ?1 AND id = ?2 AND deleted_at IS NULL",
            params![account_id, doc_id],
            |row| row.get(0),
        )
        .optional()?;

    let Some(kind) = current else {
        return Ok(RenameOutcome::NotFound);
    };

    let taken: bool = conn.query_row(
        "SELECT EXISTS(SELECT 1 FROM me_document
         WHERE account_id = ?1 AND kind = ?2 AND name = ?3 AND id <> ?4 AND deleted_at IS NULL)",
        params![account_id, kind, new_name, doc_id],
        |row| row.get(0),
    )?;
    if taken {
        return Ok(RenameOutcome::Conflict);
    }

    conn.execute(
        "UPDATE me_document SET name = ?1 WHERE account_id = ?2 AND id = ?3",
        params![new_name, account_id, doc_id],
    )?;
    Ok(RenameOutcome::Renamed)
}

/// Marque un document supprimé sans effacer la ligne : le tombstone est ce qui permet
/// au plugin de répercuter la suppression au prochain pull.
pub fn delete_document(
    conn: &Connection,
    account_id: &str,
    kind: &str,
    name: &str,
) -> rusqlite::Result<bool> {
    let now = now_ms();
    let affected = conn.execute(
        "UPDATE me_document SET data = '', version = version + 1, updated_at = ?1, deleted_at = ?1
         WHERE account_id = ?2 AND kind = ?3 AND name = ?4 AND deleted_at IS NULL",
        params![now, account_id, kind, name],
    )?;
    Ok(affected > 0)
}

pub fn delete_document_by_id(
    conn: &Connection,
    account_id: &str,
    doc_id: &str,
) -> rusqlite::Result<bool> {
    let now = now_ms();
    let affected = conn.execute(
        "UPDATE me_document SET data = '', version = version + 1, updated_at = ?1, deleted_at = ?1
         WHERE account_id = ?2 AND id = ?3 AND deleted_at IS NULL",
        params![now, account_id, doc_id],
    )?;
    Ok(affected > 0)
}

/// Purge les tombstones anciens : passé ce délai, un client qui n'a pas synchronisé depuis
/// si longtemps refera de toute façon un pull complet (`since = 0`).
pub fn purge_old_tombstones(conn: &Connection, max_age_ms: i64) -> rusqlite::Result<usize> {
    let cutoff = now_ms() - max_age_ms;
    conn.execute(
        "DELETE FROM me_document WHERE deleted_at IS NOT NULL AND deleted_at < ?1",
        params![cutoff],
    )
}
