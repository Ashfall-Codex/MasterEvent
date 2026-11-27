import { notifyStatus } from './commands.js';
import { $, el, setCount } from './dom.js';
import { rebuild, reconcile } from './rows.js';
import { clock, fmt, t } from './texts.js';

// Instant du dernier jet à soi dans l'état précédent ; null tant qu'aucun état joueur ne sert de point de départ.
let ownRollAt = null;

function buildRoll(r) {
  const node = el('li', 'roll');
  const head = el('div', 'roll-head');
  head.append(el('span', 'roll-name', r.roller), el('span', 'roll-total', r.total));

  const sub = el('div', 'roll-sub');
  sub.append(el('span', 'roll-detail', rollDetail(r)));
  if (r.target != null && r.success != null) {
    sub.append(el('span', 'roll-verdict ' + (r.success ? 'is-ok' : 'is-ko'),
      fmt(r.success ? 'Dice.Success' : 'Dice.Failure', r.target)));
  }
  if (r.at > 0) sub.append(el('span', 'roll-time', clock(r.at)));

  node.append(head, sub);
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

function flash(node) {
  node.classList.add('is-new');
}

export function renderRolls(s) {
  setCount('count-rolls', s.rolls.length);
  $('empty-rolls').hidden = s.rolls.length > 0;
  reconcile($('list-rolls'), s.rolls, r => r.at + '|' + r.roller + '|' + r.raw + '|' + r.total, rebuild(buildRoll), flash);
}

// Un hello (premier, reconnexion, nouveau lien) refait la page : le dernier jet qu'il montre n'est pas nouveau.
export function forgetOwnRoll() {
  ownRollAt = null;
}

// L'onglet Ordre du tour, choisi d'office en combat, cache le dernier jet de Ma fiche : son résultat en avis.
export function noticeOwnRoll(s) {
  // Sans accès, pas de dernier jet : celui qui revient avec l'accès n'a rien de nouveau.
  if (s.access !== 'player') {
    ownRollAt = null;
    return;
  }
  const r = s.lastRoll;
  const at = r ? r.at : 0;
  const previous = ownRollAt;
  ownRollAt = at;
  if (previous === null || at <= 0 || at === previous) return;
  const verdict = r.target != null && r.success != null;
  const parts = [String(r.total)];
  if (r.stat) parts.push(r.stat);
  if (verdict) parts.push(fmt(r.success ? 'Dice.Success' : 'Dice.Failure', r.target));
  notifyStatus(t('PlayerView.LastRoll'), parts.join(' · '), !verdict ? 'roll' : r.success ? 'roll-ok' : 'roll-ko');
}
