import { ATTITUDES, PERCENT } from './constants.js';
import { ARIA_LABEL, el } from './dom.js';
import { decimal, fmt, t } from './i18n.js';
import { clamp, percent, signed } from './util.js';
import { vitalLabel } from './vitals.js';

// Les jauges portent le nom choisi par le modèle actif (« Éther », « Mana »), à défaut celui de la page.
export function meter(kind, tone, value, max, d, shield, change) {
  const isPercent = (kind === 'mp' ? d.mpMode : d.hpMode) === PERCENT;
  const name = vitalLabel(kind, d);
  const cap = isPercent ? 100 : max > 0 ? max : 100;
  const ratio = clamp(value / cap, 0, 1);
  const label = isPercent ? value + '%' : value + '/' + max;
  const shieldText = '+' + shield + (isPercent ? '%' : '');

  const box = el('div', 'meter meter-' + kind);
  box.setAttribute('role', 'img');
  box.title = name + ' ' + label + (shield > 0 ? ', ' + fmt('Web.ShieldFmt', shieldText) : '');
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
  if (shield > 0) {
    const extraText = el('span', 'meter-shield-text', shieldText);
    extraText.title = fmt('Web.ShieldFmt', shieldText);
    box.append(extraText);
  }
  if (change) box.append(el('span', 'meter-pending', change));
  return box;
}

export function hasMp(display, max) {
  return display.showMp && (display.mpMode === PERCENT || max > 0);
}

// Un modèle à seuils affiche ses stats sans signe, comme StatDisplay.Stat dans le jeu.
export const statValue = (mod, d) => (d.statTarget ? String(mod) : signed(mod));

export function tagsFor(entity, priv, d) {
  const box = el('div', 'tags');

  if (!priv) {
    for (const stat of entity.stats)
      box.append(el('span', 'tag tag-stat', stat.name + ' ' + statValue(stat.mod, d)));

    for (const counter of entity.counters) {
      const tag = el('span', 'tag tag-counter');
      const dot = el('span', 'dot');
      if (counter.color) dot.style.setProperty('--c', counter.color);
      const amount = counter.max > 0 ? counter.value + '/' + counter.max : String(counter.value);
      tag.append(dot, document.createTextNode(counter.name + ' ' + amount));
      box.append(tag);
    }
  }

  if (entity.tempMod) {
    let label = fmt(entity.tempMod > 0 ? 'Web.TempBonusFmt' : 'Web.TempMalusFmt', signed(entity.tempMod));
    if (entity.tempTurns > 0)
      label += ' · ' + (entity.tempTurns === 1 ? t('Web.TempTurnsOne') : fmt('Web.TempTurnsFmt', entity.tempTurns));
    const tag = el('span', 'tag tag-temp', label);
    tag.title = t('Web.TempTitle');
    box.append(tag);
  }

  if (entity.moveMax > 0)
    box.append(el('span', 'tag tag-move', fmt('Web.MoveFmt', decimal(entity.moveLeft), decimal(entity.moveMax))));

  return box.childElementCount > 0 ? [box] : [];
}

export function attitudeText(value) {
  return el('span', 'trail att-' + value, t(ATTITUDES.get(value)));
}

export function connectionTrail(connected) {
  const box = el('span', 'trail conn ' + (connected ? 'is-on' : 'is-off'));
  box.append(el('span', 'dot'), document.createTextNode(t(connected ? 'Web.Connected' : 'Web.Disconnected')));
  return box;
}

export function actedMark() {
  const mark = el('span', 'trail acted');
  mark.append(el('span', 'check'), document.createTextNode(t('Web.Acted')));
  return mark;
}
