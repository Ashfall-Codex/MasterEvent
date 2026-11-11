// Écrans possibles avant le hello, donc avant de connaître les textes du plugin.
export const FALLBACK = new Map([
  ['Web.Title', 'Vue MJ · MasterEvent'],
  ['Web.ConnectingTitle', 'Connexion au jeu...'],
  ['Web.UnreachableTitle', 'Le jeu ne répond pas'],
  ['Web.UnreachableText', 'Nouvel essai toutes les 2 secondes. Le jeu et MasterEvent doivent rester lancés.'],
  ['Web.ExpiredTitle', 'Lien expiré'],
  ['Web.ExpiredText', 'Ce lien a expiré. Rouvrez-le depuis le jeu.'],
  ['Web.BusyTitle', "Trop d'écrans ouverts"],
  ['Web.BusyText', 'Fermez un onglet, puis rouvrez le lien depuis le jeu.'],
]);

export const RETRY_MS = 2000;
export const UNREACHABLE_MS = 10000;
export const OPEN_TIMEOUT_MS = 5000;
export const KEY_SESSION = 'masterevent.remote.session';
export const KEY_PRIVATE = 'masterevent.remote.private';
export const KEY_TAB = 'masterevent.remote.tab';
export const KEY_THRESHOLD = 'masterevent.remote.threshold';
export const CREDENTIAL = /^[A-Za-z0-9._~+/=-]{8,512}$/;
export const HEX_COLOR = /^#[0-9a-fA-F]{6}$/;
export const GROUP_LABEL = /^[A-H]$/;
export const LANG = /^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$/;
export const PORTRAIT = /^data:image\/(?:png|jpeg);base64,[A-Za-z0-9+/]+={0,2}$/;
export const WAYMARKS = new Map([
  ['A', 'wm-a'], ['B', 'wm-b'], ['C', 'wm-c'], ['D', 'wm-d'],
  ['1', 'wm-1'], ['2', 'wm-2'], ['3', 'wm-3'], ['4', 'wm-4'],
]);
export const ATTITUDES = new Map([['hostile', 'Web.Hostile'], ['neutral', 'Web.Neutral'], ['friendly', 'Web.Friendly']]);
export const LINK_STATES = new Set(['offline', 'reconnecting', 'serverOnly', 'inSession']);

export const KIND_PLAYER = 'player';
export const KIND_MARKER = 'marker';
export const KIND_NPC = 'npc';
export const PERCENT = 'percent';

export const TAB_PLAYERS = 'players';
export const TABS = ['turn', TAB_PLAYERS, 'field', 'rolls', 'pending', 'notes'];

export const OP_TURN_START = 'turnStart';
export const OP_TURN_END = 'turnEnd';
export const OP_NEXT_ROUND = 'turnNextRound';
export const OP_SET_ACTED = 'turnSetActed';
export const OP_VITALS = 'vitals';
export const OP_ROLL = 'roll';
export const OP_ROLL_REQUEST = 'rollRequest';
export const OP_ADMIT = 'admit';
export const OP_DENY = 'deny';
export const OP_ANNOUNCE = 'announce';
export const OPS = [OP_TURN_START, OP_TURN_END, OP_NEXT_ROUND, OP_SET_ACTED, OP_VITALS, OP_ROLL, OP_ROLL_REQUEST, OP_ADMIT, OP_DENY, OP_ANNOUNCE];

// Le plugin abandonne une commande au bout de 2,5 s : ce que la page croit perdue ne s'exécute pas après.
export const ANSWER_MS = 5000;
export const SETTLE_MS = 1500;
// Sous le budget de 6 par seconde du plugin, partagé par toutes les pages.
export const RATE_PER_SECOND = 5;
export const MERGE_MS = 300;
export const PRIMARY_INERT_MS = 600;
export const CONTROL_INERT_MS = 400;
export const NOTICE_MS = 6000;
export const NOTICES_MAX = 3;
export const CONFIRM_MS = 6000;
export const ADMIT_HIDE_MS = 3000;
export const ANNOUNCE_MAX = 180;
export const ANNOUNCE_LINES = 4;
export const VALUE_MAX = 99999;
export const DENY_MAX = 8;

export const ACTION_DETAIL = 'detail';
export const ACTION_DETAIL_CLOSE = 'detail-close';
export const ACTION_ROLL_STAT = 'roll-stat';
export const ACTION_DENY_CONFIRM = 'deny-confirm';
export const ACTION_DENY_CANCEL = 'deny-cancel';
// Les vitales additionnent leurs clics ; les autres gestes ne comptent qu'un clic à la fois.
export const REPEATABLE = new Set(['step', ACTION_DETAIL, ACTION_DETAIL_CLOSE]);
// Caractères de contrôle de .NET (catégorie Cc), saut de ligne excepté.
export const CONTROL_CHARS = /[\u0000-\u0009\u000B-\u001F\u007F-\u009F]/g;

export const SCREENS = {
  connecting: { title: 'Web.ConnectingTitle', busy: true },
  unreachable: { title: 'Web.UnreachableTitle', text: 'Web.UnreachableText', busy: true },
  expired: { title: 'Web.ExpiredTitle', text: 'Web.ExpiredText' },
  none: { title: 'Web.NoneTitle', text: 'Web.NoneText' },
  revoked: { title: 'Web.RevokedTitle', text: 'Web.RevokedText' },
  ended: { title: 'Web.EndedTitle', text: 'Web.EndedText' },
  unloaded: { title: 'Web.UnloadedTitle', text: 'Web.UnloadedText' },
  // Le ticket refusé pour 4409 est consommé : réessayer ne servirait à rien.
  busy: { title: 'Web.BusyTitle', text: 'Web.BusyText' },
};
