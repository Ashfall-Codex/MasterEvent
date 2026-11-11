import { OP_ANNOUNCE, OP_NEXT_ROUND, OP_TURN_END, OP_TURN_START, PRIMARY_INERT_MS } from './constants.js';
import { command, isBusy, isLocked } from './commands.js';
import { $, ARIA_LABEL, icon, setAttr, setBusy, setText } from './dom.js';
import { fmt, t } from './i18n.js';
import { renderSoon } from './render.js';
import { can } from './state.js';

// ── L'action pleine de la barre ──

const PRIMARY = 'primary';
const ANNOUNCE_BUTTON = 'announce';

let primaryCmd = null;
let primaryKey = null;
let primaryChangedAt = 0;
let primaryArmed = null;
// Le bouton plein n'obéit au clavier que si l'on y est arrivé par Tab.
let primaryKeyFocus = false;
let tabAt = 0;

function endTurnTarget(entry) {
  if (!entry) return null;
  if (entry.groupId) return { group: entry.groupId };
  return entry.ref ? { kind: entry.kind, ref: entry.ref } : null;
}

// L'action pleine de la barre, telle que l'état la décrit : op, champs, et une clé qui change avec
// l'op ou la cible (Lancer, puis Finir le tour de X, puis Manche suivante).
function primaryCommand(s) {
  const p = s.primary;
  if (!p) return null;
  if (p.kind === 'start') {
    return can(OP_TURN_START)
      ? { op: OP_TURN_START, fields: {}, key: 'start', ready: true, text: t('Web.PrimaryStart'), tip: t('Turns.StartTooltip') }
      : null;
  }
  const turn = s.turn;
  if (!turn) return null;
  const round = { encounter: turn.encounter, round: turn.round };
  if (p.kind === 'nextRound') {
    return can(OP_NEXT_ROUND)
      ? { op: OP_NEXT_ROUND, fields: round, key: 'next:' + turn.encounter + ':' + turn.round, ready: true,
        text: t('Web.PrimaryNextRound'), tip: t('Turns.NextRoundTooltip') }
      : null;
  }
  if (!can(OP_TURN_END)) return null;
  const target = endTurnTarget(turn.entries[turn.activeIndex]);
  const name = p.name || t('Web.TurnGroupUnnamed');
  return {
    op: OP_TURN_END,
    fields: { ...round, ...target },
    key: 'end:' + turn.encounter + ':' + turn.round + ':' + (target ? target.group ?? target.kind + ':' + target.ref : '?'),
    ready: !!target,
    text: fmt('Web.PrimaryEndTurnFmt', name),
    tip: t('Scene.EndTurnTooltip'),
  };
}

export function renderActions(s) {
  const cmd = primaryCommand(s);
  const key = cmd?.key ?? null;
  if (key !== primaryKey) {
    primaryKey = key;
    primaryChangedAt = Date.now();
    setTimeout(renderSoon, PRIMARY_INERT_MS + 10);
  }
  primaryCmd = cmd;

  const button = $(PRIMARY);
  button.hidden = !cmd;
  if (cmd) {
    setText(button, cmd.text);
    button.title = cmd.tip;
    setBusy(button, isBusy(PRIMARY), !cmd.ready);
    button.classList.toggle('is-settling', Date.now() - primaryChangedAt < PRIMARY_INERT_MS || isLocked(PRIMARY));
  }

  const announce = $(ANNOUNCE_BUTTON);
  announce.hidden = !can(OP_ANNOUNCE);
  if (!announce.firstChild) announce.append(icon('megaphone'));
  announce.title = t('Gm.AnnounceTooltip');
  setAttr(announce, ARIA_LABEL, t('Gm.AnnounceButton'));
}

export function onPrimary(button, event) {
  const cmd = primaryCmd;
  const armed = primaryArmed;
  primaryArmed = null;
  if (!cmd || !cmd.ready) return;
  // Op et cible relevées à l'appui : si l'état a changé avant le relâchement, le clic visait autre chose.
  if (event.detail >= 1 && armed !== cmd.key) return;
  if (event.detail === 0 && !primaryKeyFocus) return;
  if (Date.now() - primaryChangedAt < PRIMARY_INERT_MS || isLocked(PRIMARY)) return;
  command(cmd.op, cmd.fields, { label: cmd.text, lock: PRIMARY, cooldown: PRIMARY_INERT_MS });
}

export function bindPrimary() {
  $(PRIMARY).addEventListener('pointerdown', event => {
    if (event.button === 0) primaryArmed = primaryCmd?.key ?? null;
  });
  document.addEventListener('keydown', event => {
    if (event.key === 'Tab') tabAt = Date.now();
  }, true);
  $(PRIMARY).addEventListener('focus', () => {
    primaryKeyFocus = Date.now() - tabAt < 1000;
  });
  $(PRIMARY).addEventListener('blur', () => {
    primaryKeyFocus = false;
  });
}
