import { renderActions } from './actions.js';
import { renderBar } from './bar.js';
import { KEY_TAB, SCREENS, STATE_VIEWS, TAB, TABS } from './constants.js';
import { $, ARIA_LABEL, load, save, setText } from './dom.js';
import { forgetScrolledBlock, renderList, scrollToActiveBlock } from './list.js';
import { renderNotices } from './notices.js';
import { renderRolls } from './rolls.js';
import { renderSheet } from './sheet.js';
import { app } from './store.js';
import { t } from './texts.js';

let currentTab = TABS.includes(load(KEY_TAB)) ? load(KEY_TAB) : TAB.sheet;
let wasCombat = false;

function renderTabs(s) {
  const stateful = STATE_VIEWS.has(s.view);
  // États à message : le message puis Ma fiche, sans segments, comme la vue en jeu.
  $('tabs').hidden = stateful;
  setText($('tab-list'), t(s.turn ? 'Scene.TurnOrder' : 'Sidebar.Scene'));
  for (const button of $('tabs').querySelectorAll('.tab')) {
    const selected = button.dataset.tab === currentTab;
    button.setAttribute('aria-selected', String(selected));
    button.tabIndex = selected ? 0 : -1;
  }
  for (const section of document.querySelectorAll('.section'))
    section.classList.toggle('is-current', stateful || section.dataset.tab === currentTab);
}

export function selectTab(name, focus) {
  if (!TABS.includes(name) || name === currentTab) return;
  currentTab = name;
  save(KEY_TAB, name);
  if (app.current) renderTabs(app.current);
  const button = $('tabs').querySelector('.tab[data-tab="' + name + '"]');
  if (focus) button?.focus();
  const layout = $('layout');
  if (layout.getBoundingClientRect().top < 0) layout.scrollIntoView({ block: 'start' });
  // Après le recalage : avant, le défilement vers le bloc actif était aussitôt défait.
  if (app.current) scrollToActiveBlock();
}

export function installTabs() {
  const tabs = $('tabs');
  tabs.addEventListener('click', event => {
    const button = event.target instanceof Element ? event.target.closest('.tab') : null;
    if (button) selectTab(button.dataset.tab, false);
  });
  tabs.addEventListener('keydown', event => {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
    const index = TABS.indexOf(currentTab);
    const step = event.key === 'ArrowRight' ? 1 : -1;
    selectTab(TABS[(index + step + TABS.length) % TABS.length], true);
    event.preventDefault();
  });
}

export function render() {
  const current = app.current;
  if (!current) return;
  // Ordre du tour au début de chaque combat, Ma fiche quand il se termine. Une coupure masque le
  // combat sans le finir : c'est sa présence qui compte, pas son affichage, comme en jeu.
  const combat = current.combatPresent;
  if (combat !== wasCombat) {
    currentTab = combat ? TAB.list : TAB.sheet;
    save(KEY_TAB, currentTab);
    forgetScrolledBlock();
  }
  wasCombat = combat;

  renderBar(current);
  renderActions(current);
  renderNotices(current);
  renderList(current);
  renderSheet(current);
  renderRolls(current);
  renderTabs(current);
  scrollToActiveBlock();
}

export function showScreen(kind) {
  app.screen = kind;
  renderScreen();
}

export function renderScreen() {
  const spec = app.screen ? SCREENS[app.screen] : null;
  $('screen').hidden = !spec;
  $('app').hidden = !!spec || !app.current;
  renderBanner();
  if (!spec) return;

  $('screen').classList.toggle('is-busy', !!spec.busy);
  $('screen-title').textContent = t(spec.title);
  const text = $('screen-text');
  text.hidden = !spec.text;
  text.textContent = spec.text ? t(spec.text) : '';
}

export function renderBanner() {
  const banner = $('banner');
  const show = app.lostAt > 0 && !app.screen;
  banner.hidden = !show;
  if (show) banner.textContent = t('PlayerWeb.Reconnecting');
}

export function applyTexts() {
  document.title = t('PlayerWeb.Title');
  for (const node of document.querySelectorAll('[data-t]')) node.textContent = t(node.dataset.t);
  for (const node of document.querySelectorAll('[data-t-label]')) node.setAttribute(ARIA_LABEL, t(node.dataset.tLabel));
  const search = $('stat-search');
  search.placeholder = t('Models.StatsFilter');
  search.setAttribute(ARIA_LABEL, t('Models.StatsFilter'));
  const clear = $('stat-search-clear');
  clear.title = t('Search.Clear');
  clear.setAttribute(ARIA_LABEL, t('Search.Clear'));
}
