import { KEY_PRIVATE, KIND_MARKER, KIND_NPC, KIND_PLAYER } from './constants.js';
import { fmt } from './i18n.js';
import { load } from './util.js';

// État partagé entre les modules : une liaison importée ne se réassigne pas, un champ de cet objet si.
// privateChoice vaut « 1 » ou « 0 » une fois choisi par le bouton de cet onglet ; sinon le hello donne
// le réglage du MJ.
const privateChoice = load(KEY_PRIVATE);
export const app = {
  helloVersion: 0,
  current: null,
  notesText: '',
  privateChoice,
  privateHidden: privateChoice === '1',
  final: false,
  lostAt: 0,
  detail: null,
};

// Le rendu s'enregistre au démarrage : les modules qui le demandent n'importent pas render.js, qui les importe.
let renderer = null;
let renderQueued = false;

export function setRenderer(fn) {
  renderer = fn;
}

export function renderSoon() {
  if (renderQueued) return;
  renderQueued = true;
  queueMicrotask(() => {
    renderQueued = false;
    renderer();
  });
}

export function renderNow() {
  renderer();
}

export function can(op) {
  const s = app.current;
  return app.helloVersion >= 2 && !app.lostAt && !app.final && !!s && s.access !== 'none'
    && !!s.link.lobbyCode && s.caps.includes(op);
}

// ── Recherche dans l'état ──

export const isNamed = m => m.name.trim() !== '';

export const isDetail = (kind, ref) => !!app.detail && app.detail.kind === kind && app.detail.ref === ref;

export function entityOf(s, kind, ref) {
  if (!s || ref == null) return null;
  if (kind === KIND_PLAYER) return s.party.find(p => p.hash === ref) ?? null;
  if (kind === KIND_MARKER) return s.markers.find(m => m.id === ref) ?? null;
  if (kind === KIND_NPC) return s.npcs.find(n => n.id === ref) ?? null;
  return null;
}

export function displayName(kind, entity, ref) {
  if (kind === KIND_MARKER && !isNamed(entity)) return fmt('Web.MarkerFmt', ref);
  return entity.name;
}

export function targetName(s, kind, ref) {
  const entity = entityOf(s, kind, ref);
  return entity ? displayName(kind, entity, ref) : ref;
}
