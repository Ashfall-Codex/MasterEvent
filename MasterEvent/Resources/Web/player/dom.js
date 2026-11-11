export const $ = id => document.getElementById(id);
export const ARIA_LABEL = 'aria-label';

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

export function el(tag, cls, content) {
  const node = document.createElement(tag);
  if (cls) node.className = cls;
  if (content != null) node.textContent = String(content);
  return node;
}

export function control(cls, action, text) {
  const node = el('button', cls, text);
  node.type = 'button';
  node.dataset.action = action;
  return node;
}

// N'écrit que ce qui change : ces nœuds sont mis à jour à chaque état.
export function setText(node, text) {
  if (node.textContent !== text) node.textContent = text;
}

const ABSENT = new Set([null, undefined, false]);

export function setAttr(node, name, value) {
  if (ABSENT.has(value)) {
    if (node.hasAttribute(name)) node.removeAttribute(name);
  } else if (node.getAttribute(name) !== String(value)) {
    node.setAttribute(name, String(value));
  }
}

export function setData(node, values) {
  for (const [key, value] of Object.entries(values)) {
    if (value == null) delete node.dataset[key];
    else if (node.dataset[key] !== String(value)) node.dataset[key] = String(value);
  }
}

export function setBusy(node, busy, disabled) {
  setAttr(node, 'aria-busy', busy ? 'true' : null);
  setAttr(node, 'aria-disabled', busy || disabled ? 'true' : null);
}

export function setTitle(node, text) {
  if (text) {
    if (node.title !== text) node.title = text;
  } else if (node.hasAttribute('title')) {
    node.removeAttribute('title');
  }
}

export function setCount(id, n) {
  setText($(id), n > 0 ? String(n) : '');
}

const SVG = 'http://www.w3.org/2000/svg';
const pip = c => ['circle', { cx: c, cy: c, r: 1.3, class: 'pip' }];
const ICONS = {
  close: [['path', { d: 'M6.5 6.5l11 11M17.5 6.5l-11 11' }]],
  die: [['rect', { x: 4, y: 4, width: 16, height: 16, rx: 3 }], pip(9), pip(12), pip(15)],
};

export function icon(name) {
  const svg = document.createElementNS(SVG, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('focusable', 'false');
  svg.setAttribute('class', 'icon');
  for (const [tag, attrs] of ICONS[name]) {
    const part = document.createElementNS(SVG, tag);
    for (const [key, value] of Object.entries(attrs)) part.setAttribute(key, String(value));
    svg.append(part);
  }
  return svg;
}
