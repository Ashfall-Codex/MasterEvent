import { FALLBACK } from './constants.js';

let strings = new Map();
export const t = key => strings.get(key) ?? FALLBACK.get(key) ?? key;
export const fmt = (key, ...args) =>
  t(key).replace(/\{(\d+)(?::[^}]*)?\}/g, (match, index) => (Number(index) < args.length ? String(args[Number(index)]) : match));

export const hasString = key => strings.has(key);

export function setStrings(values) {
  strings = new Map();
  for (const [key, value] of Object.entries(values))
    if (typeof value === 'string') strings.set(key, value);
}

let numberFormat = null;
let timeFormat = null;

export function resetFormats() {
  numberFormat = null;
  timeFormat = null;
}

function intl(make) {
  try { return make(document.documentElement.lang || 'fr'); } catch { return make('fr'); }
}

export function decimal(value) {
  numberFormat ??= intl(lang => new Intl.NumberFormat(lang, { maximumFractionDigits: 1 }));
  return numberFormat.format(value);
}

export function clock(ms) {
  timeFormat ??= intl(lang => new Intl.DateTimeFormat(lang, { hour: '2-digit', minute: '2-digit' }));
  return timeFormat.format(new Date(ms));
}
