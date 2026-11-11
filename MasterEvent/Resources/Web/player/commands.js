import { ANSWER_MS, NOTICES_MAX, NOTICE_MS, RATE_PER_SECOND, SETTLE_MS } from './constants.js';
import { $, ARIA_LABEL, el, icon } from './dom.js';
import { int, opt, str, yes } from './read.js';
import { send } from './socket.js';
import { app, renderSoon } from './store.js';
import { hasText, t } from './texts.js';

const SETTLING = 'settling';
const TONE_ERROR = 'error';

const pending = new Map();
export const locks = new Map();
const cooldowns = new Map();
const outbox = [];
const sentAt = [];
let pumpTimer = 0;

export function can(op) {
  const current = app.current;
  return app.helloVersion >= 2 && !app.lostAt && !app.final && !!current && current.access === 'player'
    && !!current.room && current.caps.includes(op);
}

export const isLocked = key => locks.has(key) || (cooldowns.get(key) ?? 0) > Date.now();

function newId() {
  const bytes = new Uint8Array(12);
  crypto.getRandomValues(bytes);
  return btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

// options : label (pour l'avis), lock (geste verrouillé jusqu'à l'état qui montre l'effet), value
// (ce que le geste vise, pour marquer le bon bouton), cooldown.
export function command(op, fields, options = {}) {
  if (!can(op)) return false;
  const current = app.current;
  const entry = { id: newId(), op, phase: 'queued', ackSeq: 0, timer: 0, ...options };
  pending.set(entry.id, entry);
  if (entry.lock) locks.set(entry.lock, entry);
  outbox.push({ entry, message: { type: 'cmd', id: entry.id, op, lobby: current.room, seen: current.seq, ...fields } });
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
  // L'état de ce seq contient déjà l'effet : sans lui, le bouton reviendrait un instant à l'ancien geste.
  entry.ackSeq = int(message.seq);
  entry.phase = SETTLING;
  if (app.current && app.current.seq >= entry.ackSeq) finish(entry, true, null);
  else entry.timer = setTimeout(() => finish(entry, true, null), SETTLE_MS);
}

function expire(entry) {
  if (entry.phase === SETTLING) finish(entry, true, null);
  else finish(entry, false, 'noAnswer');
}

export function settle(seq) {
  for (const entry of [...pending.values()])
    if (entry.phase === SETTLING && entry.ackSeq <= seq) finish(entry, true, null);
}

function finish(entry, ok, error) {
  if (pending.get(entry.id) !== entry) return;
  pending.delete(entry.id);
  clearTimeout(entry.timer);
  if (entry.lock && locks.get(entry.lock) === entry) {
    locks.delete(entry.lock);
    const inert = entry.cooldown ?? 0;
    if (inert > 0) {
      cooldowns.set(entry.lock, Date.now() + inert);
      setTimeout(renderSoon, inert + 10);
    }
  }
  if (!ok) notify(entry.label, describe(error), error === 'stale' ? 'info' : TONE_ERROR);
  renderSoon();
}

function describe(error) {
  if (error === 'noAnswer') return t('PlayerWeb.NoAnswer');
  const key = 'PlayerWeb.Err.' + error;
  return hasText(key) ? t(key) : t('PlayerWeb.Err.unknown');
}

// Coupure, changement de lien, fin de session : rien n'est renvoyé de soi-même.
export function abandonCommands(lost) {
  const had = pending.size > 0;
  for (const entry of pending.values()) clearTimeout(entry.timer);
  pending.clear();
  locks.clear();
  outbox.length = 0;
  clearTimeout(pumpTimer);
  if (had && lost) notify(null, t('PlayerWeb.LostCommand'), TONE_ERROR);
}

function notify(label, text, tone) {
  const box = $('notices');
  const item = el('div', 'notice notice-' + tone);
  item.setAttribute('role', tone === TONE_ERROR ? 'alert' : 'status');
  const close = el('button', 'icon-btn notice-close');
  close.type = 'button';
  close.setAttribute(ARIA_LABEL, t('PlayerWeb.Close'));
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
