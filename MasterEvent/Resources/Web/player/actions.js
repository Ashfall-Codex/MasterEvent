import { HOLD, LOCK, OP, PRIMARY_INERT_MS } from './constants.js';
import { can, command, isLocked, locks } from './commands.js';
import { $, setBusy, setText, setTitle } from './dom.js';
import { app, renderSoon } from './store.js';
import { fmt, t } from './texts.js';

// L'action pleine et l'action teintée de la barre, comme Bar.cs.
const PRIMARY = 'primary';
const TINTED = 'tinted';

let barActions = null;
let actionKey = null;
let actionChangedAt = 0;
let lastRollAt = 0;
const armed = { primary: null, tinted: null };
// Un bouton de la barre n'obéit au clavier que si l'on y est arrivé par Tab.
const keyFocus = { primary: false, tinted: false };
let tabAt = 0;

function endTurnAction(p) {
  const label = p.hold === HOLD.sent
    ? t('PlayerView.EndTurnSent')
    : t(p.groupTurn ? 'Tactical.EndGroupTurn' : 'Tactical.EndMyTurn');
  const tip = p.hold === HOLD.handover ? t('PlayerView.EndTurnHandover')
    : p.hold === HOLD.gmAway ? t('PlayerView.EndTurnGmAway')
      : p.hold === HOLD.none ? t('PlayerView.EndMyTurnTooltip')
        : '';
  return { op: OP.selfEndTurn, text: label, tip, disabled: p.hold !== HOLD.none || !can(OP.selfEndTurn) };
}

function resolveActions(s) {
  const p = s.primary;
  if (!p) return null;
  const end = p.canEndTurn ? endTurnAction(p) : null;
  if (p.kind === 'roll') {
    const text = p.statName != null ? fmt('PlayerView.RollStatFmt', p.statName) : t('PlayerView.RollNoStat');
    return { primary: { op: OP.selfAnswer, text, tip: text, disabled: !can(OP.selfAnswer) }, tinted: end };
  }
  return end ? { primary: end, tinted: null } : null;
}

const inert = () => Date.now() - actionChangedAt < PRIMARY_INERT_MS || isLocked(LOCK.bar);

function paintAction(button, action) {
  button.hidden = !action;
  if (!action) return;
  setText(button, action.text);
  setTitle(button, action.tip);
  setBusy(button, locks.get(LOCK.bar)?.op === action.op, action.disabled);
  button.classList.toggle('is-settling', inert());
}

export function renderActions(s) {
  const actions = resolveActions(s);
  // Même clé que la barre du jeu : le geste, la stat, la fin de tour possible, la manche et l'entrée active.
  const key = actions
    ? JSON.stringify([s.primary.kind, s.primary.statName, s.primary.canEndTurn, s.turn?.round ?? 0, s.turn?.activeIndex ?? -1])
    : null;
  if (key !== actionKey) {
    actionKey = key;
    actionChangedAt = Date.now();
    setTimeout(renderSoon, PRIMARY_INERT_MS + 10);
  }
  barActions = actions;
  paintAction($(PRIMARY), actions?.primary ?? null);
  paintAction($(TINTED), actions?.tinted ?? null);
}

export function onBarAction(button, event) {
  const which = button.id === TINTED ? TINTED : PRIMARY;
  const action = barActions?.[which] ?? null;
  const armedKey = armed[which];
  armed[which] = null;
  if (!action || action.disabled) return;
  // Geste relevé à l'appui : si l'action a changé avant le relâchement, le clic visait autre chose.
  if (event.detail >= 1 && armedKey !== actionKey) return;
  if (event.detail === 0 && !keyFocus[which]) return;
  if (inert()) return;
  actionChangedAt = lastRollAt = Date.now();
  setTimeout(renderSoon, PRIMARY_INERT_MS + 10);
  command(action.op, {}, { label: action.text, lock: LOCK.bar, cooldown: PRIMARY_INERT_MS });
}

// Comme RollOnce : un double clic, ou un clic juste après la barre, ne lance qu'un dé.
export function onRollStat(button) {
  const current = app.current;
  if (Date.now() - lastRollAt < PRIMARY_INERT_MS || isLocked(LOCK.roll) || !current?.me) return;
  const id = button.dataset.stat || '';
  const stat = id ? current.me.stats.find(x => x.id === id) : null;
  if (id && !stat) return;
  lastRollAt = Date.now();
  command(OP.selfRoll, stat ? { stat: stat.id } : {}, {
    label: stat ? fmt('PlayerView.RollStatFmt', stat.name) : t('PlayerWeb.RollNoStat'),
    lock: LOCK.roll,
    value: id,
    cooldown: PRIMARY_INERT_MS,
  });
}

export function installBarButtons() {
  for (const which of [PRIMARY, TINTED]) {
    const button = $(which);
    button.addEventListener('pointerdown', event => {
      if (event.button === 0) armed[which] = actionKey;
    });
    button.addEventListener('focus', () => {
      keyFocus[which] = Date.now() - tabAt < 1000;
    });
    button.addEventListener('blur', () => {
      keyFocus[which] = false;
    });
  }
  document.addEventListener('keydown', event => {
    if (event.key === 'Tab') tabAt = Date.now();
  }, true);
}
