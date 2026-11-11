import { control, el, setAttr, setData, setText, setTitle } from './dom.js';
import { recycling } from './portraits.js';

// Une ligne n'est reconstruite que si ses données changent : à 4 états par seconde,
// tout reconstruire ferait sauter le survol et la sélection de texte.
let rowCache = new WeakMap();
let sectionSigs = new Map();

// make(node, item) rend le nœud de la ligne : le même, mis à jour, ou un nouveau.
export function reconcile(container, items, keyOf, make, onNew) {
  const previous = rowCache.get(container);
  const keys = [];
  const seen = new Set();
  for (const item of items) {
    let key = keyOf(item);
    while (seen.has(key)) key += '+';
    seen.add(key);
    keys.push(key);
  }

  // Les lignes disparues partent d'abord : sinon chaque ligne suivante serait déplacée d'un cran.
  if (previous) {
    for (const [key, entry] of previous)
      if (!seen.has(key)) entry.node.remove();
  }

  const next = new Map();
  let cursor = container.firstChild;
  items.forEach((item, index) => {
    const key = keys[index];
    const sig = JSON.stringify(item);
    const old = previous?.get(key);
    let node = old?.node ?? null;
    if (!old || old.sig !== sig) {
      const made = make(node, item);
      if (node && made !== node) {
        if (cursor === node) cursor = made;
        node.replaceWith(made);
      }
      node = made;
      if (!old && previous) onNew?.(node);
    }
    next.set(key, { node, sig });
    if (node === cursor) cursor = cursor.nextSibling;
    else container.insertBefore(node, cursor);
  });

  while (cursor) {
    const stale = cursor;
    cursor = cursor.nextSibling;
    stale.remove();
  }
  rowCache.set(container, next);
}

export const rebuild = build => (node, item) => build(item);

export function changed(name, value) {
  const sig = JSON.stringify(value);
  if (sectionSigs.get(name) === sig) return false;
  sectionSigs.set(name, sig);
  return true;
}

export function forceRebuild() {
  rowCache = new WeakMap();
  sectionSigs = new Map();
}

// Les boutons d'une boîte gardent leurs nœuds d'un état à l'autre : un clic commencé sur un
// bouton remplacé entre l'appui et le relâchement serait perdu.
export function syncControls(box, specs) {
  const existing = new Map();
  for (const child of box.children) existing.set(child.dataset.k, child);
  let cursor = box.firstElementChild;
  for (const spec of specs) {
    let node = existing.get(spec.k);
    existing.delete(spec.k);
    if (!node) {
      node = spec.make();
      node.dataset.k = spec.k;
    }
    spec.update?.(node);
    if (node === cursor) cursor = cursor.nextElementSibling;
    else box.insertBefore(node, cursor);
  }
  for (const node of existing.values()) node.remove();
}

// Le corps d'une ligne est réécrit quand il change ; le nom, qui peut être un bouton, garde son nœud.
const rowParts = new WeakMap();

export function patchRow(node, vm, body) {
  let parts = node ? rowParts.get(node) : null;
  if (!parts) {
    node = el('li', 'row');
    parts = {
      lead: el('span', 'row-lead'),
      head: el('span', 'row-head'),
      main: el('div', 'row-main'),
      line: el('div', 'row-line'),
      name: null,
      extra: el('span', 'row-extra'),
      hp: el('div', 'row-hp'),
      hpMeter: el('span', 'row-hp-meter'),
      rest: el('span', 'row-rest'),
    };
    parts.line.append(parts.extra);
    parts.hp.append(parts.hpMeter);
    parts.main.append(parts.line, parts.hp, parts.rest);
    node.append(parts.lead, parts.head, parts.main);
    rowParts.set(node, parts);
  }

  const b = recycling(parts.head, () => body(vm));
  node.className = 'row' + (b.cls ? ' ' + b.cls : '');
  setTitle(node, b.title);
  parts.lead.replaceChildren(...(b.lead ?? []));
  parts.head.replaceChildren(...b.head);
  setName(parts, b.name);
  parts.extra.replaceChildren(...b.extra);
  parts.hpMeter.replaceChildren(...(b.hp ? [b.hp] : []));
  parts.hp.hidden = !b.hp;
  parts.rest.replaceChildren(...(b.rest ?? []));
  return node;
}

function setName(parts, name) {
  const button = !!name.action;
  let node = parts.name;
  if (!node || (node.tagName === 'BUTTON') !== button) {
    const next = button ? control('name', name.action) : el('span', 'name');
    if (node) node.replaceWith(next);
    else parts.line.insertBefore(next, parts.extra);
    parts.name = node = next;
  }
  node.className = 'name' + (button ? ' name-btn' : '') + (name.muted ? ' is-muted' : '');
  setText(node, name.text);
  if (button) {
    node.dataset.action = name.action;
    setData(node, { ref: name.ref ?? null });
    setTitle(node, name.title);
    setAttr(node, 'aria-expanded', name.expanded == null ? null : String(name.expanded));
  }
}
