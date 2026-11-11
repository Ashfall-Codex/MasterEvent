import { $, chip, el } from './dom.js';
import { fmt, t } from './i18n.js';
import { visiblePending } from './pending.js';
import { changed } from './reconcile.js';
import { app } from './state.js';

const CHIP_QUIET = 'chip-quiet';

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

function turnProgress(turn) {
  const blocks = new Map();
  turn.entries.forEach((e, i) => {
    const key = e.groupId ? 'g:' + e.groupId : 'e:' + i;
    blocks.set(key, (blocks.get(key) ?? true) && e.hasActed);
  });
  let acted = 0;
  for (const done of blocks.values()) if (done) acted++;
  return { acted, total: blocks.size };
}

function sessionChip(link) {
  const linkState = link.suspended ? 'suspended' : link.state;
  const session = chip('chip-session', linkText(link));
  session.append(el('span', 'dot link-' + linkState));
  if (link.lobbyCode) {
    session.append(el('span', null, t('Web.Lobby')), el('span', 'code', link.lobbyCode),
      el('span', 'sep', '·'), el('span', null, fmt('Web.OnlineFmt', link.partyOnline, link.partyShown)));
  } else {
    session.append(el('span', null, linkText(link)));
  }
  return session;
}

function textChip(cls, title, text) {
  const node = chip(cls, title);
  node.textContent = text;
  return node;
}

// Pastilles d'état après celle du lien, dans l'ordre de la barre du jeu.
function stateChips(s, vm) {
  const items = [];
  if (s.link.suspended) items.push(textChip('chip-warning', t('Web.LinkSuspendedHint'), t('Web.LinkSuspended')));

  // Comme l'onglet Scène : l'ambiance reste au MJ.
  if (vm.ambiance) {
    const parts = [vm.ambiance.weather, vm.ambiance.time].filter(Boolean);
    items.push(textChip('chip-ambiance', t('Web.Ambiance'), parts.length ? parts.join(' · ') : t('Web.AmbianceGame')));
  }

  if (vm.pending > 0) {
    const text = vm.pending === 1 ? t('Web.RequestsOne') : fmt('Web.RequestsFmt', vm.pending);
    items.push(textChip('chip-pending', t('Web.Pending'), text));
  }

  if (vm.partial) items.push(textChip(CHIP_QUIET, t('Web.PartialViewHint'), t('Web.PartialView')));
  if (vm.readOnly) items.push(textChip(CHIP_QUIET, t('Web.ReadOnlyHint'), t('Web.ReadOnly')));
  return items;
}

export function renderBar(s) {
  const combat = s.turn?.active ? { round: s.turn.round, ...turnProgress(s.turn) } : null;
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

  const items = [];
  if (combat) {
    items.push(el('span', 'bar-round', fmt('Web.RoundFmt', combat.round)));
    items.push(el('span', 'bar-progress', fmt('Web.ProgressFmt', combat.acted, combat.total)));
  }
  items.push(sessionChip(s.link), ...stateChips(s, vm));
  $('bar-state').replaceChildren(...items);
}
