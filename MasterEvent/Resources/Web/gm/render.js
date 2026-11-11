import { renderBar } from './bar.js';
import { KEY_PRIVATE, KEY_TAB, SCREENS, TAB_PLAYERS, TABS } from './constants.js';
import { renderDetail } from './detail.js';
import { $, ARIA_LABEL, setCount, setText } from './dom.js';
import { t } from './i18n.js';
import { renderField, renderNotes, renderPlayers, renderRolls, renderTurn } from './lists.js';
import { closeMenu, renderMenu } from './menu.js';
import { renderPending, visiblePending } from './pending.js';
import { renderActions } from './primary.js';
import { forceRebuild } from './reconcile.js';
import { renderSheets } from './sheets.js';
import { app } from './state.js';
import { load, save } from './util.js';

// ── Rendu ──

const TABS_ID = 'tabs';
const TAB = '.tab';
const SCREEN = 'screen';

let renderQueued = false;

export function renderSoon() {
  if (renderQueued) return;
  renderQueued = true;
  queueMicrotask(() => {
    renderQueued = false;
    render();
  });
}

let currentTab = TABS.includes(load(KEY_TAB)) ? load(KEY_TAB) : TAB_PLAYERS;
let wasCombat = false;

function visibleTabs(s) {
  const privateHidden = app.privateHidden;
  return {
    turn: !!s?.turn,
    players: s?.access !== 'coGm',
    field: true,
    rolls: !privateHidden,
    pending: !privateHidden && !!s && visiblePending(s).length > 0,
    notes: !privateHidden,
  };
}

function renderTabs(s) {
  const visible = visibleTabs(s);
  if (!visible[currentTab]) currentTab = visible.turn ? 'turn' : visible.players ? TAB_PLAYERS : 'field';

  for (const button of $(TABS_ID).querySelectorAll(TAB)) {
    const name = button.dataset.tab;
    const selected = name === currentTab;
    button.hidden = !visible[name];
    button.setAttribute('aria-selected', String(selected));
    button.tabIndex = selected ? 0 : -1;
  }
  setCount('tab-count-pending', visible.pending ? visiblePending(s).length : 0);

  for (const section of document.querySelectorAll('.section'))
    section.classList.toggle('is-current', section.dataset.tab === currentTab);

  $('layout').classList.toggle('no-side', !visible.rolls && !visible.pending && !visible.notes && !app.detail);
}

function selectTab(name, focus) {
  if (!TABS.includes(name) || name === currentTab) return;
  currentTab = name;
  save(KEY_TAB, name);
  renderTabs(app.current);
  const button = $(TABS_ID).querySelector('.tab[data-tab="' + name + '"]');
  if (focus) button?.focus();
  const layout = $('layout');
  if (layout.getBoundingClientRect().top < 0) layout.scrollIntoView({ block: 'start' });
}

export function render() {
  const current = app.current;
  if (!current) return;
  const combat = !!current.turn?.active;
  // Comme l'onglet Scène : le combat qui commence prend la place de la liste choisie.
  if (combat && !wasCombat) currentTab = 'turn';
  wasCombat = combat;

  renderBar(current);
  renderActions(current);
  renderTurn(current);
  renderPlayers(current);
  renderField(current);
  renderPending(current);
  renderRolls(current);
  renderNotes();
  renderDetail(current);
  renderTabs(current);
  renderPrivacy();
  renderMenu(current);
  renderSheets(current);
}

// ── Écrans d'état et textes fixes ──

let screen = 'connecting';

export const hasScreen = () => !!screen;

export function showScreen(kind) {
  screen = kind;
  renderScreen();
}

export function renderScreen() {
  const spec = screen ? SCREENS[screen] : null;
  $(SCREEN).hidden = !spec;
  $('app').hidden = !!spec || !app.current;
  renderBanner();
  if (!spec) return;

  $(SCREEN).classList.toggle('is-busy', !!spec.busy);
  $('screen-title').textContent = t(spec.title);
  const text = $('screen-text');
  text.hidden = !spec.text;
  text.textContent = spec.text ? t(spec.text) : '';
}

export function renderBanner() {
  const banner = $('banner');
  const show = app.lostAt > 0 && !screen;
  banner.hidden = !show;
  if (show) banner.textContent = t('Web.Reconnecting');
}

export function renderPrivacy() {
  const button = $('privacy');
  setText(button, t(app.privateHidden ? 'Web.ShowPrivate' : 'Web.HidePrivate'));
  button.title = t('Web.HidePrivateHint');
  button.setAttribute('aria-pressed', String(app.privateHidden));
}

export function applyTexts() {
  document.title = t('Web.Title');
  for (const node of document.querySelectorAll('[data-t]')) node.textContent = t(node.dataset.t);
  for (const node of document.querySelectorAll('[data-t-label]')) node.setAttribute(ARIA_LABEL, t(node.dataset.tLabel));
}

export function bindPrivacy() {
  $('privacy').addEventListener('click', () => {
    app.privateHidden = !app.privateHidden;
    app.privateChoice = app.privateHidden ? '1' : '0';
    save(KEY_PRIVATE, app.privateChoice);
    closeMenu(false);
    forceRebuild();
    render();
  });
}

export function bindTabs() {
  $(TABS_ID).addEventListener('click', event => {
    const button = event.target instanceof Element ? event.target.closest(TAB) : null;
    if (button) selectTab(button.dataset.tab, false);
  });

  $(TABS_ID).addEventListener('keydown', event => {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
    const visible = Array.from($(TABS_ID).querySelectorAll(TAB)).filter(b => !b.hidden).map(b => b.dataset.tab);
    const index = visible.indexOf(currentTab);
    if (index < 0) return;
    const step = event.key === 'ArrowRight' ? 1 : -1;
    selectTab(visible[(index + step + visible.length) % visible.length], true);
    event.preventDefault();
  });
}
