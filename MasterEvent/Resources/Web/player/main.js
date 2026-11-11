import { installBarButtons } from './actions.js';
import { connect, onHashChange, readCredentials } from './connection.js';
import { $, icon } from './dom.js';
import { installSearch, onClick } from './gestures.js';
import { applyTexts, installTabs, render, renderScreen } from './render.js';
import { setRenderer } from './store.js';

setRenderer(render);
readCredentials();

window.addEventListener('hashchange', onHashChange);
installTabs();
document.addEventListener('click', onClick);
installBarButtons();
installSearch();

$('stat-search-clear').append(icon('close'));

applyTexts();
renderScreen();
connect();
