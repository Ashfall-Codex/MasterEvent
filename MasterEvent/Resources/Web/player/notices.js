import { LOCK, OP, PAGE_NOTICES_MAX, ROOM_VIEWS, SESSION_VIEWS, VIEW } from './constants.js';
import { can, locks } from './commands.js';
import { $, ARIA_LABEL, control, el, icon, setAttr, setBusy, setText, setTitle } from './dom.js';
import { syncControls } from './rows.js';
import { fmt, t } from './texts.js';

// Avis de la page : deux au plus, dans l'ordre de la vue en jeu.
let hiddenAnnouncement = null;
const noticeParts = new WeakMap();

function noticeSpec(k, tone, text, details, action) {
  return {
    k,
    make: () => {
      const node = el('div', 'p-notice p-notice-' + tone);
      const body = el('div', 'p-notice-body');
      const main = el('p', 'p-notice-text');
      const more = el('p', 'p-notice-details');
      body.append(main, more);
      const end = el('div', 'p-notice-end');
      node.append(body, end);
      noticeParts.set(node, { main, more, end });
      return node;
    },
    update: node => {
      const p = noticeParts.get(node);
      setText(p.main, text);
      setText(p.more, details);
      p.more.hidden = details === '';
      syncControls(p.end, action ? [action] : []);
    },
  };
}

function requestNotice(s) {
  const r = s.request;
  const text = r.statId == null && !r.missing
    ? fmt('PlayerView.RollRequestNoStatFmt', r.threshold)
    : fmt('RollRequest.Banner', r.statName, r.threshold);
  const details = [];
  if (r.missing) details.push(fmt('PlayerView.RollMissingStatFmt', r.statName));
  if (r.statId != null && s.display.statTarget) details.push(t('PlayerView.RollHintTarget'));
  if (s.lowerIsBetter) details.push(t('RollRequest.HintLower'));
  return noticeSpec('request', 'accent', text, details.join('\n'), {
    k: 'dismiss',
    make: () => control('btn btn-small', 'dismiss'),
    update: button => {
      setText(button, t('RollRequest.Dismiss'));
      setBusy(button, locks.has(LOCK.dismiss), !can(OP.selfDismiss));
    },
  });
}

function announcementNotice(text) {
  return noticeSpec('announcement', 'accent', fmt('PlayerView.AnnouncementFmt', text), '', {
    k: 'hide',
    make: () => {
      const button = control('icon-btn', 'hide-announcement');
      button.append(icon('close'));
      return button;
    },
    update: button => {
      setTitle(button, t('PlayerView.HideAnnouncement'));
      setAttr(button, ARIA_LABEL, t('PlayerView.HideAnnouncement'));
    },
  });
}

export function renderNotices(s) {
  // Une autre annonce, ou plus d'annonce : celle qu'on avait masquée ne compte plus.
  if (hiddenAnnouncement !== null && s.announcement !== hiddenAnnouncement) hiddenAnnouncement = null;
  const specs = [];
  if (ROOM_VIEWS.has(s.view) && s.request) specs.push(requestNotice(s));
  if (s.view === VIEW.reconnecting) specs.push(noticeSpec('reconnecting', 'warning', t('PlayerView.ReconnectingNotice'), '', null));
  if (SESSION_VIEWS.has(s.view) && s.announcement && s.announcement !== hiddenAnnouncement)
    specs.push(announcementNotice(s.announcement));
  syncControls($('p-notices'), specs.slice(0, PAGE_NOTICES_MAX));
}

export function hideAnnouncement(text) {
  hiddenAnnouncement = text;
}

export function clearNotices() {
  syncControls($('p-notices'), []);
}
