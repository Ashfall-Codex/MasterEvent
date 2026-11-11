import {
  ACTION_DENY_CANCEL, ACTION_DENY_CONFIRM, ACTION_DETAIL, ACTION_DETAIL_CLOSE, ACTION_ROLL_STAT, CONTROL_INERT_MS,
  OP_ADMIT, OP_ANNOUNCE, OP_DENY, OP_ROLL, OP_SET_ACTED, REPEATABLE,
} from './constants.js';
import { command, isLocked } from './commands.js';
import { actedLock, rollLock, rollStats } from './controls.js';
import { closeDetail, openDetail } from './detail.js';
import { isDisabled } from './dom.js';
import { fmt } from './i18n.js';
import { closeMenu, menuAnchor, openMenu } from './menu.js';
import { onAdmit, onDeny, onDenyCancel, onDenyConfirm } from './pending.js';
import { onPrimary } from './primary.js';
import { movedAt } from './reconcile.js';
import { openAnnounce, openAsk } from './sheets.js';
import { app, entityOf, targetName } from './state.js';
import { queueVital } from './vitals.js';

// ── Gestes ──

function onActed(button) {
  const turn = app.current?.turn;
  if (!turn) return;
  const { group, kind, ref, name } = button.dataset;
  const target = group ? { group } : { kind, ref };
  const lock = actedLock(group, kind, ref);
  if (isLocked(lock)) return;
  // La valeur montrée, et non celle de l'état : on coche ce qu'on voit.
  const value = button.getAttribute('aria-checked') !== 'true';
  command(OP_SET_ACTED, { encounter: turn.encounter, round: turn.round, ...target, value },
    { label: fmt('Web.ActedFor', name), lock, value });
}

function onStep(button, event) {
  const { kind, ref, field } = button.dataset;
  const step = (event.shiftKey ? 5 : 1) * (button.dataset.dir === '-1' ? -1 : 1);
  queueVital(kind, ref, field, { delta: step });
}

function sendRoll(kind, ref, stat) {
  const lock = rollLock(kind, ref);
  if (isLocked(lock)) return;
  command(OP_ROLL, { kind, ref, stat: stat || null }, { label: fmt('Web.RollForFmt', targetName(app.current, kind, ref)), lock });
}

function onRoll(button) {
  const { kind, ref } = button.dataset;
  if (menuAnchor() === button) {
    closeMenu(true);
    return;
  }
  const entity = entityOf(app.current, kind, ref);
  if (!entity) return;
  if (rollStats(entity).length > 0) openMenu(button, kind, ref);
  else sendRoll(kind, ref, null);
}

function onRollStat(button) {
  const { kind, ref, stat } = button.dataset;
  closeMenu(button.closest('#menu') !== null);
  sendRoll(kind, ref, stat);
}

const ACTIONS = {
  primary: onPrimary,
  [OP_ANNOUNCE]: openAnnounce,
  acted: onActed,
  step: onStep,
  roll: onRoll,
  [ACTION_ROLL_STAT]: onRollStat,
  [ACTION_DETAIL]: openDetail,
  [ACTION_DETAIL_CLOSE]: closeDetail,
  ask: openAsk,
  [OP_ADMIT]: onAdmit,
  [OP_DENY]: onDeny,
  [ACTION_DENY_CONFIRM]: onDenyConfirm,
  [ACTION_DENY_CANCEL]: onDenyCancel,
};

function onClick(event) {
  const button = event.target instanceof Element ? event.target.closest('[data-action]') : null;
  if (!button) return;
  const action = button.dataset.action;
  const handler = ACTIONS[action];
  // Un clic de souris ne garde pas le focus : Espace ou Entrée, tapés ensuite pour le jeu, referaient le geste.
  const pointer = event.detail >= 1;
  if (pointer) button.blur();
  if (!handler || isDisabled(button)) return;
  // Un double clic ne fait jamais deux gestes : le second tomberait sur l'état suivant.
  if (event.detail > 1 && !REPEATABLE.has(action)) return;
  const row = button.closest('.row, .turn-group, .pending-group');
  if (row && action !== ACTION_DETAIL && Date.now() - (movedAt.get(row) ?? 0) < CONTROL_INERT_MS) return;
  handler(button, event);
  if (pointer && document.activeElement === button) button.blur();
}

export function bindGestures() {
  document.addEventListener('click', onClick);
}
