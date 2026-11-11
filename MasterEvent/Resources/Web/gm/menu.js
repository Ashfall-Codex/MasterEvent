import { ACTION_ROLL_STAT } from './constants.js';
import { rollCtl, rollStats } from './controls.js';
import { $, ARIA_LABEL, control, el, setData } from './dom.js';
import { fmt, t } from './i18n.js';
import { statValue } from './parts.js';
import { app, entityOf, targetName } from './state.js';
import { clamp } from './util.js';

// ── Menu des stats d'un jet ──

const MENU = 'menu';
const MENU_ITEM = '.menu-item';
const ARIA_EXPANDED = 'aria-expanded';

let menuFor = null;

export const menuOpen = () => menuFor !== null;
export const menuAnchor = () => menuFor?.anchor ?? null;

export function openMenu(anchor, kind, ref) {
  const current = app.current;
  const entity = entityOf(current, kind, ref);
  if (!entity) return;
  const menu = $(MENU);
  const choices = [{ id: '', text: t('Dice.NoStat') },
    ...rollStats(entity).map(st => ({ id: st.id, text: st.name + ' ' + statValue(st.mod, current.display) }))];
  menu.replaceChildren(el('div', 'menu-title', t('Dice.SelectStat')), ...choices.map(choice => {
    const item = control('menu-item', ACTION_ROLL_STAT, choice.text);
    item.setAttribute('role', 'menuitem');
    setData(item, { kind, ref, stat: choice.id });
    return item;
  }));
  menu.setAttribute(ARIA_LABEL, fmt('Web.RollForFmt', targetName(current, kind, ref)));
  menu.hidden = false;
  menuFor = { kind, ref, anchor };
  anchor.setAttribute(ARIA_EXPANDED, 'true');
  placeMenu();
  menu.querySelector(MENU_ITEM)?.focus({ preventScroll: true });
}

function placeMenu() {
  if (!menuFor) return;
  const menu = $(MENU);
  const rect = menuFor.anchor.getBoundingClientRect();
  const width = menu.offsetWidth;
  const height = menu.offsetHeight;
  const left = clamp(rect.right - width, 8, window.innerWidth - width - 8);
  const below = rect.bottom + 4;
  const top = below + height > window.innerHeight - 8 ? Math.max(8, rect.top - 4 - height) : below;
  menu.style.left = left + 'px';
  menu.style.top = top + 'px';
}

export function closeMenu(restoreFocus) {
  if (!menuFor) return;
  const anchor = menuFor.anchor;
  menuFor = null;
  $(MENU).hidden = true;
  $(MENU).replaceChildren();
  anchor.removeAttribute(ARIA_EXPANDED);
  if (restoreFocus && anchor.isConnected) anchor.focus({ preventScroll: true });
}

export function renderMenu(s) {
  if (!menuFor) return;
  const roll = rollCtl(menuFor.kind, menuFor.ref, entityOf(s, menuFor.kind, menuFor.ref), '');
  if (!roll || roll.busy || roll.unnamed || !menuFor.anchor.isConnected) closeMenu(false);
}

// Fermeture du menu : clic à côté, Échap, défilement ou redimensionnement.
export function bindMenu() {
  document.addEventListener('pointerdown', event => {
    if (!menuFor || !(event.target instanceof Node)) return;
    if ($(MENU).contains(event.target) || menuFor.anchor.contains(event.target)) return;
    closeMenu(false);
  }, true);
  window.addEventListener('resize', () => closeMenu(false));
  document.addEventListener('scroll', event => {
    if (menuFor && !(event.target instanceof Node && $(MENU).contains(event.target))) closeMenu(false);
  }, true);

  $(MENU).addEventListener('keydown', event => {
    const items = Array.from($(MENU).querySelectorAll(MENU_ITEM));
    const index = items.indexOf(document.activeElement);
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      const step = event.key === 'ArrowDown' ? 1 : -1;
      items[(index + step + items.length) % items.length]?.focus();
      event.preventDefault();
    } else if (event.key === 'Tab') {
      closeMenu(false);
    }
  });
}
