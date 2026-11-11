import { $, el, setCount } from './dom.js';
import { rebuild, reconcile } from './rows.js';
import { clock, fmt } from './texts.js';

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
