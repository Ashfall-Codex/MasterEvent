import { OPEN_TIMEOUT_MS } from './constants.js';

let socket = null;

// Faux si le navigateur refuse d'ouvrir le socket. Seul le socket courant est écouté.
export function openSocket(onData, onClosed) {
  let ws;
  try {
    ws = new WebSocket('ws://' + location.host + '/ws');
  } catch {
    return false;
  }
  socket = ws;

  const openTimer = setTimeout(() => {
    if (ws.readyState === WebSocket.CONNECTING) ws.close();
  }, OPEN_TIMEOUT_MS);

  ws.addEventListener('open', () => clearTimeout(openTimer));
  ws.addEventListener('message', event => {
    if (socket === ws) onData(event.data);
  });
  ws.addEventListener('close', event => {
    clearTimeout(openTimer);
    if (socket !== ws) return;
    socket = null;
    onClosed(event.code);
  });
  return true;
}

export function send(message) {
  if (socket && socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify(message));
}

export function closeSocket() {
  if (!socket) return;
  const ws = socket;
  socket = null;
  ws.close(1000);
}
