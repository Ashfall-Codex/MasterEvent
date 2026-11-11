// ── Rendu par lignes réutilisées ──

// Une ligne n'est reconstruite que si ses données changent : à 4 états par seconde,
// tout reconstruire ferait sauter le survol et la sélection de texte.
let rowCache = new WeakMap();
let sectionSigs = new Map();
// Ligne qui vient d'apparaître ou de changer de place : un clic qui lui arrive visait sans doute une autre ligne.
export const movedAt = new WeakMap();

function uniqueKeys(items, keyOf) {
  const keys = [];
  const seen = new Set();
  for (const item of items) {
    let key = keyOf(item);
    while (seen.has(key)) key += '+';
    seen.add(key);
    keys.push(key);
  }
  return keys;
}

// Rend la ligne si ses données ont changé : le nœud neuf remplace l'ancien à sa place.
function refreshRow(old, sig, item, make) {
  const node = old?.node ?? null;
  if (old && old.sig === sig) return node;
  const made = make(node, item);
  if (node && made !== node) node.replaceWith(made);
  return made;
}

// make(node, item) rend le nœud de la ligne : le même, mis à jour, ou un nouveau.
export function reconcile(container, items, keyOf, make, onNew) {
  const previous = rowCache.get(container);
  const keys = uniqueKeys(items, keyOf);
  const seen = new Set(keys);

  // Les lignes disparues partent d'abord : sinon chaque ligne suivante serait déplacée d'un cran.
  if (previous) {
    for (const [key, entry] of previous)
      if (!seen.has(key)) entry.node.remove();
  }

  const now = Date.now();
  const next = new Map();
  let cursor = container.firstChild;
  items.forEach((item, index) => {
    const key = keys[index];
    const sig = JSON.stringify(item);
    const old = previous?.get(key);
    const node = refreshRow(old, sig, item, make);
    if (old && old.node === cursor) cursor = node;
    if (!old && previous) onNew?.(node);
    if (previous && (!old || old.index !== index)) movedAt.set(node, now);
    next.set(key, { node, sig, index });
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

// Les commandes d'une boîte gardent leurs nœuds d'un état à l'autre : un clic commencé sur un
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
