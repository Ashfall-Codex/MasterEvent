export const RETRY_MS = 2000;
export const UNREACHABLE_MS = 10000;
export const OPEN_TIMEOUT_MS = 5000;
export const KEY_SESSION = 'masterevent.player.session';
export const KEY_TAB = 'masterevent.player.tab';
export const CREDENTIAL = /^[A-Za-z0-9._~+/=-]{8,512}$/;
export const HEX_COLOR = /^#[0-9a-fA-F]{6}$/;
export const LANG = /^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$/;
export const PORTRAIT = /^data:image\/(?:png|jpeg);base64,[A-Za-z0-9+/]+={0,2}$/;
export const WAYMARKS = new Map([
  ['A', 'wm-a'], ['B', 'wm-b'], ['C', 'wm-c'], ['D', 'wm-d'],
  ['1', 'wm-1'], ['2', 'wm-2'], ['3', 'wm-3'], ['4', 'wm-4'],
]);
export const ATTITUDES = new Map([['hostile', 'Attitude.Hostile'], ['neutral', 'Attitude.Neutral'], ['friendly', 'Attitude.Friendly']]);

export const VIEW = Object.freeze({
  instance: 'instance',
  versionRejected: 'versionRejected',
  joinRejected: 'joinRejected',
  awaitingApproval: 'awaitingApproval',
  offline: 'offline',
  reconnecting: 'reconnecting',
  outOfSession: 'outOfSession',
  joining: 'joining',
  handover: 'handover',
  gmAway: 'gmAway',
  inSession: 'inSession',
});
export const LINK = Object.freeze({
  offline: 'offline',
  reconnecting: 'reconnecting',
  serverOnly: 'serverOnly',
  inSession: 'inSession',
});
export const HOLD = Object.freeze({ none: 'none', sent: 'sent', handover: 'handover', gmAway: 'gmAway' });
export const KIND = Object.freeze({ player: 'player', marker: 'marker', npc: 'npc', other: 'other' });
export const TAB = Object.freeze({ list: 'list', sheet: 'sheet' });
export const OP = Object.freeze({
  selfRoll: 'selfRoll',
  selfAnswer: 'selfAnswer',
  selfEndTurn: 'selfEndTurn',
  selfDismiss: 'selfDismiss',
});
export const LOCK = Object.freeze({ bar: 'bar', roll: 'roll', dismiss: 'dismiss' });
export const PERCENT = 'percent';

export const LINK_STATES = new Set(Object.values(LINK));
export const VIEWS = new Set(Object.values(VIEW));
export const REJECTIONS = new Set(['denied', 'roomLimit', 'rateLimited', 'invalid']);
export const HOLDS = new Set(Object.values(HOLD));
export const ENTRY_KINDS = new Set(Object.values(KIND));
export const TABS = Object.values(TAB);
export const OPS = Object.values(OP);

// Les prédicats de PlayerWindow : message à la place des listes, salle montrée, salle où l'on répond.
export const STATE_VIEWS = new Set([VIEW.instance, VIEW.versionRejected, VIEW.joinRejected, VIEW.awaitingApproval,
  VIEW.offline, VIEW.outOfSession, VIEW.joining]);
export const SESSION_VIEWS = new Set([VIEW.inSession, VIEW.handover, VIEW.gmAway, VIEW.reconnecting]);
export const ROOM_VIEWS = new Set([VIEW.inSession, VIEW.handover, VIEW.gmAway]);

// Le plugin abandonne une commande au bout de 2,5 s : ce que la page croit perdue ne s'exécute pas après.
export const ANSWER_MS = 5000;
export const SETTLE_MS = 1500;
// Sous le budget de 6 par seconde du plugin.
export const RATE_PER_SECOND = 5;
export const PRIMARY_INERT_MS = 600;
export const NOTICE_MS = 6000;
export const NOTICES_MAX = 3;
export const PAGE_NOTICES_MAX = 2;
// VitalsControls.StatFilterThreshold : le champ de recherche paraît au-delà.
export const STAT_FILTER_THRESHOLD = 5;
export const NARROW = '(max-width: 899px)';

export const SCREENS = {
  connecting: { title: 'PlayerWeb.ConnectingTitle', busy: true },
  unreachable: { title: 'PlayerWeb.UnreachableTitle', text: 'PlayerWeb.UnreachableText', busy: true },
  expired: { title: 'PlayerWeb.ExpiredTitle', text: 'PlayerWeb.ExpiredText' },
  none: { title: 'PlayerWeb.NoneTitle', text: 'PlayerWeb.NoneText' },
  revoked: { title: 'PlayerWeb.RevokedTitle', text: 'PlayerWeb.RevokedText' },
  ended: { title: 'PlayerWeb.EndedTitle', text: 'PlayerWeb.EndedText' },
  unloaded: { title: 'PlayerWeb.UnloadedTitle', text: 'PlayerWeb.UnloadedText' },
  // Le ticket refusé pour 4409 est consommé : réessayer ne servirait à rien.
  busy: { title: 'PlayerWeb.BusyTitle', text: 'PlayerWeb.BusyText' },
};
