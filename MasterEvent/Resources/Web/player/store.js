export const app = {
  current: null,
  // Fin de la page (bye, lien expiré, onglet de trop) : plus de reconnexion.
  final: false,
  lostAt: 0,
  helloVersion: 0,
  screen: 'connecting',
};

// Le rendu s'enregistre au démarrage : les modules qui le demandent n'importent pas render.js, qui les importe.
let renderer = null;
let renderQueued = false;

export function setRenderer(fn) {
  renderer = fn;
}

export function renderSoon() {
  if (renderQueued) return;
  renderQueued = true;
  queueMicrotask(() => {
    renderQueued = false;
    renderer();
  });
}
