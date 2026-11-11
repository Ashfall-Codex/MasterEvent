import { abandonCommands, onAck, settle } from './commands.js';
import { CREDENTIAL, KEY_SESSION, LANG, RETRY_MS, UNREACHABLE_MS } from './constants.js';
import { forget, load, save } from './dom.js';
import { closeDetails, scrollToActiveBlock } from './list.js';
import { clearNotices } from './notices.js';
import { acceptPortraits, clearPortraits } from './portraits.js';
import { int, obj, readState } from './read.js';
import { applyTexts, render, renderBanner, renderScreen, showScreen } from './render.js';
import { forceRebuild } from './rows.js';
import { closeSocket, openSocket, send } from './socket.js';
import { app, renderSoon } from './store.js';
import { resetFormats, setTexts } from './texts.js';

let ticket = null;
let session = null;
let retryTimer = 0;
let unreachableTimer = 0;

function takeTicket() {
  if (!location.hash) return null;
  const value = new URLSearchParams(location.hash.slice(1)).get('t');
  history.replaceState(null, '', location.pathname + location.search);
  return value && CREDENTIAL.test(value) ? value : null;
}

// Un ticket dans l'adresse remplace la session gardée par l'onglet.
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
  if (!openSocket(onMessage, onClose)) connectionLost();
}

function stop(kind) {
  app.final = true;
  clearTimeout(retryTimer);
  clearTimeout(unreachableTimer);
  app.lostAt = 0;
  abandonCommands(true);
  closeDetails();
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
    case 'portraits': onPortraits(message); break;
    case 'bye': onBye(message); break;
  }
}

function onHello(message) {
  setTexts(obj(message.strings));
  app.helloVersion = int(message.v);
  if (typeof message.lang === 'string' && LANG.test(message.lang)) document.documentElement.lang = message.lang;
  resetFormats();

  applyTexts();
  forceRebuild();
  clearNotices();
  renderScreen();
  render();

  if (session) send({ type: 'auth', session });
  else if (ticket) send({ type: 'auth', ticket });
  else stop('expired');
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
  else if (app.screen) {
    showScreen(null);
    // Le rendu a eu lieu sous l'écran d'attente, liste masquée : le défilement n'a pas pu se faire.
    scrollToActiveBlock();
  }
}

function onPortraits(message) {
  acceptPortraits(obj(message.items));
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
    stop('expired');
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
export function onHashChange() {
  const fresh = takeTicket();
  if (!fresh) return;
  abandonCommands(true);
  closeDetails();
  ticket = fresh;
  session = null;
  forget(KEY_SESSION);
  app.final = false;
  app.lostAt = 0;
  clearTimeout(retryTimer);
  clearTimeout(unreachableTimer);
  closeSocket();
  app.current = null;
  clearPortraits();
  showScreen('connecting');
  connect();
}
