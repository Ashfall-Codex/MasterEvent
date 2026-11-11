import { ACTION_DENY_CANCEL, ACTION_DENY_CONFIRM, ADMIT_HIDE_MS, CONFIRM_MS, DENY_MAX, OP_ADMIT, OP_DENY } from './constants.js';
import { command, isBusy, isLocked } from './commands.js';
import { $, ARIA_LABEL, control, el, hidden, setAttr, setBusy, setCount, setData, setText } from './dom.js';
import { fmt, t } from './i18n.js';
import { playerGlyph } from './portraits.js';
import { movedAt, reconcile, syncControls } from './reconcile.js';
import { render, renderSoon } from './render.js';
import { app, can } from './state.js';

const PENDING_LABEL = 'pending-label';
const pendLock = rep => 'pend:' + rep;
const denyLabel = title => fmt('Web.DenyFmt', title);
const rowOf = button => button.closest('.pending-group');

// Demandes masquées quelques secondes après une réponse : le plugin ne retire que le représentant,
// les coéquipiers ne partent qu'avec la file renvoyée par le relais.
const hiddenPending = new Map();
const denyConfirms = new Map();

export function visiblePending(s) {
  const now = Date.now();
  return s.pending
    .map(g => ({ ...g, members: g.members.filter(m => (hiddenPending.get(m.hash) ?? 0) <= now) }))
    .filter(g => g.members.length > 0);
}

function hidePending(hashes) {
  const until = Date.now() + ADMIT_HIDE_MS;
  for (const hash of hashes) hiddenPending.set(hash, until);
  setTimeout(() => {
    const now = Date.now();
    for (const [hash, at] of hiddenPending) if (at <= now) hiddenPending.delete(hash);
    renderSoon();
  }, ADMIT_HIDE_MS + 20);
}

export function dropDenyConfirms() {
  for (const c of denyConfirms.values()) clearTimeout(c.timer);
  denyConfirms.clear();
}

function pendingTitle(g) {
  if (g.label) return fmt('Web.SubgroupFmt', g.label);
  return g.members[0].name;
}

function pendingHead(b) {
  const head = el('div', 'pending-head');
  if (b.label) {
    head.append(hidden(playerGlyph(b.label)), el('span', PENDING_LABEL, fmt('Web.SubgroupFmt', b.label)));
  } else if (b.grouped) {
    // Sous-groupe sans lettre encore attribuée : présenté comme dans le jeu, par son premier membre.
    const others = b.members.length - 1;
    const first = b.members[0].name;
    head.append(el('span', PENDING_LABEL, others === 0 ? first
      : others === 1 ? fmt('Web.PendingTeammatesOne', first) : fmt('Web.PendingTeammatesFmt', first, others)));
  } else {
    head.append(el('span', PENDING_LABEL + ' is-muted', t('Web.NoSubgroup')));
  }
  return head;
}

const pendingParts = new WeakMap();

function patchPending(node, vm) {
  let parts = node ? pendingParts.get(node) : null;
  if (!parts) {
    node = el('li', 'pending-group');
    parts = { body: el('div', 'pending-body'), actions: el('div', 'pending-actions'), sig: null };
    node.append(parts.body, parts.actions);
    pendingParts.set(node, parts);
  }
  const sig = JSON.stringify(vm.body);
  if (sig !== parts.sig) {
    parts.sig = sig;
    const members = el('ul', 'pending-members');
    for (const member of vm.body.members) members.append(el('li', 'pending-member', member.name));
    parts.body.replaceChildren(pendingHead(vm.body), members);
  }
  syncControls(parts.actions, pendingSpecs(vm));
  return node;
}

// Chaque bouton a pour clé son action.
function pendingSpecs(vm) {
  const c = vm.ctl;
  const button = (action, cls, text, label) => ({
    k: action,
    make: () => control(cls, action),
    update: node => {
      setText(node, text);
      setAttr(node, ARIA_LABEL, label);
      setBusy(node, c.busy, false);
      setData(node, { ref: vm.rep });
    },
  });
  if (c.confirm) {
    return [
      { k: 'confirm', make: () => el('span', 'pending-confirm'), update: node => setText(node, c.confirm) },
      button(ACTION_DENY_CONFIRM, 'btn btn-small btn-danger-soft', t('Lobby.Deny'), denyLabel(c.title)),
      button(ACTION_DENY_CANCEL, 'btn btn-small', t('Gm.Cancel'), null),
    ];
  }
  return [
    ...(c.admit ? [button(OP_ADMIT, 'btn btn-small btn-success', t('Lobby.Admit'), fmt('Web.AdmitFmt', c.title))] : []),
    ...(c.deny ? [button(OP_DENY, 'btn btn-small btn-danger', t('Lobby.Deny'), denyLabel(c.title))] : []),
  ];
}

// Masqué, le contenu privé quitte aussi le DOM : un partage d'écran ne montre rien par l'inspecteur.
export function renderPending(s) {
  const groups = app.privateHidden ? [] : visiblePending(s);
  $('sec-pending').hidden = groups.length === 0;
  setCount('count-pending', groups.length);
  const admit = can(OP_ADMIT);
  const deny = can(OP_DENY);
  setText($('pending-hint'), t(admit || deny ? 'Web.PendingHintActions' : 'Web.PendingHint'));

  reconcile($('list-pending'), groups.map(g => {
    const rep = g.members[0].hash;
    const hashes = g.members.map(m => m.hash);
    let confirm = denyConfirms.get(rep);
    // La composition a changé depuis la question : elle se repose sur le sous-groupe tel qu'il est.
    if (confirm && (!deny || confirm.hashes.join(',') !== hashes.join(','))) {
      clearTimeout(confirm.timer);
      denyConfirms.delete(rep);
      confirm = null;
    }
    return {
      rep,
      body: { label: g.label, grouped: g.grouped, members: g.members },
      ctl: {
        admit, deny,
        title: pendingTitle(g),
        busy: isBusy(pendLock(rep)),
        confirm: !confirm ? null : g.members.length === 1
          ? fmt('Web.DenyConfirmOne', g.members[0].name)
          : fmt('Web.DenyConfirmFmt', g.members.length),
      },
    };
  }), vm => 'q:' + vm.rep, patchPending);
}

function findPending(rep) {
  return app.current ? visiblePending(app.current).find(g => g.members[0].hash === rep) ?? null : null;
}

export function onAdmit(button) {
  const rep = button.dataset.ref;
  const group = findPending(rep);
  if (!group || isLocked(pendLock(rep))) return;
  const hashes = group.members.map(m => m.hash);
  command(OP_ADMIT, { hash: rep }, {
    label: fmt('Web.AdmitFmt', pendingTitle(group)),
    lock: pendLock(rep),
    done: ok => {
      if (ok) hidePending(hashes);
      return false;
    },
  });
}

// Refuser vise tout le sous-groupe : la page demande confirmation et n'envoie que les membres montrés.
export function onDeny(button) {
  const rep = button.dataset.ref;
  const group = findPending(rep);
  if (!group || isLocked(pendLock(rep))) return;
  const row = rowOf(button);
  const timer = setTimeout(() => {
    const hadFocus = row?.contains(document.activeElement) ?? false;
    denyConfirms.delete(rep);
    render();
    if (hadFocus) focusPendingRow(row);
  }, CONFIRM_MS);
  denyConfirms.set(rep, { hashes: group.members.map(m => m.hash), timer });
  if (row) movedAt.set(row, Date.now());
  render();
  row?.querySelector('[data-k="deny-cancel"]')?.focus({ preventScroll: true });
}

export function onDenyConfirm(button) {
  const rep = button.dataset.ref;
  const confirm = denyConfirms.get(rep);
  const group = findPending(rep);
  if (!confirm || !group || isLocked(pendLock(rep))) return;
  const row = rowOf(button);
  const hadFocus = row?.contains(document.activeElement) ?? false;
  clearTimeout(confirm.timer);
  denyConfirms.delete(rep);
  command(OP_DENY, { hash: rep, hashes: confirm.hashes.slice(0, DENY_MAX) }, {
    label: denyLabel(pendingTitle(group)),
    lock: pendLock(rep),
    done: ok => {
      if (ok) hidePending(confirm.hashes);
      if (hadFocus) queueMicrotask(() => {
        if (document.activeElement === document.body) focusPendingRow(row);
      });
      return false;
    },
  });
  render();
  if (hadFocus) focusPendingRow(row);
}

export function onDenyCancel(button) {
  const row = rowOf(button);
  const hadFocus = row?.contains(document.activeElement) ?? false;
  const confirm = denyConfirms.get(button.dataset.ref);
  if (confirm) clearTimeout(confirm.timer);
  denyConfirms.delete(button.dataset.ref);
  render();
  if (hadFocus) focusPendingRow(row);
}

// Au clavier, le focus revient à « Refuser » de la ligne, ou à la demande suivante si elle a disparu.
function focusPendingRow(row) {
  const own = row?.isConnected ? row.querySelector('[data-k="deny"]') ?? row.querySelector('[data-k="admit"]') : null;
  (own ?? document.querySelector('.pending-group [data-action]'))?.focus({ preventScroll: true });
}
