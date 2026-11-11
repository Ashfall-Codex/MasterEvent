import { LINK, SESSION_VIEWS, VIEW } from './constants.js';
import { $, el, setTitle } from './dom.js';
import { changed } from './rows.js';
import { fmt, t } from './texts.js';

const CHIP_MUTED = 'chip-muted';
const CHIP_DANGER = 'chip-danger';
const CHIP_PENDING = 'chip-pending';
const CHIP_WARNING = 'chip-warning';

function chip(cls, title, text) {
  const node = el('span', 'chip' + (cls ? ' ' + cls : ''));
  setTitle(node, title);
  if (text != null) node.textContent = text;
  return node;
}

const isLobby = s => !!(s.link.lobbyCode || s.detail.lobby);
export const lobbyCode = s => s.detail.lobby ?? s.link.lobbyCode ?? '';

export function offlineHint(s) {
  return t(s.detail.consent ? 'PlayerView.OfflineHint' : 'PlayerView.OfflineConsentHint');
}

export function versionHint(s) {
  return fmt('PlayerView.VersionHintFmt', s.detail.minVersion ?? '');
}

export function rejectionHint(s) {
  switch (s.detail.rejection) {
    case 'denied':
      return isLobby(s) ? fmt('PlayerView.RejectedDeniedFmt', lobbyCode(s)) : t('PlayerView.RejectedInvalid');
    case 'roomLimit':
      return t('PlayerView.RejectedRoomLimit');
    case 'rateLimited':
      return t('PlayerView.RejectedRateLimited');
    default:
      return t('PlayerView.RejectedInvalid');
  }
}

function linkText(link) {
  switch (link.state) {
    case LINK.inSession:
      return link.onlinePlayers === 1 ? t('Link.InSessionOne') : fmt('Link.InSession', link.onlinePlayers);
    case LINK.serverOnly:
      return t('Gm.LinkServer');
    case LINK.reconnecting:
      return t('Gm.LinkReconnecting');
    default:
      return t('Gm.Local');
  }
}

// Le MJ et son lien, comme la fenêtre Séance du jeu, puis le modèle actif.
function roomTooltip(s) {
  const lines = [];
  if (s.gm) {
    lines.push((s.gm.name ? fmt('PlayerView.GmFmt', s.gm.name) : t('Group.Gm'))
      + ' · ' + t(s.gm.connected ? 'Group.Connected' : 'Group.Disconnected'));
  }
  if (s.template) lines.push(fmt('PlayerWeb.TemplateFmt', s.template));
  return lines.join('\n');
}

function roomChip(s) {
  const tip = roomTooltip(s);
  const node = chip('chip-session', null, null);
  node.append(el('span', 'dot link-' + s.link.state));
  if (s.link.lobbyCode) {
    const code = s.link.lobbyCode;
    const full = fmt('Scene.LobbyFmt', code) + ' · ' + fmt('Scene.OnlineFmt', s.link.partyOnline, s.link.partyShown);
    // Étroite, la puce se raccourcit et garde le texte complet en infobulle, comme en jeu.
    node.append(el('span', 'p-chip-full', full),
      el('span', 'p-chip-short', fmt('PlayerView.LobbyShortFmt', code, s.link.partyOnline, s.link.partyShown)));
    setTitle(node, tip ? full + '\n' + tip : full);
  } else {
    node.append(el('span', null, linkText(s.link)));
    setTitle(node, tip);
  }
  return node;
}

// La puce de séance de la vue en jeu (PlayerWindow.SessionChip), état par état.
function sessionChip(s) {
  const code = lobbyCode(s);
  switch (s.view) {
    case VIEW.instance:
      return chip(CHIP_MUTED, t('PlayerView.InstanceHint'), t('PlayerView.InstanceChip'));
    case VIEW.versionRejected:
      return chip(CHIP_DANGER, versionHint(s), t('PlayerView.VersionChip'));
    case VIEW.joinRejected:
      return chip(CHIP_DANGER, rejectionHint(s),
        isLobby(s) ? fmt('PlayerView.LobbyRejectedFmt', code) : t('PlayerView.RejectedTitle'));
    case VIEW.awaitingApproval:
      return chip(CHIP_PENDING, fmt('PlayerView.PendingHintFmt', code), fmt('PlayerView.LobbyPendingFmt', code));
    case VIEW.offline:
      return chip(CHIP_DANGER, offlineHint(s), t('Gm.Local'));
    case VIEW.reconnecting:
      return chip(CHIP_WARNING, t('PlayerView.ReconnectingNotice'), t('Gm.LinkReconnecting'));
    case VIEW.outOfSession:
      return chip(CHIP_MUTED, null, t('PlayerView.LinkServer'));
    case VIEW.joining:
      return chip(CHIP_MUTED, null, t('PlayerView.LinkJoining'));
    case VIEW.handover:
      return chip(CHIP_PENDING, t('PlayerView.HandoverTooltip'), t('PlayerView.Handover'));
    case VIEW.gmAway:
      return chip(CHIP_WARNING, t('PlayerView.GmAwayTooltip'),
        isLobby(s) ? fmt('PlayerView.LobbyGmAwayFmt', code) : t('PlayerView.GmAway'));
    default:
      return roomChip(s);
  }
}

function turnChip(s) {
  if (s.turn.activeIndex < 0) return chip(CHIP_MUTED, null, t('PlayerView.AllActed'));
  if (s.me?.myTurn) return chip(CHIP_PENDING, null, t('PlayerView.YourTurn'));
  return chip(null, null, fmt('PlayerView.TurnOfFmt', s.turn.turnOf ?? ''));
}

function ambianceText(s) {
  if (!s.ambiance) return null;
  const parts = [s.ambiance.weather, s.ambiance.time].filter(Boolean);
  return parts.length > 0 ? parts.join(' · ') : null;
}

export function renderBar(s) {
  const vm = {
    view: s.view,
    link: s.link,
    detail: s.detail,
    gm: s.gm,
    template: s.template,
    turn: s.turn && { round: s.turn.round, acted: s.turn.acted, total: s.turn.total, activeIndex: s.turn.activeIndex, turnOf: s.turn.turnOf },
    myTurn: !!s.me?.myTurn,
    // Comme la barre du jeu : pas de puce d'ambiance pendant un combat.
    ambiance: !s.turn && SESSION_VIEWS.has(s.view) ? ambianceText(s) : null,
  };
  if (!changed('bar', vm)) return;

  const items = [];
  if (s.turn) {
    items.push(el('span', 'bar-round', fmt('Turns.Round', s.turn.round)));
    items.push(el('span', 'bar-progress', fmt('Turns.Progress', s.turn.acted, s.turn.total)));
  }
  items.push(sessionChip(s));
  if (vm.ambiance) items.push(chip('chip-ambiance', t('PlayerView.AmbianceTooltip'), vm.ambiance));
  if (s.turn) items.push(turnChip(s));
  $('bar-state').replaceChildren(...items);
}
