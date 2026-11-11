import { ACTION_DETAIL, KIND_MARKER, KIND_NPC, KIND_PLAYER } from './constants.js';
import { tickSpec } from './controls.js';
import { control, el, hidden, pill, setData, setText } from './dom.js';
import { clock, fmt, t } from './i18n.js';
import { actedMark, attitudeText, connectionTrail, hasMp, meter, tagsFor } from './parts.js';
import { markerGlyph, npcGlyph, playerGlyph, recycling } from './portraits.js';
import { syncControls } from './reconcile.js';
import { isNamed } from './state.js';
import { signed, yes } from './util.js';

const ROW_LEAD = 'row-lead';
const TONE_PENDING = 'pending';
const accentPill = key => pill(t(key), 'accent');
const bossPill = () => accentPill('Web.Boss');
const theirTurnPill = () => pill(t('Web.TheirTurn'), TONE_PENDING);

// Ligne à commandes : le corps est reconstruit quand il change, le nom et les commandes restent.
// La case en tête ; le dé, - et + à droite de la jauge de PV, comme en jeu.
const rowParts = new WeakMap();

function makeRowParts() {
  const node = el('li', 'row');
  const parts = {
    lead: el('span', ROW_LEAD),
    head: el('span', 'row-head'),
    main: el('div', 'row-main'),
    line: el('div', 'row-line'),
    name: null,
    extra: el('span', 'row-extra'),
    pre: el('span', 'row-pre'),
    hp: el('div', 'row-hp'),
    hpMeter: el('span', 'row-hp-meter'),
    hpCtl: el('span', 'row-ctl'),
    rest: el('span', 'row-rest'),
    sig: null,
  };
  parts.line.append(parts.extra);
  parts.hp.append(parts.hpMeter, parts.hpCtl);
  parts.main.append(parts.line, parts.pre, parts.hp, parts.rest);
  node.append(parts.lead, parts.head, parts.main);
  rowParts.set(node, parts);
  return node;
}

export function patchRow(node, vm, body, controls) {
  if (!node || !rowParts.has(node)) node = makeRowParts();
  const parts = rowParts.get(node);

  const sig = JSON.stringify(vm.body);
  if (sig !== parts.sig) {
    parts.sig = sig;
    const b = recycling(parts.head, () => body(vm.body));
    node.className = 'row' + (b.cls ? ' ' + b.cls : '');
    parts.head.replaceChildren(...b.head);
    setName(parts, b.name);
    parts.extra.replaceChildren(...b.extra);
    parts.pre.replaceChildren(...(b.pre ?? []));
    parts.hpMeter.replaceChildren(...(b.hp ? [b.hp] : []));
    parts.rest.replaceChildren(...b.rest);
  }
  const c = controls(vm.ctl);
  syncControls(parts.lead, c.lead);
  syncControls(parts.hpCtl, c.hp);
  parts.hp.hidden = !parts.hpMeter.firstChild && !parts.hpCtl.firstChild;
  return node;
}

function setName(parts, name) {
  const button = !!name.open;
  let node = parts.name;
  if (!node || (node.tagName === 'BUTTON') !== button) {
    const next = button ? control('name', ACTION_DETAIL) : el('span', 'name');
    if (node) node.replaceWith(next);
    else parts.line.insertBefore(next, parts.extra);
    parts.name = node = next;
  }
  node.className = 'name' + (button ? ' name-btn' : '') + (name.muted ? ' is-muted' : '');
  setText(node, name.text);
  if (button) {
    setData(node, { kind: name.open.kind, ref: name.open.ref });
    node.title = t('Web.OpenDetail');
  }
}

// ── Lignes ──

export function playerBody(b) {
  const p = b.p;
  return {
    cls: p.connected ? '' : 'is-dim',
    head: [hidden(playerGlyph(p.group, p.hash))],
    name: { text: p.name, muted: !p.connected, open: b.open },
    extra: [
      ...(p.isGm ? [accentPill('Web.Gm')] : p.isCoGm ? [accentPill('Web.CoGm')] : []),
      ...(p.awaitingRoll ? [Object.assign(pill(t('Web.AwaitingRoll'), TONE_PENDING), { title: t('Web.AwaitingRollHint') })] : []),
      connectionTrail(p.connected),
    ],
    hp: meter('hp', KIND_PLAYER, p.hp, p.hpMax, b.d, b.d.showShield ? p.shield : 0, b.pend),
    rest: [
      ...(hasMp(b.d, p.mpMax) ? [meter('mp', 'mp', p.mp, p.mpMax, b.d, 0, null)] : []),
      ...tagsFor(p, b.priv, b.d),
    ],
  };
}

export function markerBody(b) {
  const m = b.m;
  const named = isNamed(m);
  return {
    cls: m.visible ? '' : 'is-dim',
    head: [hidden(markerGlyph(m.id))],
    name: { text: named ? m.name : fmt('Web.MarkerFmt', m.id), muted: !named, open: b.open },
    extra: [
      ...(m.boss ? [bossPill()] : []),
      ...(m.visible ? [] : [el('span', 'hint', t('Web.MarkerNotPlaced'))]),
      attitudeText(m.attitude),
    ],
    hp: meter('hp', m.attitude, m.hp, m.hpMax, b.d, b.d.showShield ? m.shield : 0, b.pend),
    rest: [
      ...(hasMp(b.d, m.mpMax) ? [meter('mp', 'mp', m.mp, m.mpMax, b.d, 0, null)] : []),
      ...tagsFor(m, b.priv, b.d),
    ],
  };
}

export function npcBody(b) {
  const n = b.n;
  return {
    cls: '',
    head: [hidden(npcGlyph(n.creature))],
    name: { text: n.name, muted: false, open: b.open },
    extra: [
      el('span', 'hint', t(n.creature ? 'Web.Creature' : 'Web.Npc')),
      ...(n.boss ? [bossPill()] : []),
      attitudeText(n.attitude),
    ],
    hp: meter('hp', n.attitude, n.hp, n.hpMax, b.d, b.d.showShield ? n.shield : 0, b.pend),
    rest: tagsFor({ ...n, tempTurns: 0, moveMax: 0 }, b.priv, b.d),
  };
}

export function resolveEntry(state, entry) {
  if (entry.kind === KIND_PLAYER) {
    const p = state.party.find(x => x.hash === entry.ref);
    return p && { tone: KIND_PLAYER, group: p.group, hp: p.hp, hpMax: p.hpMax, shield: p.shield, boss: false };
  }
  if (entry.kind === KIND_MARKER) {
    const m = state.markers.find(x => x.id === entry.ref);
    return m && { tone: m.attitude, hp: m.hp, hpMax: m.hpMax, shield: m.shield, boss: m.boss };
  }
  const n = state.npcs.find(x => x.id === entry.ref);
  return n && { tone: n.attitude, creature: n.creature, hp: n.hp, hpMax: n.hpMax, shield: n.shield, boss: n.boss };
}

function initiativeDetail(entry) {
  if (entry.roll <= 0) return null;
  if (entry.stat) return fmt('Web.InitRollStatFmt', entry.roll, entry.stat, signed(entry.mod));
  if (entry.mod) return fmt('Web.InitRollModFmt', entry.roll, signed(entry.mod));
  return fmt('Web.InitRollFmt', entry.roll);
}

export function turnEntryBody(b) {
  const e = b.e;
  const ent = b.ent;
  const glyph = e.kind === KIND_MARKER ? markerGlyph(e.ref ?? '')
    : e.kind === KIND_NPC ? npcGlyph(yes(ent?.creature))
      : playerGlyph(ent?.group ?? null, e.ref);
  const init = el('span', 'init', e.initiative);
  init.title = t('Web.Initiative');
  const detail = b.priv ? null : initiativeDetail(e);
  return {
    cls: ['turn-row', b.active && 'is-active', e.hasActed && 'is-acted', b.grouped && 'is-grouped'].filter(Boolean).join(' '),
    head: [init, hidden(glyph)],
    name: { text: e.name, muted: false, open: b.open },
    extra: [
      ...(ent?.boss ? [bossPill()] : []),
      ...(b.active && !b.grouped ? [theirTurnPill()] : []),
      ...(e.hasActed && !b.grouped && b.mark ? [actedMark()] : []),
    ],
    pre: detail ? [el('div', 'row-sub', detail)] : [],
    hp: ent ? meter('hp', ent.tone, ent.hp, ent.hpMax, b.d, b.d.showShield ? ent.shield : 0, b.pend) : null,
    rest: [],
  };
}

const groupParts = new WeakMap();

export function patchGroup(node, vm) {
  let parts = node ? groupParts.get(node) : null;
  if (!parts) {
    node = el('li', 'turn-group');
    parts = { lead: el('span', ROW_LEAD), body: el('span', 'turn-group-body'), sig: null };
    node.append(parts.lead, parts.body);
    groupParts.set(node, parts);
  }
  const sig = JSON.stringify(vm.body);
  if (sig !== parts.sig) {
    parts.sig = sig;
    const b = vm.body;
    node.className = 'turn-group' + (b.active ? ' is-active' : '') + (b.acted ? ' is-acted' : '');
    parts.body.replaceChildren(el('span', 'turn-group-name', b.label),
      ...(b.active ? [theirTurnPill()] : b.acted && b.mark ? [actedMark()] : []));
  }
  syncControls(parts.lead, vm.ctl ? [tickSpec(vm.ctl)] : []);
  return node;
}

function rollDetail(r) {
  const parts = [];
  if (r.stat) parts.push(r.stat);
  const count = r.dice && r.dice.length > 1 ? r.dice.length : 1;
  let base = count > 1 ? r.dice.join(' + ') : String(r.raw);
  // L'écart réel et non r.mod : en mode Seuil la stat déplace la cible, le total reste le dé.
  const offset = r.total - r.raw;
  if (offset) base += (offset > 0 ? ' + ' : ' - ') + Math.abs(offset);
  parts.push(base);
  // diceMax est le maximum de toute la formule : il ne se divise en faces que pour des dés identiques.
  if (r.diceMax > 0) parts.push(r.diceMax % count === 0 ? count + 'd' + r.diceMax / count : r.raw + '/' + r.diceMax);
  return parts.join(' · ');
}

export function buildRoll(r) {
  const node = el('li', 'roll');
  const head = el('div', 'roll-head');
  head.append(el('span', 'roll-name', r.roller), el('span', 'roll-total', r.total));

  const sub = el('div', 'roll-sub');
  sub.append(el('span', 'roll-detail', rollDetail(r)));
  if (r.target != null && r.success != null) {
    sub.append(el('span', 'roll-verdict ' + (r.success ? 'is-ok' : 'is-ko'),
      fmt(r.success ? 'Web.RollSuccessFmt' : 'Web.RollFailureFmt', r.target)));
  }
  if (r.at > 0) sub.append(el('span', 'roll-time', clock(r.at)));

  node.append(head, sub);
  return node;
}

export function flash(node) {
  node.classList.add('is-new');
}
