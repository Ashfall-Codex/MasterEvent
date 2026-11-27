import { notify } from './commands.js';
import { ACTION_COPY_INVITE, ACTION_SHOW_PENDING } from './constants.js';
import { $, chip, control, el, setAttr, setText } from './dom.js';
import { fmt, t } from './i18n.js';
import { visiblePending } from './pending.js';
import { changed, syncControls } from './reconcile.js';
import { app } from './state.js';

const CHIP_QUIET = 'chip-quiet';
const BAR_STATE = 'bar-state';

function linkText(link) {
  switch (link.state) {
    case 'inSession':
      return link.onlinePlayers === 1 ? t('Web.LinkInSessionOne') : fmt('Web.LinkInSessionFmt', link.onlinePlayers);
    case 'serverOnly':
      return t('Web.LinkServerOnly');
    case 'reconnecting':
      return t('Web.LinkReconnecting');
    default:
      return t('Web.LinkOffline');
  }
}

// Pastille de la barre qui agit : un bouton qui garde l'allure des autres.
function chipButton(cls, action) {
  return control('chip chip-btn ' + cls, action);
}

function textSpec(k, make, text, title = null) {
  return {
    k, make,
    update: node => {
      setAttr(node, 'title', title);
      setText(node, text);
    },
  };
}

function sessionParts(link) {
  const parts = [el('span', 'dot link-' + (link.suspended ? 'suspended' : link.state))];
  if (link.lobbyCode) {
    parts.push(el('span', null, t('Web.Lobby')), el('span', 'code', link.lobbyCode),
      el('span', 'sep', '·'), el('span', null, fmt('Web.OnlineFmt', link.partyOnline, link.partyShown)));
  } else {
    parts.push(el('span', null, linkText(link)));
  }
  return parts;
}

// En lobby, la pastille copie l'invitation, comme la popup de séance du jeu.
function sessionSpec(link) {
  const copy = !!link.invite;
  return {
    k: copy ? 'session-copy' : 'session',
    make: () => (copy ? chipButton('chip-session', ACTION_COPY_INVITE) : chip('chip-session')),
    update: node => {
      setAttr(node, 'title', copy ? linkText(link) + '\n' + t('Lobby.CopyInvite') : linkText(link));
      node.replaceChildren(...sessionParts(link));
    },
  };
}

function pendingChip() {
  const node = chipButton('chip-pending', ACTION_SHOW_PENDING);
  node.setAttribute('aria-controls', 'sec-pending');
  return node;
}

// Pastilles d'état après celle du lien, dans l'ordre de la barre du jeu.
function stateSpecs(s, vm) {
  const specs = [];
  if (s.link.suspended) specs.push(textSpec('warning', () => chip('chip-warning'), t('Web.LinkSuspended'), t('Web.LinkSuspendedHint')));

  // Comme l'onglet Scène : l'ambiance reste au MJ.
  if (vm.ambiance) {
    const parts = [vm.ambiance.weather, vm.ambiance.time].filter(Boolean);
    specs.push(textSpec('ambiance', () => chip('chip-ambiance'), parts.length ? parts.join(' · ') : t('Web.AmbianceGame'), t('Web.Ambiance')));
  }

  if (vm.pending > 0) {
    specs.push(textSpec('pending', pendingChip,
      vm.pending === 1 ? t('Web.RequestsOne') : fmt('Web.RequestsFmt', vm.pending), t('Web.Pending')));
  }

  if (vm.partial) specs.push(textSpec('partial', () => chip(CHIP_QUIET), t('Web.PartialView'), t('Web.PartialViewHint')));
  if (vm.readOnly) specs.push(textSpec('readOnly', () => chip(CHIP_QUIET), t('Web.ReadOnly'), t('Web.ReadOnlyHint')));
  return specs;
}

export function renderBar(s) {
  const combat = s.turn?.active ? { round: s.turn.round, acted: s.turn.acted, total: s.turn.total } : null;
  const partial = s.access === 'coGm';
  const vm = {
    link: s.link,
    // Comme la barre du jeu : pas de pastille d'ambiance pendant un combat.
    ambiance: partial || s.turn?.active ? null : s.ambiance,
    pending: app.privateHidden ? 0 : visiblePending(s).length,
    partial,
    readOnly: app.helloVersion >= 2 && s.access !== 'none' && s.caps.length === 0,
    combat,
  };
  if (!changed('bar', vm)) return;

  const specs = [];
  if (combat) {
    specs.push(textSpec('round', () => el('span', 'bar-round'), fmt('Web.RoundFmt', combat.round)));
    specs.push(textSpec('progress', () => el('span', 'bar-progress'), fmt('Web.ProgressFmt', combat.acted, combat.total)));
  }
  specs.push(sessionSpec(s.link), ...stateSpecs(s, vm));
  // Les deux pastilles qui agissent gardent leur nœud : refaites d'un bloc, elles perdraient le clic en cours.
  syncControls($(BAR_STATE), specs);
}

// Aucune op : la copie reste dans le navigateur, que 127.0.0.1 laisse écrire dans le presse-papiers.
export function copyInvite() {
  const invite = app.current?.link.invite;
  if (!invite) return;
  const failed = () => notify(t('Web.InviteCopyFailed'), invite, 'error');
  let copying;
  try {
    copying = navigator.clipboard.writeText(invite);
  } catch {
    failed();
    return;
  }
  copying.then(() => notify(null, fmt('Lobby.InviteCopiedFmt', invite), 'success'), failed);
}
