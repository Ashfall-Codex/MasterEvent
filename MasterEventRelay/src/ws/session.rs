use std::sync::atomic::Ordering;
use axum::extract::ws::{Message, WebSocket};
use futures_util::{SinkExt, StreamExt};
use tokio::sync::mpsc;
use tokio::time::{interval, Duration};
use tracing::{error, info, warn};

use crate::models::IncomingMessage;
use crate::state::AppState;
use crate::ws::broadcast::relay_to_room;
use crate::ws::handlers;

const WS_RATE_LIMIT: u32 = 30;
const WS_RATE_WINDOW_MS: u64 = 1000;
const PING_INTERVAL_SECS: u64 = 30;
/// Nature d'une trame reçue, pour sortir le contrôle de flux de la boucle `select!`.
enum Frame {
    Text(String),
    Pong,
    Ignore,
    Stop,
}

fn classify_frame(result: Option<Result<Message, axum::Error>>, client_id: u64) -> Frame {
    match result {
        Some(Ok(Message::Text(t))) => Frame::Text(t.to_string()),
        Some(Ok(Message::Pong(_))) => Frame::Pong,
        Some(Ok(Message::Close(_))) | None => Frame::Stop,
        Some(Err(e)) => {
            error!("WebSocket error for client {}: {}", client_id, e);
            Frame::Stop
        }
        _ => Frame::Ignore,
    }
}

/// Fenêtre glissante de limitation de débit. Retourne true quand la trame doit être ignorée.
fn rate_limited(count: &mut u32, window_start: &mut u64, client_id: u64) -> bool {
    let now = AppState::now_ms();
    if now - *window_start > WS_RATE_WINDOW_MS {
        *count = 0;
        *window_start = now;
    }

    *count += 1;
    if *count > WS_RATE_LIMIT {
        warn!("Rate limit exceeded for client {}", client_id);
        return true;
    }

    false
}

/// Contexte immuable d'une session, pour éviter de réenfiler les mêmes arguments.
struct SessionCtx<'a> {
    state: &'a AppState,
    client_id: u64,
    client_ip: &'a str,
    tx: &'a mpsc::UnboundedSender<String>,
}

/// Décode une trame texte. Retourne None (en incrémentant le compteur d'erreurs)
/// quand le JSON est invalide, sans type, ou non conforme au schéma attendu.
fn decode_message(
    state: &AppState,
    raw: &str,
) -> Option<(String, IncomingMessage, serde_json::Value)> {
    let decoded = (|| {
        let raw_value: serde_json::Value = serde_json::from_str(raw).ok()?;
        let msg_type = raw_value.get("type")?.as_str()?.to_string();
        let parsed: IncomingMessage = serde_json::from_value(raw_value.clone()).ok()?;
        Some((msg_type, parsed, raw_value))
    })();

    match decoded {
        Some(v) => {
            state.messages_total.fetch_add(1, Ordering::Relaxed);
            Some(v)
        }
        None => {
            state.errors_total.fetch_add(1, Ordering::Relaxed);
            None
        }
    }
}

/// Relaie la trame à la room courante, en exigeant le statut leader ou promu si demandé.
fn relay_to_current_room(
    ctx: &SessionCtx,
    current_room: &Option<String>,
    raw_value: &serde_json::Value,
    msg_type: &str,
    require_privilege: bool,
) {
    let Some(room_key) = current_room else {
        return;
    };
    let Some(mut room) = ctx.state.rooms.get_mut(room_key) else {
        return;
    };

    if require_privilege {
        let authorized = room
            .clients
            .get(&ctx.client_id)
            .map(|c| c.info.is_leader || c.info.is_promoted)
            .unwrap_or(false);
        if !authorized {
            warn!(
                "[relay] {} from client {} rejected — not leader/promoted in room {}",
                msg_type, ctx.client_id, room_key
            );
            return;
        }
    }

    relay_to_room(room.value_mut(), ctx.client_id, raw_value);
}

/// Aiguille un message décodé vers son handler.
fn dispatch_message(
    ctx: &SessionCtx,
    msg_type: &str,
    parsed: &IncomingMessage,
    raw_value: &serde_json::Value,
    current_room: &mut Option<String>,
) {
    match msg_type {
        "join" => handlers::handle_join(
            ctx.state,
            ctx.client_id,
            ctx.client_ip,
            ctx.tx,
            parsed,
            current_room,
        ),
        "leave" => handlers::handle_leave(ctx.state, ctx.client_id, current_room, true),

        // Messages nécessitant le statut leader ou promu
        "update" | "clear" | "playerUpdate" | "templateShare" | "turnUpdate" | "turnClear"
        | "weatherUpdate" | "timeUpdate" | "allianceKick" | "allianceInvite"
        | "allianceDisband" | "gmAnnouncement" => {
            relay_to_current_room(ctx, current_room, raw_value, msg_type, true);
        }

        // Messages ouverts à tous.
        // `turnEndSelf` est une *demande* : le joueur signale qu'il a fini son tour,
        // et seul le MJ décide de l'appliquer. L'état des tours reste donc modifiable
        // par le seul `turnUpdate`, réservé au leader.
        "requestUpdate" | "roll" | "statRoll" | "playerStatUpdate" | "turnEndSelf" => {
            relay_to_current_room(ctx, current_room, raw_value, msg_type, false);
        }

        "promote" => {
            handlers::handle_promote(ctx.state, ctx.client_id, current_room, parsed, raw_value);
        }

        // Couche lobby (protocole 2).
        "admit" => handlers::handle_admit(ctx.state, ctx.client_id, current_room, parsed),
        "deny" => handlers::handle_deny(ctx.state, ctx.client_id, current_room, parsed),
        "rosterUpdate" => {
            handlers::handle_roster_update(ctx.state, ctx.client_id, current_room, parsed);
        }
        _ => {}
    }
}

/// Ping de keepalive. Retourne false quand la connexion doit être fermée :
/// pong manquant depuis le tour précédent, ou envoi impossible.
async fn keepalive_ping(
    ws_sink: &mut futures_util::stream::SplitSink<WebSocket, Message>,
    awaiting_pong: &mut bool,
    client_id: u64,
) -> bool {
    if *awaiting_pong {
        warn!("Client {} pong timeout, disconnecting", client_id);
        return false;
    }

    if ws_sink.send(Message::Ping(vec![].into())).await.is_err() {
        return false;
    }

    *awaiting_pong = true;
    true
}

pub async fn handle_session(
    state: AppState,
    socket: WebSocket,
    client_id: u64,
    client_ip: String,
) {
    let (mut ws_sink, mut ws_stream) = socket.split();
    let (tx, mut rx) = mpsc::unbounded_channel::<String>();

    let mut current_room: Option<String> = None;
    let mut message_count: u32 = 0;
    let mut rate_window_start = AppState::now_ms();

    // Keepalive : ping périodique pour détecter les connexions mortes
    let mut ping_timer = interval(Duration::from_secs(PING_INTERVAL_SECS));
    ping_timer.tick().await; // Le premier tick est immédiat, on le skip
    let mut awaiting_pong = false;

    //  shutdown : déclenché par notify_waiters() depuis main au Ctrl-C / SIGTERM
    let shutdown_notify = state.shutdown_notify.clone();
    let shutdown_fut = shutdown_notify.notified();
    tokio::pin!(shutdown_fut);

    loop {
        tokio::select! {
            // Lecture des messages entrants depuis le client
            result = ws_stream.next() => {
                let raw = match classify_frame(result, client_id) {
                    Frame::Text(t) => t,
                    Frame::Pong => {
                        awaiting_pong = false;
                        continue;
                    }
                    Frame::Ignore => continue,
                    Frame::Stop => break,
                };

                if rate_limited(&mut message_count, &mut rate_window_start, client_id) {
                    continue;
                }

                let Some((msg_type, parsed, raw_value)) = decode_message(&state, &raw) else {
                    continue;
                };

                let ctx = SessionCtx { state: &state, client_id, client_ip: &client_ip, tx: &tx };
                dispatch_message(&ctx, &msg_type, &parsed, &raw_value, &mut current_room);
            }

            // Envoi des messages sortants (relayés depuis d'autres clients)
            msg = rx.recv() => {
                match msg {
                    Some(text) => {
                        if ws_sink.send(Message::Text(text.into())).await.is_err() {
                            break;
                        }
                    }
                    None => break,
                }
            }

            // Keepalive : envoi de ping périodique
            _ = ping_timer.tick() => {
                if !keepalive_ping(&mut ws_sink, &mut awaiting_pong, client_id).await {
                    break;
                }
            }

            // Shutdown  : le serveur s'arrête, on ferme la session proprement
            _ = &mut shutdown_fut => {
                info!("Shutdown signal reçu, fermeture de la session client {}", client_id);
                let _ = ws_sink.send(Message::Close(None)).await;
                break;
            }
        }
    }

    // Nettoyage à la déconnexion
    handlers::handle_leave(&state, client_id, &mut current_room, false);
}
