// Écrans possibles avant le hello, donc avant de connaître les textes du plugin.
const FALLBACK = new Map([
  ['PlayerWeb.Title', 'Vue joueur · MasterEvent'],
  ['PlayerWeb.ConnectingTitle', 'Connexion au jeu...'],
  ['PlayerWeb.UnreachableTitle', 'Le jeu ne répond pas'],
  ['PlayerWeb.UnreachableText', 'Nouvel essai toutes les 2 secondes. Le jeu et MasterEvent doivent rester lancés.'],
  ['PlayerWeb.ExpiredTitle', 'Lien expiré'],
  ['PlayerWeb.ExpiredText', 'Ce lien a expiré. Rouvrez-le depuis la vue joueur du jeu.'],
  ['PlayerWeb.BusyTitle', "Trop d'écrans ouverts"],
  ['PlayerWeb.BusyText', 'Fermez un onglet, puis rouvrez le lien depuis le jeu.'],
]);

let strings = new Map();
let numberFormat = null;
let timeFormat = null;

export const t = key => strings.get(key) ?? FALLBACK.get(key) ?? key;
export const fmt = (key, ...args) =>
  t(key).replace(/\{(\d+)(?::[^}]*)?\}/g, (match, index) => (Number(index) < args.length ? String(args[Number(index)]) : match));
export const hasText = key => strings.has(key);

export function setTexts(values) {
  strings = new Map();
  for (const [key, value] of Object.entries(values))
    if (typeof value === 'string') strings.set(key, value);
}

export function resetFormats() {
  numberFormat = null;
  timeFormat = null;
}

function intl(make) {
  try { return make(document.documentElement.lang || 'fr'); } catch { return make('fr'); }
}

export function decimal(value) {
  numberFormat ??= intl(lang => new Intl.NumberFormat(lang, { maximumFractionDigits: 1 }));
  return numberFormat.format(value);
}

export function clock(ms) {
  timeFormat ??= intl(lang => new Intl.DateTimeFormat(lang, { hour: '2-digit', minute: '2-digit' }));
  return timeFormat.format(new Date(ms));
}
