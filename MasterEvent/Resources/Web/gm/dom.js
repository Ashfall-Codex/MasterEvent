export const ARIA_LABEL = 'aria-label';
export const ARIA_DISABLED = 'aria-disabled';
export const ARIA_INVALID = 'aria-invalid';

export const $ = id => document.getElementById(id);

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

// null ou undefined retire l'attribut.
export function setAttr(node, name, value) {
  if (value == null) {
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
  setAttr(node, ARIA_DISABLED, busy || disabled ? 'true' : null);
}

export const isDisabled = node => node.getAttribute(ARIA_DISABLED) === 'true';

export function setCount(id, n) {
  const node = $(id);
  const value = n > 0 ? String(n) : '';
  if (node.textContent !== value) node.textContent = value;
}

const SVG = 'http://www.w3.org/2000/svg';
const pip = (cx, cy) => ['circle', { cx, cy, r: 1.3, class: 'pip' }];
const ICONS = {
  minus: [['path', { d: 'M6 12h12' }]],
  plus: [['path', { d: 'M12 6v12M6 12h12' }]],
  close: [['path', { d: 'M6.5 6.5l11 11M17.5 6.5l-11 11' }]],
  die: [
    ['rect', { x: 4, y: 4, width: 16, height: 16, rx: 3 }],
    pip(9, 9),
    pip(12, 12),
    pip(15, 15),
  ],
  megaphone: [['path', { d: 'M4 10v4h3l6 4V6l-6 4H4z' }], ['path', { d: 'M16.5 9.5a3.5 3.5 0 0 1 0 5M18.5 7a7 7 0 0 1 0 10' }]],
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

export function hidden(node) {
  node.setAttribute('aria-hidden', 'true');
  return node;
}

export function pill(text, tone) {
  return el('span', 'pill pill-' + tone, text);
}

export function chip(cls, title) {
  const node = el('span', 'chip' + (cls ? ' ' + cls : ''));
  if (title) node.title = title;
  return node;
}
