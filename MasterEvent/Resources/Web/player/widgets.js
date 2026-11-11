import { ATTITUDES, PERCENT, WAYMARKS } from './constants.js';
import { ARIA_LABEL, clamp, el, percent, signed } from './dom.js';
import { fmt, t } from './texts.js';

export function markerGlyph(id) {
  return el('span', 'glyph wm ' + (WAYMARKS.get(id) ?? 'wm-other'), id ? Array.from(id)[0] : '?');
}

export function npcGlyph(creature) {
  return el('span', 'glyph ' + (creature ? 'glyph-creature' : 'glyph-npc'));
}

export function dimGlyph() {
  return el('span', 'glyph glyph-dim');
}

export function hidden(node) {
  node.setAttribute('aria-hidden', 'true');
  return node;
}

export function pill(text, tone) {
  return el('span', 'pill pill-' + tone, text);
}

// Les jauges portent le nom choisi par le modèle actif (« Éther », « Mana »), à défaut celui du jeu.
export function vitalLabel(field, d) {
  if (field === 'mp') return d.mpLabel || t('Marker.Mp');
  return d.hpLabel || t('Marker.Hp');
}

export function gaugeRatio(value, max, isPercent) {
  if (isPercent) return value / 100;
  return max > 0 ? value / max : 0;
}

// Jauge compacte d'une ligne, comme ActorRow : « 30/40 » ou « 75% ».
export function meter(kind, tone, value, max, d, shield) {
  const isPercent = (kind === 'mp' ? d.mpMode : d.hpMode) === PERCENT;
  const name = vitalLabel(kind, d);
  const cap = isPercent ? 100 : max > 0 ? max : 100;
  const ratio = clamp(value / cap, 0, 1);
  const label = isPercent ? value + '%' : value + '/' + max;
  const shieldText = '+' + shield + (isPercent ? '%' : '');

  const box = el('div', 'meter meter-' + kind);
  box.setAttribute('role', 'img');
  box.title = name + ' ' + label + (shield > 0 ? ', ' + t('Marker.Shield') + ' ' + shieldText : '');
  box.setAttribute(ARIA_LABEL, box.title);

  const track = el('span', 'meter-track');
  const fill = el('span', 'meter-fill tone-' + tone);
  fill.style.width = percent(ratio);
  track.append(fill);

  if (shield > 0) {
    const extra = el('span', 'meter-shield');
    extra.style.left = percent(ratio);
    extra.style.width = percent(clamp(shield / cap, 0, 1 - ratio));
    track.append(extra);
  }

  box.append(track, el('span', 'meter-text', label));
  if (shield > 0) box.append(el('span', 'meter-shield-text', shieldText));
  return box;
}

// Jauge du détail d'un marqueur, avec son libellé comme les barres d'éther et de compteur du jeu.
export function detailMeter(label, ratio, tone, color) {
  const box = el('div', 'meter meter-mp');
  box.setAttribute('role', 'img');
  box.title = label;
  box.setAttribute(ARIA_LABEL, label);
  const track = el('span', 'meter-track');
  const fill = el('span', 'meter-fill ' + tone);
  if (color) fill.style.setProperty('--c', color);
  fill.style.width = percent(clamp(ratio, 0, 1));
  track.append(fill);
  box.append(track, el('span', 'meter-text', label));
  return box;
}

// Un modèle à seuils affiche ses stats sans signe, comme StatDisplay.Stat dans le jeu.
export const statValue = (mod, d) => (d.statTarget ? String(mod) : signed(mod));

export function tempTag(modifier, rounds) {
  let label = fmt(modifier > 0 ? 'PlayerView.TempBonusFmt' : 'PlayerView.TempMalusFmt', signed(modifier));
  if (rounds > 0)
    label += ' · ' + (rounds === 1 ? t('PlayerView.TempRoundsLeftOne') : fmt('PlayerView.TempRoundsLeftFmt', rounds));
  return el('span', 'tag tag-temp', label);
}

export function attitudeText(value) {
  return el('span', 'trail att-' + value, t(ATTITUDES.get(value)));
}

export function presence(connected) {
  const box = el('span', 'trail conn ' + (connected ? 'is-on' : 'is-off'));
  box.append(el('span', 'dot'), document.createTextNode(t(connected ? 'Group.Connected' : 'Group.Disconnected')));
  return box;
}

export function readOnlyTick(acted) {
  const tick = el('span', 'tick p-tick');
  tick.setAttribute('role', 'checkbox');
  tick.setAttribute('aria-readonly', 'true');
  tick.setAttribute('aria-checked', acted ? 'true' : 'false');
  tick.setAttribute(ARIA_LABEL, t('PlayerWeb.Acted'));
  return tick;
}
