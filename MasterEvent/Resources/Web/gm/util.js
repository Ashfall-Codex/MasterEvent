export function load(key) {
  try { return sessionStorage.getItem(key); } catch { return null; }
}

export function save(key, value) {
  try { sessionStorage.setItem(key, value); } catch { /* sans stockage, seul le F5 perd la session */ }
}

export function forget(key) {
  try { sessionStorage.removeItem(key); } catch { /* idem */ }
}

export const signed = n => (n >= 0 ? '+' + n : String(n));
export const clamp = (x, lo, hi) => Math.min(hi, Math.max(lo, x));
export const percent = ratio => (ratio * 100).toFixed(2) + '%';

// ── Lecture défensive des messages ──

export const num = x => (typeof x === 'number' && Number.isFinite(x) ? x : 0);
export const int = x => Math.trunc(num(x));
export const str = x => (typeof x === 'string' ? x : '');
export const opt = x => (typeof x === 'string' && x !== '' ? x : null);
export const list = x => (Array.isArray(x) ? x : []);
export const obj = x => (x && typeof x === 'object' && !Array.isArray(x) ? x : {});
// Seul un vrai booléen compte : toute autre valeur prend la valeur par défaut.
export const flag = (x, fallback) => (typeof x === 'boolean' ? x : fallback);
export const yes = x => flag(x, false);
