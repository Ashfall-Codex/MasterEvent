import { CREDENTIAL, KEY_SESSION, LANG, OPEN_TIMEOUT_MS, RETRY_MS, UNREACHABLE_MS } from './constants.js';
import { dropCommands, hasCommandsInFlight, notify, onAck, settle } from './commands.js';
import { forgetDetailShell } from './detail.js';
import { resetFormats, setStrings, t } from './i18n.js';
import { renderNotes } from './lists.js';
import { closeMenu } from './menu.js';
import { dropDenyConfirms } from './pending.js';
import { clearPortraits, setPortraits } from './portraits.js';
import { readState } from './read.js';
import { forceRebuild } from './reconcile.js';
import { applyTexts, hasScreen, render, renderBanner, renderScreen, renderSoon, showScreen } from './render.js';
import { closeSheets } from './sheets.js';
import { app } from './state.js';
import { forget, int, load, obj, save, str, yes } from './util.js';
import { dropVitals, hasVitalsInFlight } from './vitals.js';

// ── Connexion ──

const SCREEN_EXPIRED = 'expired';

function takeTicket() {
  if (!location.hash) return null;
  const value = new URLSearchParams(location.hash.slice(1)).get('t');
  history.replaceState(null, '', location.pathname + location.search);
  return value && CREDENTIAL.test(value) ? value : null;
}

let ticket = null;
let session = null;
let socket = null;
let retryTimer = 0;
let unreachableTimer = 0;

// Lit le ticket du lien, sinon la session gardée par cet onglet.
export function readCredentials() {
  ticket = takeTicket();
  if (ticket) {
    forget(KEY_SESSION);
  } else {
    const stored = load(KEY_SESSION);
    session = stored && CREDENTIAL.test(stored) ? stored : null;
  }
}

export function connect() {
  clearTimeout(retryTimer);
  let ws;
  try {
    ws = new WebSocket('ws://' + location.host + '/ws');
  } catch {
    connectionLost();
    return;
  }
  socket = ws;

  const openTimer = setTimeout(() => {
    if (ws.readyState === WebSocket.CONNECTING) ws.close();
  }, OPEN_TIMEOUT_MS);

  ws.addEventListener('open', () => clearTimeout(openTimer));
  ws.addEventListener('message', event => {
    if (socket === ws) onMessage(event.data);
  });
  ws.addEventListener('close', event => {
    clearTimeout(openTimer);
    if (socket !== ws) return;
    socket = null;
    onClose(event.code);
  });
}

export function send(message) {
  if (socket && socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify(message));
}

function closeSocket() {
  if (!socket) return;
  const ws = socket;
  socket = null;
  ws.close(1000);
}

// Coupure, changement de lien, fin de session : rien n'est renvoyé de soi-même.
function abandonCommands(lost) {
  const had = hasCommandsInFlight() || hasVitalsInFlight();
  dropCommands();
  dropVitals();
  dropDenyConfirms();
  closeMenu(false);
  if (had && lost) notify(null, t('Web.LostCommand'), 'error');
}

function stop(kind) {
  app.final = true;
  clearTimeout(retryTimer);
  clearTimeout(unreachableTimer);
  app.lostAt = 0;
  abandonCommands(true);
  closeSheets();
  app.detail = null;
  clearPortraits();
  showScreen(kind);
  closeSocket();
}

function dropCredentials() {
  ticket = null;
  session = null;
  forget(KEY_SESSION);
}

function onMessage(data) {
  if (typeof data !== 'string') return;
  let message;
  try { message = JSON.parse(data); } catch { return; }
  if (!message || typeof message !== 'object') return;

  switch (message.type) {
    case 'hello': onHello(message); break;
    case 'welcome': onWelcome(message); break;
    case 'ack': onAck(message); break;
    case 'state': onState(message); break;
    case 'notes': onNotes(message); break;
    case 'portraits': onPortraits(message); break;
    case 'bye': onBye(message); break;
  }
}

function onHello(message) {
  setStrings(obj(message.strings));
  app.helloVersion = int(message.v);
  if (typeof message.lang === 'string' && LANG.test(message.lang)) document.documentElement.lang = message.lang;
  resetFormats();
  if (app.privateChoice !== '1' && app.privateChoice !== '0') app.privateHidden = yes(message.privateByDefault);

  applyTexts();
  forceRebuild();
  forgetDetailShell();
  renderScreen();
  render();

  if (session) send({ type: 'auth', session });
  else if (ticket) send({ type: 'auth', ticket });
  else stop(SCREEN_EXPIRED);
}

function onWelcome(message) {
  if (typeof message.session !== 'string' || !CREDENTIAL.test(message.session)) return;
  session = message.session;
  ticket = null;
  save(KEY_SESSION, session);
  connectionRestored();
}

function connectionRestored() {
  if (!app.lostAt) return;
  app.lostAt = 0;
  clearTimeout(unreachableTimer);
  renderBanner();
}

function onState(message) {
  // Une reprise par session peut se passer de welcome : le premier état suffit à lever l'alerte.
  connectionRestored();
  app.current = readState(message);
  settle(app.current.seq);
  // Rendu même sans accès : l'état « none » arrive vide et purge ce qui restait affiché.
  render();
  if (app.current.access === 'none') showScreen('none');
  else if (hasScreen()) showScreen(null);
}

function onNotes(message) {
  app.notesText = str(message.text);
  if (app.current) renderNotes();
}

// Gardé même avant le premier état : le plugin ne renvoie la liste que lorsqu'elle change.
function onPortraits(message) {
  setPortraits(obj(message.items));
  renderSoon();
}

function onBye(message) {
  dropCredentials();
  stop(message.reason === 'unloaded' || message.reason === 'ended' ? message.reason : 'revoked');
}

function onClose(code) {
  if (app.final) return;

  if (code === 4401) {
    dropCredentials();
    stop(SCREEN_EXPIRED);
    return;
  }
  if (code === 4409) {
    stop('busy');
    return;
  }
  connectionLost();
}

function connectionLost() {
  if (!app.lostAt) {
    app.lostAt = Date.now();
    clearTimeout(unreachableTimer);
    unreachableTimer = setTimeout(() => {
      if (app.lostAt && !app.final) showScreen('unreachable');
    }, UNREACHABLE_MS);
  }
  abandonCommands(true);
  renderBanner();
  render();
  clearTimeout(retryTimer);
  retryTimer = setTimeout(connect, RETRY_MS);
}

// Lien rouvert depuis le jeu dans un onglet déjà ouvert : seul le fragment change, sans rechargement.
export function bindReopen() {
  window.addEventListener('hashchange', () => {
    const fresh = takeTicket();
    if (!fresh) return;
    abandonCommands(true);
    closeSheets();
    app.detail = null;
    ticket = fresh;
    session = null;
    forget(KEY_SESSION);
    app.final = false;
    app.lostAt = 0;
    clearTimeout(retryTimer);
    clearTimeout(unreachableTimer);
    closeSocket();
    app.current = null;
    app.notesText = '';
    clearPortraits();
    showScreen('connecting');
    connect();
  });
}
