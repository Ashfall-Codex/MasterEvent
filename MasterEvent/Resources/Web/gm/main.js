import { bindReopen, connect, readCredentials } from './connection.js';
import { bindDetail, closeDetail } from './detail.js';
import { $ } from './dom.js';
import { bindGestures } from './gestures.js';
import { bindMenu, closeMenu, menuOpen } from './menu.js';
import { bindPrimary } from './primary.js';
import { applyTexts, bindPrivacy, bindTabs, renderPrivacy, renderScreen } from './render.js';
import { bindSheets } from './sheets.js';
import { app } from './state.js';

function bindEscape() {
  document.addEventListener('keydown', event => {
    if (event.key !== 'Escape' || document.querySelector('dialog[open]')) return;
    if (menuOpen()) {
      closeMenu(true);
      event.preventDefault();
    } else if (app.detail && $('detail').contains(document.activeElement)) {
      closeDetail();
      event.preventDefault();
    }
  });
}

readCredentials();
bindReopen();
bindPrivacy();
bindTabs();
bindGestures();
bindPrimary();
bindMenu();
bindEscape();
bindDetail();
bindSheets();

applyTexts();
renderPrivacy();
renderScreen();
connect();
