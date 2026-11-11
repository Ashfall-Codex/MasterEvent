import { ANNOUNCE_LINES, ANNOUNCE_MAX, CONTROL_CHARS, KEY_THRESHOLD, OP_ANNOUNCE, OP_ROLL_REQUEST, VALUE_MAX } from './constants.js';
import { command, describe, isBusy, isLocked } from './commands.js';
import { $, ARIA_INVALID, isDisabled, setBusy, setText } from './dom.js';
import { fmt, t } from './i18n.js';
import { closeMenu } from './menu.js';
import { statValue } from './parts.js';
import { app, can } from './state.js';
import { load, save } from './util.js';

// ── Feuilles : annonce et demande de jet ──

const CLICK = 'click';
const ASK_LOCK = 'ask';
const announceDialog = () => $('dlg-announce');
const announceText = () => $('announce-text');
const announceSend = () => $('announce-send');
const askDialog = () => $('dlg-ask');
const askStatSelect = () => $('ask-stat');
const askThresholdInput = () => $('ask-threshold');
const askSend = () => $('ask-send');

let announceDraft = '';
let announceError = '';

const normalizeAnnounce = text => text.replace(/\r\n?/g, '\n').replace(CONTROL_CHARS, ' ').trim();
const tooManyLines = text => text.split('\n').length > ANNOUNCE_LINES;

export function openAnnounce() {
  if (!can(OP_ANNOUNCE)) return;
  closeMenu(false);
  const text = announceText();
  text.value = announceDraft;
  announceError = '';
  updateAnnounce();
  announceDialog().showModal();
  text.focus();
}

// Motif affiché dans une feuille ouverte quand l'action n'est plus permise.
function missingRight(op) {
  const current = app.current;
  if (can(op) || app.lostAt || app.final || !current) return '';
  return current.access === 'gm' && current.caps.length === 0 ? t('Web.Err.offline') : t('Web.Err.forbidden');
}

function updateAnnounce() {
  const dialog = announceDialog();
  if (app.current && app.current.access !== 'gm') {
    if (dialog.open) dialog.close();
    return;
  }
  const area = announceText();
  const count = area.value.length;
  const counter = $('announce-count');
  setText(counter, fmt('Web.CounterFmt', count, ANNOUNCE_MAX));
  counter.classList.toggle('is-near', count >= ANNOUNCE_MAX * 0.8 && count < ANNOUNCE_MAX);
  counter.classList.toggle('is-full', count >= ANNOUNCE_MAX);
  const text = normalizeAnnounce(area.value);
  const tooMany = tooManyLines(text);
  setText($('announce-error'), tooMany ? t('Web.Err.tooManyLines') : announceError || missingRight(OP_ANNOUNCE));
  const busy = isBusy(OP_ANNOUNCE);
  setBusy(announceSend(), busy, text === '' || tooMany || !can(OP_ANNOUNCE));
}

function sendAnnounce(event) {
  if (event.detail > 1) return;
  if (isDisabled(announceSend()) || isLocked(OP_ANNOUNCE)) return;
  const text = normalizeAnnounce(announceText().value);
  if (text === '' || tooManyLines(text)) return;
  announceError = '';
  command(OP_ANNOUNCE, { text }, {
    label: t('Gm.AnnounceButton'),
    lock: OP_ANNOUNCE,
    done: (ok, error) => {
      const dialog = announceDialog();
      if (ok) {
        announceDraft = '';
        announceText().value = '';
        if (dialog.open) dialog.close();
        return true;
      }
      if (!dialog.open) return false;
      announceError = describe(OP_ANNOUNCE, error);
      updateAnnounce();
      return true;
    },
  });
}

let ask = null;
const askStat = new Map();
let askThreshold = Number.parseInt(load(KEY_THRESHOLD) ?? '', 10);

export function openAsk(button) {
  const current = app.current;
  const player = current?.party.find(p => p.hash === button.dataset.ref);
  if (!player || !can(OP_ROLL_REQUEST)) return;
  closeMenu(false);
  ask = { hash: player.hash, error: '' };
  // Comme la fenêtre du jeu : la stat choisie reste celle du joueur, le seuil vaut pour tout l'onglet.
  if (!askStat.has(player.hash)) askStat.set(player.hash, player.stats.find(st => st.id !== '')?.id ?? '');
  if (!Number.isInteger(askThreshold)) askThreshold = Math.max(1, Math.trunc((current.rules?.diceMax ?? 0) / 2));

  const select = askStatSelect();
  const options = [new Option(t('Dice.NoStat'), '')];
  for (const st of player.stats) {
    if (st.id !== '') options.push(new Option(st.name + ' (' + statValue(st.mod, current.display) + ')', st.id));
  }
  select.replaceChildren(...options);
  select.value = askStat.get(player.hash);
  if (select.value !== askStat.get(player.hash)) select.value = '';
  askThresholdInput().value = String(current.display.statTarget ? prefill(player, select.value) ?? askThreshold : askThreshold);
  setText($('ask-for'), fmt('RollRequest.For', player.name));
  const hint = current.display.statTarget ? 'RollRequest.HintTarget' : current.rules?.lowerIsBetter ? 'RollRequest.HintLower' : 'RollRequest.HintHigher';
  setText($('ask-hint'), t(hint));
  updateAsk(current);
  askDialog().showModal();
  select.focus();
}

// En mode à seuils, le seuil part de la stat et du bonus temporaire du joueur.
function prefill(player, statId) {
  const st = player.stats.find(x => x.id === statId && statId !== '');
  return st ? st.mod + player.tempMod : null;
}

function updateAsk(s) {
  const dialog = askDialog();
  if (!ask) return;
  const player = s.party.find(p => p.hash === ask.hash);
  if (!player || app.privateHidden || s.access !== 'gm') {
    if (dialog.open) dialog.close();
    return;
  }
  const error = !player.connected ? t('Web.Err.targetOffline') : ask.error || missingRight(OP_ROLL_REQUEST);
  setText($('ask-error'), error);
  setBusy(askSend(), isBusy(ASK_LOCK), !player.connected || !can(OP_ROLL_REQUEST));
}

function sendAsk(event) {
  if (event.detail > 1 || !ask) return;
  if (isDisabled(askSend()) || isLocked(ASK_LOCK)) return;
  const player = app.current?.party.find(p => p.hash === ask.hash);
  if (!player) return;
  const input = askThresholdInput();
  const threshold = Number(input.value);
  if (input.value.trim() === '' || !Number.isInteger(threshold) || Math.abs(threshold) > VALUE_MAX) {
    ask.error = t('Web.ThresholdHint');
    input.setAttribute(ARIA_INVALID, 'true');
    updateAsk(app.current);
    input.focus();
    return;
  }
  input.removeAttribute(ARIA_INVALID);
  askThreshold = threshold;
  save(KEY_THRESHOLD, String(threshold));
  const stat = askStatSelect().value || null;
  const hash = player.hash;
  ask.error = '';
  command(OP_ROLL_REQUEST, { player: hash, stat, threshold }, {
    label: fmt('Web.AskRollForFmt', player.name),
    lock: ASK_LOCK,
    done: (ok, error) => {
      const dialog = askDialog();
      const here = dialog.open && ask?.hash === hash;
      if (ok) {
        if (here) dialog.close();
        return true;
      }
      if (!here) return false;
      ask.error = describe(OP_ROLL_REQUEST, error);
      updateAsk(app.current);
      return true;
    },
  });
}

export function renderSheets(s) {
  if (announceDialog().open) updateAnnounce();
  if (askDialog().open) updateAsk(s);
}

export function closeSheets() {
  for (const dialog of [announceDialog(), askDialog()]) {
    if (dialog.open) dialog.close();
  }
}

export function bindSheets() {
  announceText().addEventListener('input', () => {
    announceDraft = announceText().value;
    announceError = '';
    updateAnnounce();
  });
  announceSend().addEventListener(CLICK, sendAnnounce);
  $('announce-cancel').addEventListener(CLICK, () => announceDialog().close());

  askStatSelect().addEventListener('change', () => {
    if (!ask) return;
    const value = askStatSelect().value;
    askStat.set(ask.hash, value);
    const player = app.current?.party.find(p => p.hash === ask.hash);
    const seeded = player && app.current.display.statTarget ? prefill(player, value) : null;
    if (seeded != null) askThresholdInput().value = String(seeded);
  });
  askThresholdInput().addEventListener('input', () => {
    if (!ask) return;
    ask.error = '';
    askThresholdInput().removeAttribute(ARIA_INVALID);
    updateAsk(app.current);
  });
  askThresholdInput().addEventListener('keydown', event => {
    if (event.key === 'Enter') {
      event.preventDefault();
      sendAsk({ detail: 0 });
    }
  });
  askSend().addEventListener(CLICK, sendAsk);
  $('ask-cancel').addEventListener(CLICK, () => askDialog().close());
  askDialog().addEventListener('close', () => {
    ask = null;
    setBusy(askSend(), false, false);
  });
  announceDialog().addEventListener('close', () => setBusy(announceSend(), false, false));
}
