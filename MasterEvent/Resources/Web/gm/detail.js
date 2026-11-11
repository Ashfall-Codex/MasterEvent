import { ACTION_DETAIL_CLOSE, ACTION_ROLL_STAT, ATTITUDES, KIND_MARKER, KIND_NPC, KIND_PLAYER, OP_ROLL_REQUEST, OP_VITALS, PERCENT } from './constants.js';
import { rollCtl, rollStats, stepSpecs } from './controls.js';
import { $, ARIA_DISABLED, ARIA_INVALID, ARIA_LABEL, control, el, hidden, icon, setAttr, setBusy, setData, setText } from './dom.js';
import { fmt, t } from './i18n.js';
import { closeMenu } from './menu.js';
import { hasMp, statValue } from './parts.js';
import { markerGlyph, npcGlyph, playerGlyph, portraitRev, recycling } from './portraits.js';
import { syncControls } from './reconcile.js';
import { render } from './render.js';
import { app, can, displayName, entityOf } from './state.js';
import { parseValue, pendingVital, queueVital, vitalLabel } from './vitals.js';

// ── Détail : jauges, jet, demande de jet ──

const DETAIL = 'detail';
const GAUGE = '.gauge';
const GAUGE_INPUT = '.gauge-input';
const IS_INVALID = 'is-invalid';

let detailShell = null;
let detailOpener = null;
const gaugeParts = new WeakMap();

export function forgetDetailShell() {
  detailShell = null;
}

function buildDetailShell(box, key) {
  const head = el('header', 'detail-head');
  const glyph = el('span', 'detail-glyph');
  const titles = el('div', 'detail-titles');
  const name = el('h2', 'detail-name');
  name.id = 'detail-name';
  const kind = el('span', 'detail-kind');
  titles.append(name, kind);
  const close = control('icon-btn', ACTION_DETAIL_CLOSE);
  close.append(icon('close'));
  close.title = t('Scene.CloseDetail');
  close.setAttribute(ARIA_LABEL, t('Scene.CloseDetail'));
  head.append(glyph, titles, close);

  const gone = el('p', 'detail-gone', t('Web.Err.gone'));
  const gauges = el('div', 'detail-gauges');
  const roll = el('div', 'detail-roll');
  const rollButtons = el('div', 'detail-roll-buttons');
  roll.append(el('span', 'detail-label', t('Web.Roll')), rollButtons);
  const ask = el('div', 'detail-ask');
  const askButton = control('btn btn-tint', 'ask', t('Group.AskRoll'));
  askButton.setAttribute('aria-haspopup', 'dialog');
  ask.append(askButton);

  box.setAttribute(ARIA_LABEL, t('Web.Detail'));
  box.replaceChildren(head, gone, gauges, roll, ask);
  return { key, glyph, glyphSig: null, name, kind, close, gone, gauges, roll, rollButtons, ask, askButton, lastName: '' };
}

function detailGlyph(kind, entity, ref) {
  if (kind === KIND_PLAYER) return playerGlyph(entity.group, ref);
  if (kind === KIND_MARKER) return markerGlyph(ref);
  return npcGlyph(entity.creature);
}

function detailKind(kind, entity) {
  if (kind === KIND_NPC) return t(entity.creature ? 'Web.Creature' : 'Web.Npc');
  if (kind === KIND_MARKER) return t(ATTITUDES.get(entity.attitude));
  return t(entity.connected ? 'Web.Connected' : 'Web.Disconnected');
}

function renderDetailRoll(sh, s, kind, ref, entity) {
  const roll = rollCtl(kind, ref, entity, sh.lastName);
  sh.roll.hidden = !roll;
  if (!roll) return;
  const stats = rollStats(entity);
  const choices = stats.length > 0
    ? [{ id: '', text: t('Dice.NoStat') }, ...stats.map(st => ({ id: st.id, text: st.name + ' ' + statValue(st.mod, s.display) }))]
    : [{ id: '', text: t('Marker.Roll') }];
  syncControls(sh.rollButtons, choices.map(choice => ({
    k: 's:' + choice.id,
    make: () => control('btn btn-small', ACTION_ROLL_STAT),
    update: button => {
      setText(button, choice.text);
      setData(button, { kind, ref, stat: choice.id });
      setBusy(button, roll.busy, roll.unnamed);
      button.title = roll.unnamed ? t('Marker.RollDisabled') : '';
    },
  })));
}

function renderDetailAsk(sh, kind, ref, entity) {
  const askable = !!entity && kind === KIND_PLAYER && can(OP_ROLL_REQUEST) && !app.privateHidden && !entity.isGm;
  sh.ask.hidden = !askable;
  if (askable) {
    setData(sh.askButton, { ref });
    setAttr(sh.askButton, ARIA_DISABLED, entity.connected ? null : 'true');
    sh.askButton.title = entity.connected ? '' : t('Web.Err.targetOffline');
  }
}

export function renderDetail(s) {
  const box = $(DETAIL);
  // Le co-MJ ne voit que les marqueurs : un joueur ou un PNJ ne lui est pas « parti », il lui est caché.
  if (app.detail && (s.access === 'none' || (s.access === 'coGm' && app.detail.kind !== KIND_MARKER))) app.detail = null;
  const open = !!app.detail;
  box.hidden = !open;
  $('app').classList.toggle('has-detail', open);
  document.body.classList.toggle('has-sheet', open);
  if (!open) {
    if (detailShell) {
      box.replaceChildren();
      detailShell = null;
    }
    return;
  }

  const { kind, ref } = app.detail;
  const key = kind + ':' + ref;
  if (!detailShell || detailShell.key !== key) detailShell = buildDetailShell(box, key);
  const sh = detailShell;
  const entity = entityOf(s, kind, ref);
  sh.gone.hidden = !!entity;
  if (entity) {
    sh.lastName = displayName(kind, entity, ref);
    const glyphSig = JSON.stringify([kind, entity.group, entity.creature, kind === KIND_PLAYER ? portraitRev(ref) : 0]);
    if (glyphSig !== sh.glyphSig) {
      sh.glyphSig = glyphSig;
      sh.glyph.replaceChildren(hidden(recycling(sh.glyph, () => detailGlyph(kind, entity, ref))));
    }
    setText(sh.kind, detailKind(kind, entity));
  }
  setText(sh.name, sh.lastName);
  syncControls(sh.gauges, entity ? gaugeSpecs(s, kind, ref, entity) : []);
  renderDetailRoll(sh, s, kind, ref, entity);
  renderDetailAsk(sh, kind, ref, entity);
}

// Bouclier, PV puis PM, dans l'ordre de l'éditeur de vitales du jeu.
function gaugeSpecs(s, kind, ref, entity) {
  const d = s.display;
  const hpPct = d.hpMode === PERCENT;
  const gauges = [];
  if (d.showShield) gauges.push({ field: 'shield', value: entity.shield, max: entity.hpMax, pct: hpPct });
  gauges.push({ field: 'hp', value: entity.hp, max: entity.hpMax, pct: hpPct });
  if (kind !== KIND_NPC && hasMp(d, entity.mpMax)) gauges.push({ field: 'mp', value: entity.mp, max: entity.mpMax, pct: d.mpMode === PERCENT });
  const name = displayName(kind, entity, ref);
  return gauges.map(g => ({ k: g.field, make: makeGauge, update: row => updateGauge(row, kind, ref, name, g, d) }));
}

function stepButton(dir, iconName) {
  const button = control('step', 'step');
  button.dataset.dir = dir;
  button.append(icon(iconName));
  return button;
}

function makeGauge() {
  const row = el('div', 'gauge');
  const label = el('span', 'gauge-label');
  const box = el('span', 'gauge-ctl');
  const down = stepButton('-1', 'minus');
  const input = el('input', 'gauge-input');
  input.type = 'text';
  input.autocomplete = 'off';
  input.spellcheck = false;
  input.maxLength = 8;
  input.setAttribute('enterkeyhint', 'done');
  const up = stepButton('1', 'plus');
  box.append(down, input, up);
  const meta = el('span', 'gauge-meta');
  const change = el('span', 'gauge-pending');
  const hint = el('p', 'gauge-hint', t('Web.ValueHint'));
  hint.hidden = true;
  row.append(label, box, meta, change, hint);
  gaugeParts.set(row, { label, down, input, up, meta, change, hint });
  return row;
}

function updateGauge(row, kind, ref, name, g, d) {
  const p = gaugeParts.get(row);
  const label = vitalLabel(g.field, d);
  setText(p.label, label);
  const editable = can(OP_VITALS);
  p.down.hidden = !editable;
  p.up.hidden = !editable;
  p.input.readOnly = !editable;
  const target = { kind, ref, field: g.field };
  setData(p.down, target);
  setData(p.up, target);
  setData(p.input, target);
  const step = stepSpecs({ kind, ref, field: g.field, pct: g.pct, name });
  step[0].update(p.down);
  step[1].update(p.up);
  p.hint.id = 'gauge-hint-' + g.field;
  setAttr(p.input, 'aria-describedby', p.hint.id);
  setAttr(p.input, ARIA_LABEL, fmt('Web.VitalForFmt', label, name));
  // Le champ en cours de saisie n'est jamais réécrit par un état entrant.
  if (document.activeElement !== p.input) {
    if (p.input.value !== String(g.value)) p.input.value = String(g.value);
    clearInvalid(p);
  }
  setText(p.meta, g.pct ? '%' : '/ ' + g.max);
  setText(p.change, pendingVital(kind, ref, g.field) ?? '');
}

function clearInvalid(p) {
  p.input.classList.remove(IS_INVALID);
  p.input.removeAttribute(ARIA_INVALID);
  p.hint.hidden = true;
}

function restoreGauge(input) {
  const p = gaugeParts.get(input.closest(GAUGE));
  const entity = entityOf(app.current, input.dataset.kind, input.dataset.ref);
  if (p && entity) input.value = String(entity[input.dataset.field] ?? '');
  if (p) clearInvalid(p);
}

function submitGauge(input) {
  const p = gaugeParts.get(input.closest(GAUGE));
  if (!p || input.readOnly) return;
  const change = parseValue(input.value);
  if (!change) {
    input.classList.add(IS_INVALID);
    input.setAttribute(ARIA_INVALID, 'true');
    p.hint.hidden = false;
    return;
  }
  clearInvalid(p);
  if (!('delta' in change) || change.delta !== 0) queueVital(input.dataset.kind, input.dataset.ref, input.dataset.field, change);
  input.blur();
}

export function openDetail(button) {
  const { kind, ref } = button.dataset;
  closeMenu(false);
  app.detail = { kind, ref };
  detailOpener = button;
  render();
  const box = $(DETAIL);
  if (box.scrollIntoView && box.getBoundingClientRect().top < 0) box.scrollIntoView({ block: 'nearest' });
  detailShell?.close.focus({ preventScroll: true });
}

export function closeDetail() {
  app.detail = null;
  render();
  if (detailOpener?.isConnected) detailOpener.focus({ preventScroll: true });
  detailOpener = null;
}

const gaugeInputOf = event => (event.target instanceof Element ? event.target.closest(GAUGE_INPUT) : null);

export function bindDetail() {
  $(DETAIL).addEventListener('keydown', event => {
    const input = gaugeInputOf(event);
    if (!input) return;
    if (event.key === 'Enter') {
      event.preventDefault();
      submitGauge(input);
    } else if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      restoreGauge(input);
      input.select();
    }
  });

  $(DETAIL).addEventListener('focusout', event => {
    const input = gaugeInputOf(event);
    if (input) restoreGauge(input);
  });
}
