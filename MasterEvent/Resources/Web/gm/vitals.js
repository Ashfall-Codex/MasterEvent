import { MERGE_MS, OP_VITALS, VALUE_MAX } from './constants.js';
import { command } from './commands.js';
import { fmt, t } from './i18n.js';
import { renderSoon } from './render.js';
import { app, can, targetName } from './state.js';
import { clamp, signed } from './util.js';

// ── Vitalité : les clics d'une même jauge s'additionnent, une seule commande en vol ──

const vitals = new Map();

export function vitalLabel(field, d) {
  if (field === 'mp') return d.mpLabel || t('Web.Mp');
  if (field === 'shield') return t('Marker.Shield');
  return d.hpLabel || t('Web.Hp');
}

export function queueVital(kind, ref, field, change) {
  if (!can(OP_VITALS)) return;
  const key = kind + ':' + ref + ':' + field;
  let v = vitals.get(key);
  if (!v) {
    v = { key, kind, ref, field, delta: 0, set: null, sent: null, timer: 0 };
    vitals.set(key, v);
  }
  if ('set' in change) {
    v.set = change.set;
    v.delta = 0;
  } else {
    v.delta = clamp(v.delta + change.delta, -VALUE_MAX, VALUE_MAX);
  }
  if (!v.sent) {
    if ('set' in change) {
      clearTimeout(v.timer);
      flushVital(v);
    } else if (!v.timer) {
      v.timer = setTimeout(() => flushVital(v), MERGE_MS);
    }
  }
  renderSoon();
}

function flushVital(v) {
  v.timer = 0;
  if (v.set == null && v.delta === 0) {
    vitals.delete(v.key);
    renderSoon();
    return;
  }
  let fields;
  if (v.set == null) {
    fields = { delta: v.delta };
    v.delta = 0;
  } else {
    fields = { set: v.set };
    v.set = null;
  }
  v.sent = fields;
  const current = app.current;
  const label = fmt('Web.VitalForFmt', vitalLabel(v.field, current.display), targetName(current, v.kind, v.ref));
  const sent = command(OP_VITALS, { kind: v.kind, ref: v.ref, field: v.field, ...fields }, {
    label,
    done: ok => {
      v.sent = null;
      if (!ok) {
        v.delta = 0;
        v.set = null;
      }
      if (vitals.get(v.key) !== v) return false;
      if (v.set != null || v.delta !== 0) flushVital(v);
      else vitals.delete(v.key);
      return false;
    },
  });
  if (!sent) vitals.delete(v.key);
}

// Ce qui part ou attend encore, à côté de la valeur de l'état : la page n'invente pas de valeur.
export function pendingVital(kind, ref, field) {
  const v = vitals.get(kind + ':' + ref + ':' + field);
  if (!v) return null;
  const base = v.set ?? v.sent?.set ?? null;
  if (base != null) return '→ ' + base + (v.delta ? ' ' + signed(v.delta) : '');
  const delta = (v.sent?.delta ?? 0) + v.delta;
  return delta ? signed(delta) : null;
}

export function parseValue(text) {
  const match = /^\s*([+\-−]?)\s*(\d{1,5})\s*$/.exec(text);
  if (!match) return null;
  const n = Number(match[2]);
  if (match[1] === '') return { set: n };
  return { delta: match[1] === '+' ? n : -n };
}

export const hasVitalsInFlight = () => [...vitals.values()].some(v => v.sent || v.set != null || v.delta !== 0);

export function dropVitals() {
  for (const v of vitals.values()) clearTimeout(v.timer);
  vitals.clear();
}
