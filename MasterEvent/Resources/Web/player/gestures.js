import { onBarAction, onRollStat } from './actions.js';
import { command, isLocked } from './commands.js';
import { LOCK, NARROW, OP, TAB } from './constants.js';
import { $ } from './dom.js';
import { toggleDetail } from './list.js';
import { hideAnnouncement } from './notices.js';
import { render, selectTab } from './render.js';
import { hasStatQuery, setStatQuery } from './sheet.js';
import { app } from './store.js';
import { t } from './texts.js';

function onDismiss() {
  if (isLocked(LOCK.dismiss)) return;
  command(OP.selfDismiss, {}, { label: t('RollRequest.Dismiss'), lock: LOCK.dismiss });
}

function onHideAnnouncement() {
  hideAnnouncement(app.current?.announcement ?? null);
  render();
}

function onDetail(button) {
  toggleDetail(button.dataset.ref);
  render();
}

// Sa propre ligne de l'ordre du tour mène à Ma fiche, déjà à côté en largeur normale.
function onFocusSheet() {
  if (window.matchMedia(NARROW).matches) {
    selectTab(TAB.sheet, false);
  } else {
    const sheet = $('sec-sheet');
    if (sheet.scrollIntoView) sheet.scrollIntoView({ block: 'nearest' });
  }
}

function onSearchClear() {
  const search = $('stat-search');
  setStatQuery('');
  search.value = '';
  render();
  search.focus({ preventScroll: true });
}

const ACTIONS = {
  primary: onBarAction,
  tinted: onBarAction,
  'roll-stat': onRollStat,
  dismiss: onDismiss,
  'hide-announcement': onHideAnnouncement,
  detail: onDetail,
  'focus-sheet': onFocusSheet,
  'search-clear': onSearchClear,
};

export function onClick(event) {
  const button = event.target instanceof Element ? event.target.closest('[data-action]') : null;
  if (!button) return;
  const handler = ACTIONS[button.dataset.action];
  // Un clic de souris ne garde pas le focus : Espace ou Entrée, tapés ensuite pour le jeu, referaient le geste.
  const pointer = event.detail >= 1;
  if (pointer) button.blur();
  if (!handler || button.getAttribute('aria-disabled') === 'true') return;
  // Un double clic ne fait jamais deux gestes : le second tomberait sur l'état suivant.
  if (event.detail > 1) return;
  handler(button, event);
  if (pointer && document.activeElement === button) button.blur();
}

export function installSearch() {
  const search = $('stat-search');
  search.addEventListener('input', () => {
    setStatQuery(search.value);
    render();
  });
  search.addEventListener('keydown', event => {
    if (event.key === 'Escape' && hasStatQuery()) {
      event.preventDefault();
      onSearchClear();
    }
  });
}
