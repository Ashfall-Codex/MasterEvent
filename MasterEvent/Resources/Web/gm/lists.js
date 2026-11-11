import { KIND_MARKER, KIND_NPC, KIND_PLAYER, OP_SET_ACTED } from './constants.js';
import { isBusy, lockedValue } from './commands.js';
import { actedLock, hpCtl, opens, rollCtl, rowControls } from './controls.js';
import { $, setCount } from './dom.js';
import { fmt, t } from './i18n.js';
import { portraitRev } from './portraits.js';
import { rebuild, reconcile } from './reconcile.js';
import { buildRoll, flash, markerBody, npcBody, patchGroup, patchRow, playerBody, resolveEntry, turnEntryBody } from './rows.js';
import { app, can, displayName, entityOf } from './state.js';
import { pendingVital } from './vitals.js';

const LIST_TURN = 'list-turn';

// Ligne d'en-tête d'un groupe d'initiative, avant son premier membre.
function groupItem(turn, e, i, active, ticks) {
  let end = i;
  while (end < turn.entries.length && turn.entries[end].groupId === e.groupId) end++;
  const acted = turn.entries.slice(i, end).every(x => x.hasActed);
  const label = e.group ?? t('Web.TurnGroupUnnamed');
  const lock = actedLock(e.groupId);
  return {
    k: 'g', id: e.groupId,
    body: { label, active: !!active && active.groupId === e.groupId, acted, mark: !ticks },
    ctl: ticks ? { group: e.groupId, name: label, busy: isBusy(lock), value: isBusy(lock) ? lockedValue(lock) : acted } : null,
  };
}

function entryItem(s, e, i, active, ticks) {
  const ent = resolveEntry(s, e) ?? null;
  const entity = entityOf(s, e.kind, e.ref);
  const tickable = ticks && !e.groupId && !!e.ref;
  const lock = actedLock(null, e.kind, e.ref);
  return {
    k: 'e', e,
    body: {
      e,
      active: !!active && (s.turn.activeIndex === i || (!!e.groupId && active.groupId === e.groupId)),
      grouped: !!e.groupId,
      ent,
      pic: e.kind === KIND_PLAYER ? portraitRev(e.ref) : 0,
      d: s.display,
      priv: app.privateHidden,
      mark: !tickable,
      pend: ent ? pendingVital(e.kind, e.ref, 'hp') : null,
      open: entity && opens(e.kind) ? { kind: e.kind, ref: e.ref } : null,
    },
    ctl: {
      tick: tickable ? { kind: e.kind, ref: e.ref, name: e.name, busy: isBusy(lock), value: isBusy(lock) ? lockedValue(lock) : e.hasActed } : null,
      hp: ent && e.ref ? hpCtl(e.kind, e.ref, e.name, s.display) : null,
      roll: rollCtl(e.kind, e.ref, entity, e.name),
    },
  };
}

export function renderTurn(s) {
  const section = $('sec-turn');
  section.hidden = !s.turn;
  if (!s.turn) {
    reconcile($(LIST_TURN), [], null, null);
    return;
  }

  const turn = s.turn;
  $('turn-sub').textContent = turn.active ? fmt('Web.TurnSubtitleFmt', turn.diceMax) : t('Web.TurnIdle');

  const ticks = turn.active && can(OP_SET_ACTED);
  const active = turn.activeIndex >= 0 && turn.activeIndex < turn.entries.length ? turn.entries[turn.activeIndex] : null;
  const items = [];
  let previousGroup = null;
  turn.entries.forEach((e, i) => {
    if (e.groupId && e.groupId !== previousGroup) items.push(groupItem(turn, e, i, active, ticks));
    previousGroup = e.groupId;
    items.push(entryItem(s, e, i, active, ticks));
  });

  reconcile($(LIST_TURN), items,
    vm => (vm.k === 'g' ? 'g:' + vm.id : 'e:' + vm.e.kind + ':' + (vm.e.ref ?? vm.e.name)),
    (node, vm) => (vm.k === 'g' ? patchGroup(node, vm) : patchRow(node, vm, turnEntryBody, rowControls)));
}

// Le relais ne donne pas la liste au co-MJ : « Aucun joueur » serait faux.
export function renderPlayers(s) {
  $('sec-players').hidden = s.access === 'coGm';
  setCount('count-players', s.party.length);
  $('empty-players').hidden = s.party.length > 0;
  reconcile($('list-players'), s.party.map(p => ({
    hash: p.hash,
    body: {
      p, d: s.display, priv: app.privateHidden, pic: portraitRev(p.hash), pend: pendingVital(KIND_PLAYER, p.hash, 'hp'),
      open: opens(KIND_PLAYER) ? { kind: KIND_PLAYER, ref: p.hash } : null,
    },
    ctl: { hp: hpCtl(KIND_PLAYER, p.hash, p.name, s.display) },
  })), vm => 'p:' + vm.hash, (node, vm) => patchRow(node, vm, playerBody, rowControls));
}

function markerItem(s, m) {
  const name = displayName(KIND_MARKER, m, m.id);
  return {
    k: 'm', id: m.id,
    body: { m, d: s.display, priv: app.privateHidden, pend: pendingVital(KIND_MARKER, m.id, 'hp'), open: opens(KIND_MARKER) ? { kind: KIND_MARKER, ref: m.id } : null },
    ctl: { hp: hpCtl(KIND_MARKER, m.id, name, s.display), roll: rollCtl(KIND_MARKER, m.id, m, name) },
  };
}

const npcItem = (s, n) => ({
  k: 'n', id: n.id,
  body: { n, d: s.display, priv: app.privateHidden, pend: pendingVital(KIND_NPC, n.id, 'hp'), open: opens(KIND_NPC) ? { kind: KIND_NPC, ref: n.id } : null },
  ctl: { hp: hpCtl(KIND_NPC, n.id, n.name, s.display), roll: rollCtl(KIND_NPC, n.id, n, n.name) },
});

export function renderField(s) {
  const items = [...s.markers.map(m => markerItem(s, m)), ...s.npcs.map(n => npcItem(s, n))];
  setCount('count-field', items.length);
  $('empty-field').hidden = items.length > 0;
  $('note-field').hidden = s.access !== 'coGm';
  reconcile($('list-field'), items, vm => vm.k + ':' + vm.id,
    (node, vm) => patchRow(node, vm, vm.k === 'm' ? markerBody : npcBody, rowControls));
}

export function renderRolls(s) {
  const privateHidden = app.privateHidden;
  $('sec-rolls').hidden = privateHidden;
  const rolls = privateHidden ? [] : s.rolls;
  setCount('count-rolls', rolls.length);
  $('empty-rolls').hidden = privateHidden || rolls.length > 0;
  reconcile($('list-rolls'), rolls, r => r.at + '|' + r.roller + '|' + r.raw + '|' + r.total, rebuild(buildRoll), flash);
}

export function renderNotes() {
  const privateHidden = app.privateHidden;
  $('sec-notes').hidden = privateHidden;
  const body = $('notes-text');
  // Le texte reste en mémoire sans accès : le plugin ne renvoie les notes que lorsqu'elles changent.
  const value = privateHidden || app.current?.access === 'none' ? '' : app.notesText;
  if (body.textContent !== value) body.textContent = value;
  const empty = value.trim() === '';
  body.hidden = empty;
  $('empty-notes').hidden = privateHidden || !empty;
}
