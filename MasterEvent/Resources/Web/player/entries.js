import { KIND, PERCENT } from './constants.js';
import { el, signed } from './dom.js';
import { playerGlyph } from './portraits.js';
import { patchRow } from './rows.js';
import { t } from './texts.js';
import {
  attitudeText, detailMeter, dimGlyph, gaugeRatio, hidden, markerGlyph, meter, npcGlyph, pill, presence, readOnlyTick, tempTag,
  vitalLabel,
} from './widgets.js';

const PLAYER_TONE = 'player';
const tickSpace = () => el('span', 'p-tick-space');
const bossPill = () => pill(t('Marker.Boss'), 'accent');

// Ce que le MJ partage d'un marqueur en plus de ses PV ; jamais ses stats.
function markerDetail(m, d) {
  const box = el('div', 'p-detail');
  if (d.showMp && m.mp != null) {
    const max = m.mpMax ?? 0;
    const pct = d.mpMode === PERCENT;
    box.append(detailMeter(vitalLabel('mp', d) + ': ' + (pct ? m.mp + '%' : m.mp + ' / ' + max),
      gaugeRatio(m.mp, max, pct), 'tone-mp', null));
  }
  for (const c of m.counters)
    box.append(detailMeter(c.name + ': ' + c.value + ' / ' + c.max, gaugeRatio(c.value, c.max, false), 'tone-counter', c.color));
  if (m.tempModifier) {
    const tags = el('div', 'tags');
    tags.append(tempTag(m.tempModifier, m.tempRounds));
    box.append(tags);
  }
  if (m.lastRoll > 0) {
    const roll = el('div', 'p-detail-roll');
    roll.append(el('span', null, t('PlayerView.LastRoll')), el('strong', null, m.lastRoll + ' / ' + m.lastRollMax));
    box.append(roll);
  }
  if (!box.firstChild) box.append(el('p', 'p-quiet', t('PlayerView.NoDetail')));
  return box;
}

function markerName(f, open) {
  return {
    text: f.name,
    action: 'detail',
    ref: f.ref,
    expanded: open,
    title: t(open ? 'PlayerWeb.HideDetail' : 'PlayerWeb.ShowDetail'),
  };
}

function fieldBody(vm) {
  const f = vm.f;
  const marker = f.kind === KIND.marker;
  return {
    lead: vm.reserve ? [tickSpace()] : [],
    head: [hidden(marker ? markerGlyph(f.ref) : npcGlyph(f.creature))],
    name: marker ? markerName(f, vm.open) : { text: f.name },
    extra: [...(f.boss ? [bossPill()] : []), attitudeText(f.attitude)],
    hp: meter('hp', f.attitude, f.hp, f.hpMax, vm.d, vm.d.showShield ? f.shield : 0),
    rest: marker && vm.open ? [markerDetail(f, vm.d)] : [],
  };
}

function playerBody(vm) {
  const p = vm.p;
  return {
    cls: p.connected ? '' : 'is-dim',
    title: p.connected ? null : t('Group.Disconnected'),
    lead: vm.reserve ? [tickSpace()] : [],
    head: [hidden(playerGlyph(p.hash))],
    name: { text: p.name, muted: !p.connected },
    extra: [...(p.coGm ? [pill(t('Group.CoGm'), 'accent')] : []), presence(p.connected)],
    hp: meter('hp', PLAYER_TONE, p.hp, p.hpMax, vm.d, vm.d.showShield ? p.shield : 0),
  };
}

// Comme TurnText.InitiativeTooltip ; sans jet d'initiative, le seul mot.
function initiativeTitle(e) {
  if (e.roll <= 0) return t('PlayerWeb.Initiative');
  if (!e.stat) return t('Turns.InitRoll') + ': ' + e.roll;
  return t('Turns.InitRoll') + ': ' + e.roll + ' (' + e.stat + ' ' + signed(e.mod) + ') = ' + e.initiative;
}

function turnGlyph(e, x) {
  if (e.kind === KIND.marker && e.ref) return markerGlyph(e.ref);
  if (e.kind === KIND.npc) return npcGlyph(Boolean(x?.creature));
  if (e.kind === KIND.player && x) return playerGlyph(e.ref);
  return dimGlyph();
}

function turnExtras(e, x, myTurn) {
  const extra = [];
  if (e.mine) extra.push(pill(t('PlayerView.YouPill'), PLAYER_TONE));
  if (x?.boss) extra.push(bossPill());
  // « À vous » sur sa seule ligne : la barre dit déjà que tout le bloc est à lui.
  if (e.active) extra.push(pill(t(e.mine && myTurn ? 'PlayerView.YourTurnPill' : 'Scene.TheirTurn'), 'pending'));
  if (x && e.kind !== KIND.player) extra.push(attitudeText(x.attitude));
  return extra;
}

function turnName(e, x, open) {
  if (e.kind === KIND.marker && x) return markerName(x, open);
  if (e.mine) return { text: e.name, action: 'focus-sheet', title: t('PlayerWeb.FocusSheet') };
  return { text: e.name };
}

function turnBody(vm) {
  const e = vm.e;
  const x = vm.x;
  const glyph = turnGlyph(e, x);
  const init = el('span', 'init', e.initiative);
  init.title = initiativeTitle(e);
  const extra = turnExtras(e, x, vm.myTurn);
  const name = turnName(e, x, vm.open);

  return {
    cls: ['turn-row', e.active && 'is-active', e.acted && 'is-acted', e.group && 'is-grouped'].filter(Boolean).join(' '),
    title: e.kind === KIND.player && x && !e.mine && !x.connected ? t('Group.Disconnected') : null,
    lead: [readOnlyTick(e.acted)],
    head: [init, hidden(glyph)],
    name,
    extra,
    hp: x ? meter('hp', e.kind === KIND.player ? PLAYER_TONE : x.attitude, x.hp, x.hpMax, vm.d, vm.d.showShield ? x.shield : 0) : null,
    rest: e.kind === KIND.marker && x && vm.open ? [markerDetail(x, vm.d)] : [],
  };
}

function buildGroup(vm) {
  const node = el('li', 'turn-group' + (vm.active ? ' is-active' : '') + (vm.acted ? ' is-acted' : ''));
  node.append(tickSpace(), el('span', 'turn-group-name', vm.label));
  return node;
}

export function makeRow(node, vm) {
  if (vm.k === 'g') return buildGroup(vm);
  return patchRow(node, vm, vm.k === 'f' ? fieldBody : vm.k === 'p' ? playerBody : turnBody);
}

export function rowKey(vm) {
  if (vm.k === 'g') return 'g:' + vm.id;
  if (vm.k === 'f') return 'f:' + vm.f.kind + ':' + vm.f.ref;
  if (vm.k === 'p') return 'p:' + vm.p.hash;
  return 'e:' + vm.e.kind + ':' + (vm.e.ref ?? vm.e.name);
}
