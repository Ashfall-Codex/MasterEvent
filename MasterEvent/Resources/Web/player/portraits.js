import { PORTRAIT } from './constants.js';
import { el } from './dom.js';

// Portraits UmbraSync par empreinte de joueur. rev change avec la data URI : les signatures des
// lignes le portent à sa place.
let portraits = new Map();
let portraitSerial = 0;
const imageUris = new WeakMap();
let recycled = null;

export const portraitRev = hash => (hash ? portraits.get(hash)?.rev ?? 0 : 0);

// Une balise neuve, même de la même data URI, peut rester vide le temps du décodage : ce qui se
// reconstruit dans box reprend les images que box montrait déjà.
export function recycling(box, build) {
  recycled = new Map();
  for (const img of box.getElementsByTagName('img')) recycled.set(imageUris.get(img), img);
  try {
    return build();
  } finally {
    recycled = null;
  }
}

function portraitImage(uri) {
  const kept = recycled?.get(uri);
  if (kept) {
    recycled.delete(uri);
    return kept;
  }
  const img = document.createElement('img');
  img.alt = '';
  img.decoding = 'async';
  img.src = uri;
  imageUris.set(img, uri);
  return img;
}

export function playerGlyph(hash) {
  const pic = hash ? portraits.get(hash) : null;
  if (!pic) return el('span', 'glyph glyph-player');
  const box = el('span', 'glyph glyph-avatar');
  box.append(portraitImage(pic.uri));
  return box;
}

// Gardé même avant le premier état : le plugin ne renvoie la liste que lorsqu'elle change.
export function acceptPortraits(items) {
  const next = new Map();
  for (const [hash, uri] of Object.entries(items)) {
    if (hash === '' || typeof uri !== 'string' || !PORTRAIT.test(uri)) continue;
    const known = portraits.get(hash);
    next.set(hash, known && known.uri === uri ? known : { uri, rev: ++portraitSerial });
  }
  portraits = next;
}

export function clearPortraits() {
  portraits = new Map();
}
