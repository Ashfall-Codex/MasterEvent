import { ANSWER_MS, CONTROL_INERT_MS, NOTICE_MS, NOTICES_MAX, OP_ADMIT, OP_DENY, RATE_PER_SECOND, SETTLE_MS } from './constants.js';
import { send } from './connection.js';
import { $, ARIA_LABEL, el, icon } from './dom.js';
import { hasString, t } from './i18n.js';
import { renderSoon } from './render.js';
import { app, can } from './state.js';
import { int, opt, str, yes } from './util.js';

const PHASE_SETTLING = 'settling';
const TONE_ERROR = 'error';

const pending = new Map();
const locks = new Map();
const cooldowns = new Map();
const outbox = [];
const sentAt = [];
let pumpTimer = 0;

export const isBusy = key => locks.has(key);
export const isLocked = key => locks.has(key) || (cooldowns.get(key) ?? 0) > Date.now();
// Valeur demandée par la commande qui tient le verrou, affichée pendant l'attente.
export const lockedValue = key => locks.get(key).value;
export const hasCommandsInFlight = () => pending.size > 0;

function newId() {
  const bytes = new Uint8Array(12);
  crypto.getRandomValues(bytes);
  return btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

// options : label (pour l'avis), lock (contrôle verrouillé jusqu'à l'état qui montre l'effet), value
// (valeur demandée, affichée pendant l'attente), cooldown, done(ok, error) qui renvoie vrai s'il a
// lui-même affiché l'échec.
export function command(op, fields, options = {}) {
  if (!can(op)) return false;
  const current = app.current;
  const entry = { id: newId(), op, phase: 'queued', ackSeq: 0, timer: 0, ...options };
  pending.set(entry.id, entry);
  if (entry.lock) locks.set(entry.lock, entry);
  outbox.push({ entry, message: { type: 'cmd', id: entry.id, op, lobby: current.link.lobbyCode, seen: current.seq, ...fields } });
  pump();
  renderSoon();
  return true;
}

function pump() {
  clearTimeout(pumpTimer);
  while (outbox.length > 0) {
    const now = Date.now();
    while (sentAt.length > 0 && now - sentAt[0] >= 1000) sentAt.shift();
    if (sentAt.length >= RATE_PER_SECOND) {
      pumpTimer = setTimeout(pump, 1000 - (now - sentAt[0]) + 5);
      return;
    }
    const { entry, message } = outbox.shift();
    if (pending.get(entry.id) !== entry) continue;
    sentAt.push(now);
    entry.phase = 'sent';
    entry.timer = setTimeout(() => expire(entry), ANSWER_MS);
    send(message);
  }
}

export function onAck(message) {
  const entry = pending.get(str(message.id));
  if (!entry || entry.phase !== 'sent') return;
  clearTimeout(entry.timer);
  if (!yes(message.ok)) {
    finish(entry, false, opt(message.error) ?? 'unknown');
    return;
  }
  // L'état de ce seq contient déjà l'effet : sans lui, le contrôle montrerait l'ancienne valeur.
  entry.ackSeq = int(message.seq);
  entry.phase = PHASE_SETTLING;
  if (app.current && app.current.seq >= entry.ackSeq) finish(entry, true, null);
  else entry.timer = setTimeout(() => finish(entry, true, null), SETTLE_MS);
}

function expire(entry) {
  if (entry.phase === PHASE_SETTLING) finish(entry, true, null);
  else finish(entry, false, 'noAnswer');
}

export function settle(seq) {
  for (const entry of [...pending.values()])
    if (entry.phase === PHASE_SETTLING && entry.ackSeq <= seq) finish(entry, true, null);
}

function finish(entry, ok, error) {
  if (pending.get(entry.id) !== entry) return;
  pending.delete(entry.id);
  clearTimeout(entry.timer);
  if (entry.lock && locks.get(entry.lock) === entry) {
    locks.delete(entry.lock);
    const inert = entry.cooldown ?? CONTROL_INERT_MS;
    cooldowns.set(entry.lock, Date.now() + inert);
    setTimeout(renderSoon, inert + 10);
  }
  const handled = yes(entry.done?.(ok, error));
  if (!ok && !handled) notify(entry.label, describe(entry.op, error), error === 'stale' ? 'info' : TONE_ERROR);
  renderSoon();
}

export function describe(op, error) {
  if (error === 'noAnswer') return t('Web.NoAnswer');
  if (error === 'gone' && (op === OP_ADMIT || op === OP_DENY)) return t('Web.Err.goneRequest');
  const key = 'Web.Err.' + error;
  return hasString(key) ? t(key) : t('Web.Err.unknown');
}

// Coupure, changement de lien, fin de session : rien n'est renvoyé de soi-même.
export function dropCommands() {
  for (const entry of pending.values()) clearTimeout(entry.timer);
  pending.clear();
  locks.clear();
  outbox.length = 0;
  clearTimeout(pumpTimer);
}

export function notify(label, text, tone) {
  const box = $('notices');
  const item = el('div', 'notice notice-' + tone);
  item.setAttribute('role', tone === TONE_ERROR ? 'alert' : 'status');
  const close = el('button', 'icon-btn notice-close');
  close.type = 'button';
  close.setAttribute(ARIA_LABEL, t('Web.Close'));
  close.append(icon('close'));
  const body = el('span', 'notice-text');
  if (label) body.append(el('strong', 'notice-label', label));
  body.append(el('span', 'notice-body', text));
  item.append(body, close);
  box.append(item);
  while (box.childElementCount > NOTICES_MAX) box.firstElementChild.remove();
  const timer = setTimeout(() => item.remove(), NOTICE_MS);
  close.addEventListener('click', () => {
    clearTimeout(timer);
    item.remove();
  });
}
