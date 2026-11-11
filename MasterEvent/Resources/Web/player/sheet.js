import { LOCK, OP, PERCENT, ROOM_VIEWS, STAT_FILTER_THRESHOLD } from './constants.js';
import { can, locks } from './commands.js';
import { $, clamp, control, el, icon, percent, setAttr, setBusy, setData, setText, setTitle, signed } from './dom.js';
import { playerGlyph, portraitRev, recycling } from './portraits.js';
import { changed, syncControls } from './rows.js';
import { decimal, fmt, t } from './texts.js';
import { gaugeRatio, hidden, pill, statValue, tempTag, vitalLabel } from './widgets.js';

let statQuery = '';
// Le portrait ouvre la ligne d'identité, comme DrawOwnPortrait ; son nœud reste quand la ligne change.
const sheetAvatar = el('span', 'p-avatar');

export const hasStatQuery = () => statQuery !== '';

export function setStatQuery(value) {
  statQuery = value;
}

// Sans accents ni casse, comme la recherche des réglages : « erudition » trouve « Érudition ».
const fold = text => text.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();
const matches = (stat, query) => query.trim() === '' || fold(stat.name).includes(fold(query.trim()));

// « Acrobaties (Dex) » : nom et base, découpés comme StatGrid.ToTile.
function toTile(stat) {
  let name = stat.name.trim();
  let basis = '';
  const open = name.lastIndexOf('(');
  if (name.endsWith(')') && open > 0 && open < name.length - 2) {
    basis = name.slice(open + 1, -1).trim();
    name = name.slice(0, open).trimEnd();
  }
  return { stat, name, basis };
}

const tileParts = new WeakMap();

function tileSpecs(tiles, s, highlighted, highlightTip) {
  const d = s.display;
  const temp = s.me.tempModifier;
  const rolling = locks.get(LOCK.roll)?.value;
  return tiles.map(tile => ({
    k: 's:' + tile.stat.id,
    make: () => {
      const button = control('p-tile', 'roll-stat');
      const name = el('span', 'p-tile-name');
      const label = el('span', 'p-tile-label');
      const basis = el('span', 'p-tile-basis');
      name.append(label, basis);
      const value = el('span', 'p-tile-value');
      button.append(name, icon('die'), value);
      tileParts.set(button, { label, basis, value });
      return button;
    },
    update: button => {
      const p = tileParts.get(button);
      const mod = tile.stat.mod;
      const value = statValue(mod, d);
      setText(p.label, tile.name);
      setText(p.basis, tile.basis);
      setText(p.value, value);
      p.value.className = 'p-tile-value' + (d.statTarget || mod > 0 ? '' : mod < 0 ? ' is-neg' : ' is-zero');
      const requested = highlighted != null && highlighted === tile.stat.id;
      button.classList.toggle('is-requested', requested);
      setData(button, { stat: tile.stat.id });

      const lines = [];
      if (tile.basis) lines.push(tile.stat.name);
      if (requested && highlightTip) lines.push(highlightTip);
      lines.push(temp !== 0
        ? fmt('StatGrid.RollBonusFmt', tile.name, value, signed(temp))
        : fmt('StatGrid.RollFmt', tile.name, value));
      setTitle(button, lines.join('\n'));
      const rollLine = lines[lines.length - 1];
      setAttr(button, 'aria-label', requested && highlightTip ? highlightTip + '. ' + rollLine : rollLine);
      setBusy(button, rolling === tile.stat.id, !can(OP.selfRoll));
    },
  }));
}

function sheetBar(label, value, ratio, fillClass, shieldRatio, color) {
  const box = el('div', 'p-bar');
  box.setAttribute('role', 'img');
  box.setAttribute('aria-label', label + ' ' + value);
  const head = el('div', 'p-bar-head');
  head.append(el('span', 'p-bar-label', label), el('span', 'p-bar-value', value));
  const track = el('span', 'p-bar-track');
  const fill = el('span', 'p-bar-fill ' + fillClass);
  fill.style.width = percent(clamp(ratio, 0, 1));
  if (color) fill.style.setProperty('--c', color);
  track.append(fill);
  if (shieldRatio > 0) {
    const shield = el('span', 'p-bar-shield');
    shield.style.left = percent(clamp(ratio, 0, 1));
    shield.style.width = percent(clamp(shieldRatio, 0, 1 - clamp(ratio, 0, 1)));
    track.append(shield);
  }
  box.append(head, track);
  return box;
}

// Textes de HpBar : « 30 (+5) / 40 », ou « 75% (+10%) » en pourcentage.
function sheetGauges(me, d) {
  const nodes = [];
  const shield = d.showShield ? me.shield : 0;
  const hpPercent = d.hpMode === PERCENT;
  const hpRatio = gaugeRatio(me.hp, me.hpMax, hpPercent);
  const hpText = hpPercent
    ? me.hp + '%' + (shield > 0 ? ' (+' + shield + '%)' : '')
    : me.hp + (shield > 0 ? ' (+' + shield + ')' : '') + ' / ' + me.hpMax;
  const shieldRatio = shield > 0 ? gaugeRatio(shield, me.hpMax, hpPercent) : 0;
  nodes.push(sheetBar(vitalLabel('hp', d), hpText, hpRatio, 'tone-player' + (hpRatio <= 0.25 ? ' is-low' : ''), shieldRatio, null));

  if (d.showMp) {
    const mpPercent = d.mpMode === PERCENT;
    const mpRatio = gaugeRatio(me.mp, me.mpMax, mpPercent);
    nodes.push(sheetBar(vitalLabel('mp', d), mpPercent ? me.mp + '%' : me.mp + ' / ' + me.mpMax, mpRatio, 'tone-mp', 0, null));
  }

  for (const c of me.counters)
    nodes.push(sheetBar(c.name, c.value + ' / ' + c.max, gaugeRatio(c.value, c.max, false), 'tone-counter', 0, c.color));
  return nodes;
}

function lastRollNodes(r, d) {
  const parts = [];
  if (r.stat) parts.push(r.stat + ' ' + statValue(r.statMod ?? r.mod - (r.tempMod ?? 0), d));
  if (r.tempMod != null) parts.push(fmt('PlayerView.BonusFmt', signed(r.tempMod)));
  let detail = r.raw + ' / ' + r.diceMax;
  if (parts.length > 0) detail += ' · ' + parts.join(', ');
  const nodes = [
    el('span', 'p-last-label', t('PlayerView.LastRoll')),
    el('span', 'p-last-total', r.total),
    el('span', 'p-last-detail', detail),
  ];
  if (r.target != null && r.success != null)
    nodes.push(el('span', 'p-verdict ' + (r.success ? 'is-ok' : 'is-ko'), fmt(r.success ? 'Dice.Success' : 'Dice.Failure', r.target)));
  return nodes;
}

export function renderSheet(s) {
  const me = s.me;
  const d = s.display;
  setText($('sheet-name'), me ? me.name : t('PlayerView.MySheet'));

  for (const id of ['sheet-gauges', 'sheet-temp', 'tiles-main', 'tiles-skills', 'sheet-free'])
    $(id).hidden = !me;
  if (!me) {
    if (changed('identity', null)) $('sheet-identity').replaceChildren(el('span', null, t('Sheets.NoneLoaded')));
    $('sheet-move').hidden = true;
    $('stat-search-box').hidden = true;
    $('sheet-stats-empty').hidden = true;
    $('sheet-last').hidden = true;
    return;
  }

  if (changed('avatar', [me.hash, portraitRev(me.hash)]))
    sheetAvatar.replaceChildren(hidden(recycling(sheetAvatar, () => playerGlyph(me.hash))));

  if (changed('identity', [me.myTurn, me.sheet, s.template])) {
    const nodes = [sheetAvatar];
    if (me.myTurn) nodes.push(pill(t('PlayerView.YourTurnPill'), 'pending'));
    nodes.push(el('span', null, me.sheet
      ? fmt('PlayerView.SheetNameFmt', me.sheet) + (s.template ? ' · ' + s.template : '')
      : t('Sheets.NoneLoaded')));
    $('sheet-identity').replaceChildren(...nodes);
  }

  if (changed('gauges', [me.hp, me.hpMax, me.mp, me.mpMax, me.shield, me.counters, d]))
    $('sheet-gauges').replaceChildren(...sheetGauges(me, d));

  if (changed('temp', [me.tempModifier, me.tempRounds]))
    $('sheet-temp').replaceChildren(...(me.tempModifier !== 0 ? [tempTag(me.tempModifier, me.tempRounds)] : []));

  // Le quota du tour en cours, seulement pendant son tour, comme le bandeau tactique.
  const move = $('sheet-move');
  move.hidden = !(me.myTurn && me.moveMax > 0);
  if (!move.hidden) {
    const ratio = clamp(me.moveLeft / me.moveMax, 0, 1);
    setText($('move-value'), fmt('Tactical.MovementValue', decimal(me.moveLeft), decimal(me.moveMax)));
    setData(move, { level: ratio <= 0 ? 'out' : ratio <= 0.25 ? 'low' : 'ok' });
    $('move-fill').style.width = percent(ratio);
  }

  renderStats(s);

  setBusy($('roll-free'), locks.get(LOCK.roll)?.value === '', !can(OP.selfRoll));

  const last = $('sheet-last');
  last.hidden = !s.lastRoll;
  if (s.lastRoll && changed('last', [s.lastRoll, d.statTarget])) last.replaceChildren(...lastRollNodes(s.lastRoll, d));
}

function renderStats(s) {
  const me = s.me;
  const stats = me.stats;
  // En mode cible, le seuil du MJ remplace la valeur de la stat : la surligner tromperait.
  const request = ROOM_VIEWS.has(s.view) ? s.request : null;
  const highlighted = request && !s.display.statTarget ? request.statId : null;
  const highlightTip = request ? fmt('PlayerView.RequestedTileFmt', request.threshold) : null;

  const search = $('stat-search');
  const searchable = stats.length > STAT_FILTER_THRESHOLD;
  $('stat-search-box').hidden = !searchable;
  // Sans champ pour l'effacer, une saisie restante masquerait des tuiles.
  if (!searchable && statQuery !== '') {
    statQuery = '';
    search.value = '';
  }
  $('stat-search-clear').hidden = statQuery === '';

  // La stat que le MJ attend reste visible, quoi qu'on cherche.
  const shown = stats.filter(st => (request?.statId != null && st.id === request.statId) || matches(st, statQuery));
  const empty = $('sheet-stats-empty');
  const message = stats.length === 0 ? t(s.template ? 'Player.NoStats' : 'Player.NoTemplate')
    : shown.length === 0 ? t('Sheets.NoStatMatch') : '';
  setText(empty, message);
  empty.hidden = message === '';

  const tiles = shown.map(toTile);
  const abilities = tiles.filter(x => x.basis === '');
  const skills = tiles.filter(x => x.basis !== '');
  const split = abilities.length > 0 && skills.length > 0;
  syncControls($('tiles-main'), tileSpecs(split ? abilities : tiles, s, highlighted, highlightTip));
  syncControls($('tiles-skills'), tileSpecs(split ? skills : [], s, highlighted, highlightTip));
}
