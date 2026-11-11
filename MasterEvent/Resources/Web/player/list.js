import { lobbyCode, offlineHint, rejectionHint, versionHint } from './bar.js';
import { KIND, STATE_VIEWS, VIEW } from './constants.js';
import { $, setCount, setText } from './dom.js';
import { makeRow, rowKey } from './entries.js';
import { portraitRev } from './portraits.js';
import { reconcile } from './rows.js';
import { fmt, t } from './texts.js';

const IN_GAME_HINT = 'PlayerWeb.InGameHint';

let expanded = null;
let scrolledBlock = null;
let activeBlock = null;

function stateMessage(s) {
  switch (s.view) {
    case VIEW.outOfSession:
      return { title: t('Lobby.JoinTitle'), text: t('PlayerWeb.JoinHint') };
    case VIEW.joining:
      return { title: t('PlayerView.LinkJoining'), text: t('PlayerView.OnFieldEmptyHint') };
    case VIEW.offline:
      return { title: t('Gm.Local'), text: offlineHint(s), note: t(IN_GAME_HINT) };
    case VIEW.awaitingApproval:
      return { title: t('PlayerView.PendingTitle'), text: fmt('PlayerView.PendingHintFmt', lobbyCode(s)), note: t(IN_GAME_HINT) };
    case VIEW.joinRejected:
      return { title: t('PlayerView.RejectedTitle'), text: rejectionHint(s), note: t(IN_GAME_HINT) };
    case VIEW.versionRejected:
      return { title: t('PlayerView.VersionTitle'), text: versionHint(s) };
    default:
      return { title: t('PlayerView.InstanceTitle'), text: t('PlayerView.InstanceHint') };
  }
}

function renderStateMessage(s) {
  const msg = stateMessage(s);
  setText($('state-title'), msg.title);
  setText($('state-text'), msg.text);
  setText($('state-note'), msg.note ?? '');
  $('state-note').hidden = !msg.note;
}

function fieldItem(f, s, reserve) {
  return { k: 'f', f, d: s.display, open: f.kind === KIND.marker && expanded === f.ref, reserve };
}

function renderScene(s) {
  const field = s.field.map(f => fieldItem(f, s, false));
  setCount('count-field', field.length);
  const empty = $('empty-field');
  empty.hidden = field.length > 0;
  empty.classList.toggle('is-compact', s.players.length > 0);
  reconcile($('list-field'), field, rowKey, makeRow);

  $('players-block').hidden = s.players.length === 0;
  setText($('count-players'), String(s.players.length));
  reconcile($('list-players'), s.players.map(p => ({ k: 'p', p, pic: portraitRev(p.hash), d: s.display, reserve: false })), rowKey, makeRow);
}

// Ce que la page sait de la cible d'une entrée, par sa ref : sa propre fiche, un autre joueur ou le terrain.
function entryTarget(s, e) {
  if (!e.ref) return null;
  if (e.kind === KIND.player) {
    if (e.mine && s.me) return { hp: s.me.hp, hpMax: s.me.hpMax, shield: s.me.shield, connected: true };
    const p = s.players.find(x => x.hash === e.ref);
    return p ? { hp: p.hp, hpMax: p.hpMax, shield: p.shield, connected: p.connected } : null;
  }
  if (e.kind === KIND.marker || e.kind === KIND.npc) return s.field.find(f => f.kind === e.kind && f.ref === e.ref) ?? null;
  return null;
}

function renderCombat(s) {
  const turn = s.turn;
  setText($('turn-sub'), fmt('Turns.SubtitleFmt', turn.diceMax));

  const items = [];
  const groups = new Set();
  turn.entries.forEach(e => {
    // Le MJ peut effacer le nom d'un groupe : l'en-tête n'aurait plus rien à dire.
    if (e.group && !groups.has(e.group)) {
      groups.add(e.group);
      const members = turn.entries.filter(x => x.group === e.group);
      items.push({
        k: 'g',
        id: e.group,
        label: e.groupLabel ?? t('Turns.GroupUnnamed'),
        active: members.some(x => x.active),
        acted: members.every(x => x.acted),
      });
    }
    const x = entryTarget(s, e);
    items.push({
      k: 'e', e, x, pic: e.kind === KIND.player ? portraitRev(e.ref) : 0, d: s.display, myTurn: !!s.me?.myTurn,
      open: e.kind === KIND.marker && !!x && expanded === e.ref,
    });
  });
  reconcile($('list-turn'), items, rowKey, makeRow);

  const also = [
    ...turn.alsoOnField.field.map(ref => s.field.find(f => f.ref === ref)).filter(Boolean).map(f => fieldItem(f, s, true)),
    ...turn.alsoOnField.players.map(hash => s.players.find(p => p.hash === hash)).filter(Boolean)
      .map(p => ({ k: 'p', p, pic: portraitRev(p.hash), d: s.display, reserve: true })),
  ];
  $('also-block').hidden = also.length === 0;
  reconcile($('list-also'), also, rowKey, makeRow);

  const active = turn.activeIndex >= 0 ? turn.entries[turn.activeIndex] : null;
  activeBlock = active ? turn.round + ':' + (active.group ?? turn.activeIndex) : null;
}

// La liste ne défile vers le bloc actif qu'une fois, quand il change. Masquée (segment Ma fiche,
// premier rendu), elle ne peut pas défiler : le bloc reste à atteindre jusqu'à ce qu'elle s'affiche.
export function scrollToActiveBlock() {
  if (!activeBlock || activeBlock === scrolledBlock) return;
  const row = $('list-turn').querySelector('.turn-row.is-active');
  if (!row || row.offsetParent === null) return;
  scrolledBlock = activeBlock;
  row.scrollIntoView({ block: 'nearest' });
}

export function forgetScrolledBlock() {
  scrolledBlock = null;
}

export function toggleDetail(ref) {
  expanded = expanded === ref ? null : ref;
}

export function closeDetails() {
  expanded = null;
}

export function renderList(s) {
  // Un marqueur caché ou parti referme son détail.
  if (expanded && !s.field.some(f => f.kind === KIND.marker && f.ref === expanded)) expanded = null;

  const stateful = STATE_VIEWS.has(s.view);
  $('state-msg').hidden = !stateful;
  $('scene').hidden = stateful || !!s.turn;
  $('combat').hidden = stateful || !s.turn;
  if (!s.turn) activeBlock = null;
  if (stateful) renderStateMessage(s);
  else if (s.turn) renderCombat(s);
  else renderScene(s);
}
