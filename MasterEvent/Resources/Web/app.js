'use strict';

(function () {
  // Écrans possibles avant le hello, donc avant de connaître les textes du plugin.
  const FALLBACK = new Map([
    ['Web.Title', 'Vue MJ · MasterEvent'],
    ['Web.ConnectingTitle', 'Connexion au jeu...'],
    ['Web.UnreachableTitle', 'Le jeu ne répond pas'],
    ['Web.UnreachableText', 'Nouvel essai toutes les 2 secondes. Le jeu et MasterEvent doivent rester lancés.'],
    ['Web.ExpiredTitle', 'Lien expiré'],
    ['Web.ExpiredText', 'Ce lien a expiré. Rouvrez-le depuis le jeu.'],
    ['Web.BusyTitle', "Trop d'écrans ouverts"],
    ['Web.BusyText', 'Fermez un onglet, puis rouvrez le lien depuis le jeu.'],
  ]);

  const RETRY_MS = 2000;
  const UNREACHABLE_MS = 10000;
  const OPEN_TIMEOUT_MS = 5000;
  const KEY_SESSION = 'masterevent.remote.session';
  const KEY_PRIVATE = 'masterevent.remote.private';
  const KEY_TAB = 'masterevent.remote.tab';
  const KEY_THRESHOLD = 'masterevent.remote.threshold';
  const CREDENTIAL = /^[A-Za-z0-9._~+/=-]{8,512}$/;
  const HEX_COLOR = /^#[0-9a-fA-F]{6}$/;
  const GROUP_LABEL = /^[A-H]$/;
  const LANG = /^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$/;
  const PORTRAIT = /^data:image\/(?:png|jpeg);base64,[A-Za-z0-9+/]+={0,2}$/;
  const WAYMARKS = new Map([
    ['A', 'wm-a'], ['B', 'wm-b'], ['C', 'wm-c'], ['D', 'wm-d'],
    ['1', 'wm-1'], ['2', 'wm-2'], ['3', 'wm-3'], ['4', 'wm-4'],
  ]);
  const ATTITUDES = new Map([['hostile', 'Web.Hostile'], ['neutral', 'Web.Neutral'], ['friendly', 'Web.Friendly']]);
  const LINK_STATES = new Set(['offline', 'reconnecting', 'serverOnly', 'inSession']);
  const TABS = ['turn', 'players', 'field', 'rolls', 'pending', 'notes'];
  const OPS = ['turnStart', 'turnEnd', 'turnNextRound', 'turnSetActed', 'vitals', 'roll', 'rollRequest', 'admit', 'deny', 'announce'];

  // Le plugin abandonne une commande au bout de 2,5 s : ce que la page croit perdue ne s'exécute pas après.
  const ANSWER_MS = 5000;
  const SETTLE_MS = 1500;
  // Sous le budget de 6 par seconde du plugin, partagé par toutes les pages.
  const RATE_PER_SECOND = 5;
  const MERGE_MS = 300;
  const PRIMARY_INERT_MS = 600;
  const CONTROL_INERT_MS = 400;
  const NOTICE_MS = 6000;
  const NOTICES_MAX = 3;
  const CONFIRM_MS = 6000;
  const ADMIT_HIDE_MS = 3000;
  const ANNOUNCE_MAX = 180;
  const ANNOUNCE_LINES = 4;
  const VALUE_MAX = 99999;
  const DENY_MAX = 8;
  // Les vitales additionnent leurs clics ; les autres gestes ne comptent qu'un clic à la fois.
  const REPEATABLE = new Set(['step', 'detail', 'detail-close']);
  // Caractères de contrôle de .NET (catégorie Cc), saut de ligne excepté.
  const CONTROL_CHARS = /[\u0000-\u0009\u000B-\u001F\u007F-\u009F]/g;

  const SCREENS = {
    connecting: { title: 'Web.ConnectingTitle', busy: true },
    unreachable: { title: 'Web.UnreachableTitle', text: 'Web.UnreachableText', busy: true },
    expired: { title: 'Web.ExpiredTitle', text: 'Web.ExpiredText' },
    none: { title: 'Web.NoneTitle', text: 'Web.NoneText' },
    revoked: { title: 'Web.RevokedTitle', text: 'Web.RevokedText' },
    ended: { title: 'Web.EndedTitle', text: 'Web.EndedText' },
    unloaded: { title: 'Web.UnloadedTitle', text: 'Web.UnloadedText' },
    // Le ticket refusé pour 4409 est consommé : réessayer ne servirait à rien.
    busy: { title: 'Web.BusyTitle', text: 'Web.BusyText' },
  };

  const $ = id => document.getElementById(id);

  function load(key) {
    try { return sessionStorage.getItem(key); } catch { return null; }
  }

  function save(key, value) {
    try { sessionStorage.setItem(key, value); } catch { /* sans stockage, seul le F5 perd la session */ }
  }

  function forget(key) {
    try { sessionStorage.removeItem(key); } catch { /* idem */ }
  }

  let strings = new Map();
  const t = key => strings.get(key) ?? FALLBACK.get(key) ?? key;
  const fmt = (key, ...args) =>
    t(key).replace(/\{(\d+)(?::[^}]*)?\}/g, (match, index) => (Number(index) < args.length ? String(args[Number(index)]) : match));

  let numberFormat = null;
  let timeFormat = null;

  function intl(make) {
    try { return make(document.documentElement.lang || 'fr'); } catch { return make('fr'); }
  }

  function decimal(value) {
    numberFormat ??= intl(lang => new Intl.NumberFormat(lang, { maximumFractionDigits: 1 }));
    return numberFormat.format(value);
  }

  function clock(ms) {
    timeFormat ??= intl(lang => new Intl.DateTimeFormat(lang, { hour: '2-digit', minute: '2-digit' }));
    return timeFormat.format(new Date(ms));
  }

  const signed = n => (n >= 0 ? '+' + n : String(n));
  const clamp = (x, lo, hi) => Math.min(hi, Math.max(lo, x));
  const percent = ratio => (ratio * 100).toFixed(2) + '%';

  function el(tag, cls, content) {
    const node = document.createElement(tag);
    if (cls) node.className = cls;
    if (content != null) node.textContent = String(content);
    return node;
  }

  function control(cls, action, text) {
    const node = el('button', cls, text);
    node.type = 'button';
    node.dataset.action = action;
    return node;
  }

  // N'écrit que ce qui change : ces nœuds sont mis à jour à chaque état.
  function setText(node, text) {
    if (node.textContent !== text) node.textContent = text;
  }

  function setAttr(node, name, value) {
    if (value == null || value === false) {
      if (node.hasAttribute(name)) node.removeAttribute(name);
    } else if (node.getAttribute(name) !== String(value)) {
      node.setAttribute(name, String(value));
    }
  }

  function setData(node, values) {
    for (const [key, value] of Object.entries(values)) {
      if (value == null) delete node.dataset[key];
      else if (node.dataset[key] !== String(value)) node.dataset[key] = String(value);
    }
  }

  function setBusy(node, busy, disabled) {
    setAttr(node, 'aria-busy', busy ? 'true' : null);
    setAttr(node, 'aria-disabled', busy || disabled ? 'true' : null);
  }

  const SVG = 'http://www.w3.org/2000/svg';
  const ICONS = {
    minus: [['path', { d: 'M6 12h12' }]],
    plus: [['path', { d: 'M12 6v12M6 12h12' }]],
    close: [['path', { d: 'M6.5 6.5l11 11M17.5 6.5l-11 11' }]],
    die: [
      ['rect', { x: 4, y: 4, width: 16, height: 16, rx: 3 }],
      ['circle', { cx: 9, cy: 9, r: 1.3, class: 'pip' }],
      ['circle', { cx: 12, cy: 12, r: 1.3, class: 'pip' }],
      ['circle', { cx: 15, cy: 15, r: 1.3, class: 'pip' }],
    ],
    megaphone: [['path', { d: 'M4 10v4h3l6 4V6l-6 4H4z' }], ['path', { d: 'M16.5 9.5a3.5 3.5 0 0 1 0 5M18.5 7a7 7 0 0 1 0 10' }]],
  };

  function icon(name) {
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

  // ── Lecture défensive des messages ──

  const num = x => (typeof x === 'number' && Number.isFinite(x) ? x : 0);
  const int = x => Math.trunc(num(x));
  const str = x => (typeof x === 'string' ? x : '');
  const opt = x => (typeof x === 'string' && x !== '' ? x : null);
  const list = x => (Array.isArray(x) ? x : []);
  const obj = x => (x && typeof x === 'object' && !Array.isArray(x) ? x : {});
  const yes = x => x === true;
  const attitude = x => (ATTITUDES.has(x) ? x : 'neutral');

  const readStats = x => list(x).map(obj).map(s => ({ id: str(s.id), name: str(s.name), mod: int(s.mod) }));
  const readCounters = x => list(x).map(obj).map(c => ({
    name: str(c.name),
    value: int(c.value),
    max: int(c.max),
    color: HEX_COLOR.test(str(c.color)) ? c.color : null,
  }));

  function readState(m) {
    const link = obj(m.link);
    const display = obj(m.display);
    const ambiance = obj(m.ambiance);
    const primary = m.primary ? obj(m.primary) : null;
    const turn = m.turn ? obj(m.turn) : null;
    const rules = m.rules ? obj(m.rules) : null;

    return {
      seq: int(m.seq),
      access: ['gm', 'coGm', 'none'].includes(m.access) ? m.access : 'none',
      caps: list(m.caps).filter(x => OPS.includes(x)),
      rules: rules && { diceMax: int(rules.diceMax), lowerIsBetter: yes(rules.lowerIsBetter) },
      link: {
        state: LINK_STATES.has(link.state) ? link.state : 'offline',
        lobbyCode: opt(link.lobbyCode),
        onlinePlayers: int(link.onlinePlayers),
        suspended: yes(link.suspended),
        partyShown: int(link.partyShown),
        partyOnline: int(link.partyOnline),
      },
      display: {
        hpMode: display.hpMode === 'percent' ? 'percent' : 'points',
        mpMode: display.mpMode === 'percent' ? 'percent' : 'points',
        showMp: display.showMp !== false,
        showShield: display.showShield !== false,
        hpLabel: opt(display.hpLabel),
        mpLabel: opt(display.mpLabel),
        statTarget: yes(display.statTarget),
      },
      primary: primary && ['start', 'endTurn', 'nextRound'].includes(primary.kind)
        ? { kind: primary.kind, name: str(primary.name) }
        : null,
      party: list(m.party).map(obj).map(p => ({
        hash: str(p.hash),
        name: str(p.name),
        connected: yes(p.connected),
        isGm: yes(p.isGm),
        isCoGm: yes(p.isCoGm),
        hp: int(p.hp),
        hpMax: int(p.hpMax),
        mp: int(p.mp),
        mpMax: int(p.mpMax),
        shield: int(p.shield),
        stats: readStats(p.stats),
        counters: readCounters(p.counters),
        tempMod: int(p.tempMod),
        tempTurns: int(p.tempTurns),
        moveLeft: num(p.moveLeft),
        moveMax: num(p.moveMax),
        group: opt(p.group),
        awaitingRoll: yes(p.awaitingRoll),
      })),
      markers: list(m.markers).map(obj).filter(x => WAYMARKS.has(x.id)).map(x => ({
        id: x.id,
        name: str(x.name),
        attitude: attitude(x.attitude),
        boss: yes(x.boss),
        visible: yes(x.visible),
        hp: int(x.hp),
        hpMax: int(x.hpMax),
        mp: int(x.mp),
        mpMax: int(x.mpMax),
        shield: int(x.shield),
        stats: readStats(x.stats),
        counters: readCounters(x.counters),
        tempMod: int(x.tempMod),
        tempTurns: int(x.tempTurns),
      })),
      npcs: list(m.npcs).map(obj).map(x => ({
        id: str(x.id),
        name: str(x.name),
        creature: yes(x.creature),
        attitude: attitude(x.attitude),
        boss: yes(x.boss),
        hp: int(x.hp),
        hpMax: int(x.hpMax),
        shield: int(x.shield),
        stats: readStats(x.stats),
        counters: readCounters(x.counters),
        tempMod: int(x.tempMod),
      })),
      turn: turn && {
        active: yes(turn.active),
        encounter: int(turn.encounter),
        round: int(turn.round),
        activeIndex: Number.isInteger(turn.activeIndex) ? turn.activeIndex : -1,
        diceMax: int(turn.diceMax),
        entries: list(turn.entries).map(obj).map(e => ({
          name: str(e.name),
          kind: ['player', 'marker', 'npc'].includes(e.kind) ? e.kind : 'player',
          ref: opt(e.ref),
          initiative: int(e.initiative),
          roll: int(e.roll),
          mod: int(e.mod),
          stat: opt(e.stat),
          hasActed: yes(e.hasActed),
          groupId: opt(e.groupId),
          group: opt(e.group),
        })),
      },
      pending: list(m.pending).map(obj).map(g => ({
        label: opt(g.label),
        grouped: yes(g.grouped),
        members: list(g.members).map(obj).map(x => ({ name: str(x.name), hash: str(x.hash) })).filter(x => x.hash !== ''),
      })).filter(g => g.members.length > 0),
      rolls: list(m.rolls).slice(0, 20).map(obj).map(r => ({
        roller: str(r.roller),
        stat: opt(r.stat),
        raw: int(r.raw),
        mod: int(r.mod),
        total: int(r.total),
        diceMax: int(r.diceMax),
        target: typeof r.target === 'number' ? int(r.target) : null,
        success: typeof r.success === 'boolean' ? r.success : null,
        dice: Array.isArray(r.dice) ? r.dice.map(int) : null,
        at: num(r.at),
      })),
      ambiance: { weather: opt(ambiance.weather), time: opt(ambiance.time) },
    };
  }

  // ── Commandes ──

  let helloVersion = 0;
  const pending = new Map();
  const locks = new Map();
  const cooldowns = new Map();
  const outbox = [];
  const sentAt = [];
  let pumpTimer = 0;

  function can(op) {
    return helloVersion >= 2 && !lostAt && !final && !!current && current.access !== 'none'
      && !!current.link.lobbyCode && current.caps.includes(op);
  }

  const isBusy = key => locks.has(key);
  const isLocked = key => locks.has(key) || (cooldowns.get(key) ?? 0) > Date.now();

  function newId() {
    const bytes = new Uint8Array(12);
    crypto.getRandomValues(bytes);
    return btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  }

  // options : label (pour l'avis), lock (contrôle verrouillé jusqu'à l'état qui montre l'effet), value
  // (valeur demandée, affichée pendant l'attente), cooldown, done(ok, error) qui renvoie vrai s'il a
  // lui-même affiché l'échec.
  function command(op, fields, options = {}) {
    if (!can(op)) return false;
    const entry = { id: newId(), op, phase: 'queued', ackSeq: 0, timer: 0, ...options };
    pending.set(entry.id, entry);
    if (entry.lock) locks.set(entry.lock, entry);
    outbox.push({ entry, message: { type: 'cmd', id: entry.id, op, lobby: current.link.lobbyCode, seen: current.seq, ...fields } });
    pump();
    renderSoon();
    return true;
  }

  function pump() {
    clearTimeout(pumpTimer);
    while (outbox.length > 0) {
      const now = Date.now();
      while (sentAt.length > 0 && now - sentAt[0] >= 1000) sentAt.shift();
      if (sentAt.length >= RATE_PER_SECOND) {
        pumpTimer = setTimeout(pump, 1000 - (now - sentAt[0]) + 5);
        return;
      }
      const { entry, message } = outbox.shift();
      if (pending.get(entry.id) !== entry) continue;
      sentAt.push(now);
      entry.phase = 'sent';
      entry.timer = setTimeout(() => expire(entry), ANSWER_MS);
      send(message);
    }
  }

  function onAck(message) {
    const entry = pending.get(str(message.id));
    if (!entry || entry.phase !== 'sent') return;
    clearTimeout(entry.timer);
    if (message.ok !== true) {
      finish(entry, false, opt(message.error) ?? 'unknown');
      return;
    }
    // L'état de ce seq contient déjà l'effet : sans lui, le contrôle montrerait l'ancienne valeur.
    entry.ackSeq = int(message.seq);
    entry.phase = 'settling';
    if (current && current.seq >= entry.ackSeq) finish(entry, true, null);
    else entry.timer = setTimeout(() => finish(entry, true, null), SETTLE_MS);
  }

  function expire(entry) {
    if (entry.phase === 'settling') finish(entry, true, null);
    else finish(entry, false, 'noAnswer');
  }

  function settle(seq) {
    for (const entry of [...pending.values()])
      if (entry.phase === 'settling' && entry.ackSeq <= seq) finish(entry, true, null);
  }

  function finish(entry, ok, error) {
    if (pending.get(entry.id) !== entry) return;
    pending.delete(entry.id);
    clearTimeout(entry.timer);
    if (entry.lock && locks.get(entry.lock) === entry) {
      locks.delete(entry.lock);
      const inert = entry.cooldown ?? CONTROL_INERT_MS;
      cooldowns.set(entry.lock, Date.now() + inert);
      setTimeout(renderSoon, inert + 10);
    }
    const handled = entry.done?.(ok, error) === true;
    if (!ok && !handled) notify(entry.label, describe(entry.op, error), error === 'stale' ? 'info' : 'error');
    renderSoon();
  }

  function describe(op, error) {
    if (error === 'noAnswer') return t('Web.NoAnswer');
    if (error === 'gone' && (op === 'admit' || op === 'deny')) return t('Web.Err.goneRequest');
    const key = 'Web.Err.' + error;
    return strings.has(key) ? t(key) : t('Web.Err.unknown');
  }

  // Coupure, changement de lien, fin de session : rien n'est renvoyé de soi-même.
  function abandonCommands(lost) {
    const had = pending.size > 0 || [...vitals.values()].some(v => v.sent || v.set != null || v.delta !== 0);
    for (const entry of pending.values()) clearTimeout(entry.timer);
    pending.clear();
    locks.clear();
    outbox.length = 0;
    clearTimeout(pumpTimer);
    for (const v of vitals.values()) clearTimeout(v.timer);
    vitals.clear();
    for (const c of denyConfirms.values()) clearTimeout(c.timer);
    denyConfirms.clear();
    closeMenu(false);
    if (had && lost) notify(null, t('Web.LostCommand'), 'error');
  }

  function notify(label, text, tone) {
    const box = $('notices');
    const item = el('div', 'notice notice-' + tone);
    item.setAttribute('role', tone === 'error' ? 'alert' : 'status');
    const close = el('button', 'icon-btn notice-close');
    close.type = 'button';
    close.setAttribute('aria-label', t('Web.Close'));
    close.append(icon('close'));
    const body = el('span', 'notice-text');
    if (label) body.append(el('strong', 'notice-label', label));
    body.append(el('span', 'notice-body', text));
    item.append(body, close);
    box.append(item);
    while (box.childElementCount > NOTICES_MAX) box.firstElementChild.remove();
    const timer = setTimeout(() => item.remove(), NOTICE_MS);
    close.addEventListener('click', () => {
      clearTimeout(timer);
      item.remove();
    });
  }

  // ── Vitalité : les clics d'une même jauge s'additionnent, une seule commande en vol ──

  const vitals = new Map();

  function queueVital(kind, ref, field, change) {
    if (!can('vitals')) return;
    const key = kind + ':' + ref + ':' + field;
    let v = vitals.get(key);
    if (!v) {
      v = { key, kind, ref, field, delta: 0, set: null, sent: null, timer: 0 };
      vitals.set(key, v);
    }
    if ('set' in change) {
      v.set = change.set;
      v.delta = 0;
    } else {
      v.delta = clamp(v.delta + change.delta, -VALUE_MAX, VALUE_MAX);
    }
    if (!v.sent) {
      if ('set' in change) {
        clearTimeout(v.timer);
        flushVital(v);
      } else if (!v.timer) {
        v.timer = setTimeout(() => flushVital(v), MERGE_MS);
      }
    }
    renderSoon();
  }

  function flushVital(v) {
    v.timer = 0;
    let fields;
    if (v.set != null) {
      fields = { set: v.set };
      v.set = null;
    } else if (v.delta !== 0) {
      fields = { delta: v.delta };
      v.delta = 0;
    } else {
      vitals.delete(v.key);
      renderSoon();
      return;
    }
    v.sent = fields;
    const label = fmt('Web.VitalForFmt', vitalLabel(v.field, current.display), targetName(current, v.kind, v.ref));
    const sent = command('vitals', { kind: v.kind, ref: v.ref, field: v.field, ...fields }, {
      label,
      done: ok => {
        v.sent = null;
        if (!ok) {
          v.delta = 0;
          v.set = null;
        }
        if (vitals.get(v.key) !== v) return false;
        if (v.set != null || v.delta !== 0) flushVital(v);
        else vitals.delete(v.key);
        return false;
      },
    });
    if (!sent) vitals.delete(v.key);
  }

  // Ce qui part ou attend encore, à côté de la valeur de l'état : la page n'invente pas de valeur.
  function pendingVital(kind, ref, field) {
    const v = vitals.get(kind + ':' + ref + ':' + field);
    if (!v) return null;
    const base = v.set ?? v.sent?.set ?? null;
    if (base != null) return '→ ' + base + (v.delta ? ' ' + signed(v.delta) : '');
    const delta = (v.sent?.delta ?? 0) + v.delta;
    return delta ? signed(delta) : null;
  }

  function parseValue(text) {
    const match = /^\s*([+\-−]?)\s*(\d{1,5})\s*$/.exec(text);
    if (!match) return null;
    const n = Number(match[2]);
    if (match[1] === '') return { set: n };
    return { delta: match[1] === '+' ? n : -n };
  }

  // ── Rendu par lignes réutilisées ──

  // Une ligne n'est reconstruite que si ses données changent : à 4 états par seconde,
  // tout reconstruire ferait sauter le survol et la sélection de texte.
  let rowCache = new WeakMap();
  let sectionSigs = new Map();
  // Ligne qui vient d'apparaître ou de changer de place : un clic qui lui arrive visait sans doute une autre ligne.
  const movedAt = new WeakMap();

  // make(node, item) rend le nœud de la ligne : le même, mis à jour, ou un nouveau.
  function reconcile(container, items, keyOf, make, onNew) {
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

    const now = Date.now();
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

  const rebuild = build => (node, item) => build(item);

  function changed(name, value) {
    const sig = JSON.stringify(value);
    if (sectionSigs.get(name) === sig) return false;
    sectionSigs.set(name, sig);
    return true;
  }

  function forceRebuild() {
    rowCache = new WeakMap();
    sectionSigs = new Map();
  }

  // Les commandes d'une boîte gardent leurs nœuds d'un état à l'autre : un clic commencé sur un
  // bouton remplacé entre l'appui et le relâchement serait perdu.
  function syncControls(box, specs) {
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

  // Ligne à commandes : le corps est reconstruit quand il change, le nom et les commandes restent.
  // La case en tête ; le dé, - et + à droite de la jauge de PV, comme en jeu.
  const rowParts = new WeakMap();

  function patchRow(node, vm, body, controls) {
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
        pre: el('span', 'row-pre'),
        hp: el('div', 'row-hp'),
        hpMeter: el('span', 'row-hp-meter'),
        hpCtl: el('span', 'row-ctl'),
        rest: el('span', 'row-rest'),
        sig: null,
      };
      parts.line.append(parts.extra);
      parts.hp.append(parts.hpMeter, parts.hpCtl);
      parts.main.append(parts.line, parts.pre, parts.hp, parts.rest);
      node.append(parts.lead, parts.head, parts.main);
      rowParts.set(node, parts);
    }

    const sig = JSON.stringify(vm.body);
    if (sig !== parts.sig) {
      parts.sig = sig;
      const b = recycling(parts.head, () => body(vm.body));
      node.className = 'row' + (b.cls ? ' ' + b.cls : '');
      parts.head.replaceChildren(...b.head);
      setName(parts, b.name);
      parts.extra.replaceChildren(...b.extra);
      parts.pre.replaceChildren(...(b.pre ?? []));
      parts.hpMeter.replaceChildren(...(b.hp ? [b.hp] : []));
      parts.rest.replaceChildren(...b.rest);
    }
    const c = controls(vm.ctl);
    syncControls(parts.lead, c.lead);
    syncControls(parts.hpCtl, c.hp);
    parts.hp.hidden = !parts.hpMeter.firstChild && !parts.hpCtl.firstChild;
    return node;
  }

  function setName(parts, name) {
    const button = !!name.open;
    let node = parts.name;
    if (!node || (node.tagName === 'BUTTON') !== button) {
      const next = button ? control('name', 'detail') : el('span', 'name');
      if (node) node.replaceWith(next);
      else parts.line.insertBefore(next, parts.extra);
      parts.name = node = next;
    }
    node.className = 'name' + (button ? ' name-btn' : '') + (name.muted ? ' is-muted' : '');
    setText(node, name.text);
    if (button) {
      setData(node, { kind: name.open.kind, ref: name.open.ref });
      node.title = t('Web.OpenDetail');
    }
  }

  function pill(text, tone) {
    return el('span', 'pill pill-' + tone, text);
  }

  // Portraits UmbraSync par empreinte de joueur. rev change avec la data URI : les signatures des
  // lignes le portent à sa place.
  let portraits = new Map();
  let portraitSerial = 0;
  const imageUris = new WeakMap();
  let recycled = null;

  const portraitRev = hash => (hash ? portraits.get(hash)?.rev ?? 0 : 0);

  // Une balise neuve, même de la même data URI, peut rester vide le temps du décodage : ce qui se
  // reconstruit dans box reprend les images que box montrait déjà.
  function recycling(box, build) {
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

  function playerGlyph(group, hash) {
    const tone = group && GROUP_LABEL.test(group) ? 'g-' + group.toLowerCase() : 'g-other';
    const letter = group ? Array.from(group).slice(0, 2).join('') : null;
    const pic = hash ? portraits.get(hash) : null;
    if (pic) {
      const box = el('span', 'glyph glyph-avatar');
      box.append(portraitImage(pic.uri));
      if (letter) box.append(el('span', 'glyph-badge ' + tone, letter));
      return box;
    }
    if (letter) return el('span', 'glyph glyph-group ' + tone, letter);
    return el('span', 'glyph glyph-player');
  }

  function markerGlyph(id) {
    return el('span', 'glyph wm ' + (WAYMARKS.get(id) ?? 'wm-other'), id ? Array.from(id)[0] : '?');
  }

  function npcGlyph(creature) {
    return el('span', 'glyph ' + (creature ? 'glyph-creature' : 'glyph-npc'));
  }

  function hidden(node) {
    node.setAttribute('aria-hidden', 'true');
    return node;
  }

  const isNamed = m => m.name.trim() !== '';

  function vitalLabel(field, d) {
    if (field === 'mp') return d.mpLabel || t('Web.Mp');
    if (field === 'shield') return t('Marker.Shield');
    return d.hpLabel || t('Web.Hp');
  }

  // Les jauges portent le nom choisi par le modèle actif (« Éther », « Mana »), à défaut celui de la page.
  function meter(kind, tone, value, max, d, shield, change) {
    const isPercent = (kind === 'mp' ? d.mpMode : d.hpMode) === 'percent';
    const name = vitalLabel(kind, d);
    const cap = isPercent ? 100 : max > 0 ? max : 100;
    const ratio = clamp(value / cap, 0, 1);
    const label = isPercent ? value + '%' : value + '/' + max;
    const shieldText = '+' + shield + (isPercent ? '%' : '');

    const box = el('div', 'meter meter-' + kind);
    box.setAttribute('role', 'img');
    box.title = name + ' ' + label + (shield > 0 ? ', ' + fmt('Web.ShieldFmt', shieldText) : '');
    box.setAttribute('aria-label', box.title);

    const track = el('span', 'meter-track');
    const fill = el('span', 'meter-fill tone-' + tone);
    fill.style.width = percent(ratio);
    track.append(fill);

    if (shield > 0) {
      const extra = el('span', 'meter-shield');
      extra.style.left = percent(ratio);
      extra.style.width = percent(clamp(shield / cap, 0, 1 - ratio));
      track.append(extra);
    }

    box.append(track, el('span', 'meter-text', label));
    if (shield > 0) {
      const extraText = el('span', 'meter-shield-text', shieldText);
      extraText.title = fmt('Web.ShieldFmt', shieldText);
      box.append(extraText);
    }
    if (change) box.append(el('span', 'meter-pending', change));
    return box;
  }

  function hasMp(display, max) {
    return display.showMp && (display.mpMode === 'percent' || max > 0);
  }

  // Un modèle à seuils affiche ses stats sans signe, comme StatDisplay.Stat dans le jeu.
  const statValue = (mod, d) => (d.statTarget ? String(mod) : signed(mod));

  function tagsFor(entity, priv, d) {
    const box = el('div', 'tags');

    if (!priv) {
      for (const stat of entity.stats)
        box.append(el('span', 'tag tag-stat', stat.name + ' ' + statValue(stat.mod, d)));

      for (const counter of entity.counters) {
        const tag = el('span', 'tag tag-counter');
        const dot = el('span', 'dot');
        if (counter.color) dot.style.setProperty('--c', counter.color);
        const amount = counter.max > 0 ? counter.value + '/' + counter.max : String(counter.value);
        tag.append(dot, document.createTextNode(counter.name + ' ' + amount));
        box.append(tag);
      }
    }

    if (entity.tempMod) {
      let label = fmt(entity.tempMod > 0 ? 'Web.TempBonusFmt' : 'Web.TempMalusFmt', signed(entity.tempMod));
      if (entity.tempTurns > 0)
        label += ' · ' + (entity.tempTurns === 1 ? t('Web.TempTurnsOne') : fmt('Web.TempTurnsFmt', entity.tempTurns));
      const tag = el('span', 'tag tag-temp', label);
      tag.title = t('Web.TempTitle');
      box.append(tag);
    }

    if (entity.moveMax > 0)
      box.append(el('span', 'tag tag-move', fmt('Web.MoveFmt', decimal(entity.moveLeft), decimal(entity.moveMax))));

    return box.childElementCount > 0 ? [box] : [];
  }

  function attitudeText(value) {
    return el('span', 'trail att-' + value, t(ATTITUDES.get(value)));
  }

  function connection(connected) {
    const box = el('span', 'trail conn ' + (connected ? 'is-on' : 'is-off'));
    box.append(el('span', 'dot'), document.createTextNode(t(connected ? 'Web.Connected' : 'Web.Disconnected')));
    return box;
  }

  function actedMark() {
    const mark = el('span', 'trail acted');
    mark.append(el('span', 'check'), document.createTextNode(t('Web.Acted')));
    return mark;
  }

  // ── Commandes des lignes ──

  function tickSpec(c) {
    return {
      k: 'tick',
      make: () => {
        const button = control('tick', 'acted');
        button.setAttribute('role', 'checkbox');
        return button;
      },
      update: button => {
        setAttr(button, 'aria-checked', c.value ? 'true' : 'false');
        setBusy(button, c.busy, false);
        const label = fmt('Web.ActedFor', c.name);
        setAttr(button, 'aria-label', label);
        button.title = label;
        setData(button, { group: c.group ?? null, kind: c.kind ?? null, ref: c.ref ?? null, name: c.name });
      },
    };
  }

  function stepSpecs(c) {
    if (!c) return [];
    const percentMode = c.pct;
    const down = c.field === 'hp' ? (percentMode ? 'Scene.HpDownPct' : 'Scene.HpDown') : (percentMode ? 'Web.StepDownPct' : 'Web.StepDown');
    const up = c.field === 'hp' ? (percentMode ? 'Scene.HpUpPct' : 'Scene.HpUp') : (percentMode ? 'Web.StepUpPct' : 'Web.StepUp');
    return [-1, 1].map(dir => ({
      k: (dir < 0 ? 'down-' : 'up-') + c.field,
      make: () => {
        const button = control('step', 'step');
        button.dataset.dir = String(dir);
        button.append(icon(dir < 0 ? 'minus' : 'plus'));
        return button;
      },
      update: button => {
        const tip = t(dir < 0 ? down : up);
        button.title = tip;
        setAttr(button, 'aria-label', fmt('Web.NoticeFmt', c.name, tip));
        setData(button, { kind: c.kind, ref: c.ref, field: c.field });
      },
    }));
  }

  function diceSpec(c) {
    return {
      k: 'roll',
      make: () => {
        const button = control('icon-btn dice', 'roll');
        button.append(icon('die'));
        return button;
      },
      update: button => {
        const tip = c.unnamed ? t('Marker.RollDisabled') : t('Turns.RollFor');
        button.title = tip;
        setAttr(button, 'aria-label', fmt('Web.NoticeFmt', c.name, tip));
        setBusy(button, c.busy, c.unnamed);
        setAttr(button, 'aria-haspopup', c.menu ? 'menu' : null);
        setData(button, { kind: c.kind, ref: c.ref });
      },
    };
  }

  const rowControls = c => ({
    lead: c.tick ? [tickSpec(c.tick)] : [],
    hp: [...(c.roll ? [diceSpec(c.roll)] : []), ...stepSpecs(c.hp)],
  });

  // Le nom ouvre le détail s'il y a quelque chose à y faire.
  function opens(kind) {
    return can('vitals') || (!privateHidden && (kind === 'player' ? can('rollRequest') : can('roll')));
  }

  function hpCtl(kind, ref, name, d) {
    return can('vitals') ? { kind, ref, field: 'hp', pct: d.hpMode === 'percent', name } : null;
  }

  // Le co-MJ ne reçoit pas les stats de ses marqueurs : ses jets partent toujours sans stat.
  function rollStats(entity) {
    return current?.access === 'gm' ? entity.stats.filter(s => s.id !== '') : [];
  }

  function rollCtl(kind, ref, entity, name) {
    if (!entity || !can('roll') || privateHidden || kind === 'player') return null;
    if (kind === 'npc' && current.access !== 'gm') return null;
    return {
      kind, ref, name,
      unnamed: kind === 'marker' && !isNamed(entity),
      busy: isBusy('roll:' + kind + ':' + ref),
      menu: rollStats(entity).length > 0,
    };
  }

  // ── Lignes ──

  function playerBody(b) {
    const p = b.p;
    return {
      cls: p.connected ? '' : 'is-dim',
      head: [hidden(playerGlyph(p.group, p.hash))],
      name: { text: p.name, muted: !p.connected, open: b.open },
      extra: [
        ...(p.isGm ? [pill(t('Web.Gm'), 'accent')] : p.isCoGm ? [pill(t('Web.CoGm'), 'accent')] : []),
        ...(p.awaitingRoll ? [Object.assign(pill(t('Web.AwaitingRoll'), 'pending'), { title: t('Web.AwaitingRollHint') })] : []),
        connection(p.connected),
      ],
      hp: meter('hp', 'player', p.hp, p.hpMax, b.d, b.d.showShield ? p.shield : 0, b.pend),
      rest: [
        ...(hasMp(b.d, p.mpMax) ? [meter('mp', 'mp', p.mp, p.mpMax, b.d, 0, null)] : []),
        ...tagsFor(p, b.priv, b.d),
      ],
    };
  }

  function markerBody(b) {
    const m = b.m;
    const named = isNamed(m);
    return {
      cls: m.visible ? '' : 'is-dim',
      head: [hidden(markerGlyph(m.id))],
      name: { text: named ? m.name : fmt('Web.MarkerFmt', m.id), muted: !named, open: b.open },
      extra: [
        ...(m.boss ? [pill(t('Web.Boss'), 'accent')] : []),
        ...(m.visible ? [] : [el('span', 'hint', t('Web.MarkerNotPlaced'))]),
        attitudeText(m.attitude),
      ],
      hp: meter('hp', m.attitude, m.hp, m.hpMax, b.d, b.d.showShield ? m.shield : 0, b.pend),
      rest: [
        ...(hasMp(b.d, m.mpMax) ? [meter('mp', 'mp', m.mp, m.mpMax, b.d, 0, null)] : []),
        ...tagsFor(m, b.priv, b.d),
      ],
    };
  }

  function npcBody(b) {
    const n = b.n;
    return {
      cls: '',
      head: [hidden(npcGlyph(n.creature))],
      name: { text: n.name, muted: false, open: b.open },
      extra: [
        el('span', 'hint', t(n.creature ? 'Web.Creature' : 'Web.Npc')),
        ...(n.boss ? [pill(t('Web.Boss'), 'accent')] : []),
        attitudeText(n.attitude),
      ],
      hp: meter('hp', n.attitude, n.hp, n.hpMax, b.d, b.d.showShield ? n.shield : 0, b.pend),
      rest: tagsFor({ ...n, tempTurns: 0, moveMax: 0 }, b.priv, b.d),
    };
  }

  function resolveEntry(state, entry) {
    if (entry.kind === 'player') {
      const p = state.party.find(x => x.hash === entry.ref);
      return p && { tone: 'player', group: p.group, hp: p.hp, hpMax: p.hpMax, shield: p.shield, boss: false };
    }
    if (entry.kind === 'marker') {
      const m = state.markers.find(x => x.id === entry.ref);
      return m && { tone: m.attitude, hp: m.hp, hpMax: m.hpMax, shield: m.shield, boss: m.boss };
    }
    const n = state.npcs.find(x => x.id === entry.ref);
    return n && { tone: n.attitude, creature: n.creature, hp: n.hp, hpMax: n.hpMax, shield: n.shield, boss: n.boss };
  }

  function initiativeDetail(entry) {
    if (entry.roll <= 0) return null;
    if (entry.stat) return fmt('Web.InitRollStatFmt', entry.roll, entry.stat, signed(entry.mod));
    if (entry.mod) return fmt('Web.InitRollModFmt', entry.roll, signed(entry.mod));
    return fmt('Web.InitRollFmt', entry.roll);
  }

  function turnEntryBody(b) {
    const e = b.e;
    const ent = b.ent;
    const glyph = e.kind === 'marker' ? markerGlyph(e.ref ?? '')
      : e.kind === 'npc' ? npcGlyph(ent?.creature === true)
        : playerGlyph(ent?.group ?? null, e.ref);
    const init = el('span', 'init', e.initiative);
    init.title = t('Web.Initiative');
    const detail = b.priv ? null : initiativeDetail(e);
    return {
      cls: ['turn-row', b.active && 'is-active', e.hasActed && 'is-acted', b.grouped && 'is-grouped'].filter(Boolean).join(' '),
      head: [init, hidden(glyph)],
      name: { text: e.name, muted: false, open: b.open },
      extra: [
        ...(ent?.boss ? [pill(t('Web.Boss'), 'accent')] : []),
        ...(b.active && !b.grouped ? [pill(t('Web.TheirTurn'), 'pending')] : []),
        ...(e.hasActed && !b.grouped && b.mark ? [actedMark()] : []),
      ],
      pre: detail ? [el('div', 'row-sub', detail)] : [],
      hp: ent ? meter('hp', ent.tone, ent.hp, ent.hpMax, b.d, b.d.showShield ? ent.shield : 0, b.pend) : null,
      rest: [],
    };
  }

  const groupParts = new WeakMap();

  function patchGroup(node, vm) {
    let parts = node ? groupParts.get(node) : null;
    if (!parts) {
      node = el('li', 'turn-group');
      parts = { lead: el('span', 'row-lead'), body: el('span', 'turn-group-body'), sig: null };
      node.append(parts.lead, parts.body);
      groupParts.set(node, parts);
    }
    const sig = JSON.stringify(vm.body);
    if (sig !== parts.sig) {
      parts.sig = sig;
      const b = vm.body;
      node.className = 'turn-group' + (b.active ? ' is-active' : '') + (b.acted ? ' is-acted' : '');
      parts.body.replaceChildren(el('span', 'turn-group-name', b.label),
        ...(b.active ? [pill(t('Web.TheirTurn'), 'pending')] : b.acted && b.mark ? [actedMark()] : []));
    }
    syncControls(parts.lead, vm.ctl ? [tickSpec(vm.ctl)] : []);
    return node;
  }

  function pendingTitle(g) {
    if (g.label) return fmt('Web.SubgroupFmt', g.label);
    return g.members[0].name;
  }

  function pendingHead(b) {
    const head = el('div', 'pending-head');
    if (b.label) {
      head.append(hidden(playerGlyph(b.label)), el('span', 'pending-label', fmt('Web.SubgroupFmt', b.label)));
    } else if (b.grouped) {
      // Sous-groupe sans lettre encore attribuée : présenté comme dans le jeu, par son premier membre.
      const others = b.members.length - 1;
      const first = b.members[0].name;
      head.append(el('span', 'pending-label', others === 0 ? first
        : others === 1 ? fmt('Web.PendingTeammatesOne', first) : fmt('Web.PendingTeammatesFmt', first, others)));
    } else {
      head.append(el('span', 'pending-label is-muted', t('Web.NoSubgroup')));
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

  function pendingSpecs(vm) {
    const c = vm.ctl;
    const button = (k, cls, action, text, label) => ({
      k,
      make: () => control(cls, action),
      update: node => {
        setText(node, text);
        setAttr(node, 'aria-label', label);
        setBusy(node, c.busy, false);
        setData(node, { ref: vm.rep });
      },
    });
    if (c.confirm) {
      return [
        { k: 'confirm', make: () => el('span', 'pending-confirm'), update: node => setText(node, c.confirm) },
        button('deny-confirm', 'btn btn-small btn-danger-soft', 'deny-confirm', t('Lobby.Deny'), fmt('Web.DenyFmt', c.title)),
        button('deny-cancel', 'btn btn-small', 'deny-cancel', t('Gm.Cancel'), null),
      ];
    }
    return [
      ...(c.admit ? [button('admit', 'btn btn-small btn-success', 'admit', t('Lobby.Admit'), fmt('Web.AdmitFmt', c.title))] : []),
      ...(c.deny ? [button('deny', 'btn btn-small btn-danger', 'deny', t('Lobby.Deny'), fmt('Web.DenyFmt', c.title))] : []),
    ];
  }

  function rollDetail(r) {
    const parts = [];
    if (r.stat) parts.push(r.stat);
    const count = r.dice && r.dice.length > 1 ? r.dice.length : 1;
    let base = count > 1 ? r.dice.join(' + ') : String(r.raw);
    // L'écart réel et non r.mod : en mode Seuil la stat déplace la cible, le total reste le dé.
    const offset = r.total - r.raw;
    if (offset) base += (offset > 0 ? ' + ' : ' - ') + Math.abs(offset);
    parts.push(base);
    // diceMax est le maximum de toute la formule : il ne se divise en faces que pour des dés identiques.
    if (r.diceMax > 0) parts.push(r.diceMax % count === 0 ? count + 'd' + r.diceMax / count : r.raw + '/' + r.diceMax);
    return parts.join(' · ');
  }

  function buildRoll(r) {
    const node = el('li', 'roll');
    const head = el('div', 'roll-head');
    head.append(el('span', 'roll-name', r.roller), el('span', 'roll-total', r.total));

    const sub = el('div', 'roll-sub');
    sub.append(el('span', 'roll-detail', rollDetail(r)));
    if (r.target != null && r.success != null) {
      sub.append(el('span', 'roll-verdict ' + (r.success ? 'is-ok' : 'is-ko'),
        fmt(r.success ? 'Web.RollSuccessFmt' : 'Web.RollFailureFmt', r.target)));
    }
    if (r.at > 0) sub.append(el('span', 'roll-time', clock(r.at)));

    node.append(head, sub);
    return node;
  }

  function flash(node) {
    node.classList.add('is-new');
  }

  // ── Recherche dans l'état ──

  function entityOf(s, kind, ref) {
    if (!s || ref == null) return null;
    if (kind === 'player') return s.party.find(p => p.hash === ref) ?? null;
    if (kind === 'marker') return s.markers.find(m => m.id === ref) ?? null;
    if (kind === 'npc') return s.npcs.find(n => n.id === ref) ?? null;
    return null;
  }

  function displayName(kind, entity, ref) {
    if (kind === 'marker' && !isNamed(entity)) return fmt('Web.MarkerFmt', ref);
    return entity.name;
  }

  function targetName(s, kind, ref) {
    const entity = entityOf(s, kind, ref);
    return entity ? displayName(kind, entity, ref) : ref;
  }

  // ── Sections ──

  function linkText(link) {
    switch (link.state) {
      case 'inSession':
        return link.onlinePlayers === 1 ? t('Web.LinkInSessionOne') : fmt('Web.LinkInSessionFmt', link.onlinePlayers);
      case 'serverOnly':
        return t('Web.LinkServerOnly');
      case 'reconnecting':
        return t('Web.LinkReconnecting');
      default:
        return t('Web.LinkOffline');
    }
  }

  function chip(cls, title) {
    const node = el('span', 'chip' + (cls ? ' ' + cls : ''));
    if (title) node.title = title;
    return node;
  }

  function turnProgress(turn) {
    const blocks = new Map();
    turn.entries.forEach((e, i) => {
      const key = e.groupId ? 'g:' + e.groupId : 'e:' + i;
      blocks.set(key, (blocks.get(key) ?? true) && e.hasActed);
    });
    let acted = 0;
    for (const done of blocks.values()) if (done) acted++;
    return { acted, total: blocks.size };
  }

  // Demandes masquées quelques secondes après une réponse : le plugin ne retire que le représentant,
  // les coéquipiers ne partent qu'avec la file renvoyée par le relais.
  const hiddenPending = new Map();
  const denyConfirms = new Map();

  function visiblePending(s) {
    const now = Date.now();
    return s.pending
      .map(g => ({ ...g, members: g.members.filter(m => !((hiddenPending.get(m.hash) ?? 0) > now)) }))
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

  function renderBar(s) {
    const combat = s.turn?.active ? { round: s.turn.round, ...turnProgress(s.turn) } : null;
    const partial = s.access === 'coGm';
    const vm = {
      link: s.link,
      // Comme la barre du jeu : pas de pastille d'ambiance pendant un combat.
      ambiance: partial || s.turn?.active ? null : s.ambiance,
      pending: privateHidden ? 0 : visiblePending(s).length,
      partial,
      readOnly: helloVersion >= 2 && s.access !== 'none' && s.caps.length === 0,
      combat,
    };
    if (!changed('bar', vm)) return;

    const items = [];
    if (combat) {
      items.push(el('span', 'bar-round', fmt('Web.RoundFmt', combat.round)));
      items.push(el('span', 'bar-progress', fmt('Web.ProgressFmt', combat.acted, combat.total)));
    }

    const linkState = s.link.suspended ? 'suspended' : s.link.state;
    const session = chip('chip-session', linkText(s.link));
    session.append(el('span', 'dot link-' + linkState));
    if (s.link.lobbyCode) {
      session.append(el('span', null, t('Web.Lobby')), el('span', 'code', s.link.lobbyCode),
        el('span', 'sep', '·'), el('span', null, fmt('Web.OnlineFmt', s.link.partyOnline, s.link.partyShown)));
    } else {
      session.append(el('span', null, linkText(s.link)));
    }
    items.push(session);

    if (s.link.suspended) {
      const suspended = chip('chip-warning', t('Web.LinkSuspendedHint'));
      suspended.textContent = t('Web.LinkSuspended');
      items.push(suspended);
    }

    // Comme l'onglet Scène : l'ambiance reste au MJ.
    if (vm.ambiance) {
      const ambiance = chip('chip-ambiance', t('Web.Ambiance'));
      const parts = [vm.ambiance.weather, vm.ambiance.time].filter(Boolean);
      ambiance.textContent = parts.length ? parts.join(' · ') : t('Web.AmbianceGame');
      items.push(ambiance);
    }

    if (vm.pending > 0) {
      const count = chip('chip-pending', t('Web.Pending'));
      count.textContent = vm.pending === 1 ? t('Web.RequestsOne') : fmt('Web.RequestsFmt', vm.pending);
      items.push(count);
    }

    if (vm.partial) {
      const chipPartial = chip('chip-quiet', t('Web.PartialViewHint'));
      chipPartial.textContent = t('Web.PartialView');
      items.push(chipPartial);
    }

    if (vm.readOnly) {
      const readOnly = chip('chip-quiet', t('Web.ReadOnlyHint'));
      readOnly.textContent = t('Web.ReadOnly');
      items.push(readOnly);
    }

    $('bar-state').replaceChildren(...items);
  }

  // L'action pleine de la barre, telle que l'état la décrit : op, champs, et une clé qui change avec
  // l'op ou la cible (Lancer, puis Finir le tour de X, puis Manche suivante).
  function primaryCommand(s) {
    const p = s.primary;
    if (!p) return null;
    if (p.kind === 'start') {
      return can('turnStart')
        ? { op: 'turnStart', fields: {}, key: 'start', ready: true, text: t('Web.PrimaryStart'), tip: t('Turns.StartTooltip') }
        : null;
    }
    const turn = s.turn;
    if (!turn) return null;
    const round = { encounter: turn.encounter, round: turn.round };
    if (p.kind === 'nextRound') {
      return can('turnNextRound')
        ? { op: 'turnNextRound', fields: round, key: 'next:' + turn.encounter + ':' + turn.round, ready: true,
          text: t('Web.PrimaryNextRound'), tip: t('Turns.NextRoundTooltip') }
        : null;
    }
    if (!can('turnEnd')) return null;
    const entry = turn.entries[turn.activeIndex];
    const target = !entry ? null : entry.groupId ? { group: entry.groupId } : entry.ref ? { kind: entry.kind, ref: entry.ref } : null;
    const name = p.name || t('Web.TurnGroupUnnamed');
    return {
      op: 'turnEnd',
      fields: { ...round, ...target },
      key: 'end:' + turn.encounter + ':' + turn.round + ':' + (target ? target.group ?? target.kind + ':' + target.ref : '?'),
      ready: !!target,
      text: fmt('Web.PrimaryEndTurnFmt', name),
      tip: t('Scene.EndTurnTooltip'),
    };
  }

  let primaryCmd = null;
  let primaryKey = null;
  let primaryChangedAt = 0;
  let primaryArmed = null;
  // Le bouton plein n'obéit au clavier que si l'on y est arrivé par Tab.
  let primaryKeyFocus = false;
  let tabAt = 0;

  function renderActions(s) {
    const cmd = primaryCommand(s);
    const key = cmd?.key ?? null;
    if (key !== primaryKey) {
      primaryKey = key;
      primaryChangedAt = Date.now();
      setTimeout(renderSoon, PRIMARY_INERT_MS + 10);
    }
    primaryCmd = cmd;

    const button = $('primary');
    button.hidden = !cmd;
    if (cmd) {
      setText(button, cmd.text);
      button.title = cmd.tip;
      setBusy(button, isBusy('primary'), !cmd.ready);
      button.classList.toggle('is-settling', Date.now() - primaryChangedAt < PRIMARY_INERT_MS || isLocked('primary'));
    }

    const announce = $('announce');
    announce.hidden = !can('announce');
    if (!announce.firstChild) announce.append(icon('megaphone'));
    announce.title = t('Gm.AnnounceTooltip');
    setAttr(announce, 'aria-label', t('Gm.AnnounceButton'));
  }

  function setCount(id, n) {
    const node = $(id);
    const value = n > 0 ? String(n) : '';
    if (node.textContent !== value) node.textContent = value;
  }

  function renderTurn(s) {
    const section = $('sec-turn');
    section.hidden = !s.turn;
    if (!s.turn) {
      reconcile($('list-turn'), [], null, null);
      return;
    }

    const turn = s.turn;
    $('turn-sub').textContent = turn.active ? fmt('Web.TurnSubtitleFmt', turn.diceMax) : t('Web.TurnIdle');

    const ticks = turn.active && can('turnSetActed');
    const active = turn.activeIndex >= 0 && turn.activeIndex < turn.entries.length ? turn.entries[turn.activeIndex] : null;
    const items = [];
    let previousGroup = null;
    turn.entries.forEach((e, i) => {
      if (e.groupId && e.groupId !== previousGroup) {
        let end = i;
        while (end < turn.entries.length && turn.entries[end].groupId === e.groupId) end++;
        const acted = turn.entries.slice(i, end).every(x => x.hasActed);
        const label = e.group ?? t('Web.TurnGroupUnnamed');
        const lock = 'acted:g:' + e.groupId;
        items.push({
          k: 'g', id: e.groupId,
          body: { label, active: !!active && active.groupId === e.groupId, acted, mark: !ticks },
          ctl: ticks ? { group: e.groupId, name: label, busy: isBusy(lock), value: isBusy(lock) ? locks.get(lock).value : acted } : null,
        });
      }
      previousGroup = e.groupId;

      const ent = resolveEntry(s, e) ?? null;
      const entity = entityOf(s, e.kind, e.ref);
      const tickable = ticks && !e.groupId && !!e.ref;
      const lock = 'acted:' + e.kind + ':' + e.ref;
      items.push({
        k: 'e', e,
        body: {
          e,
          active: !!active && (turn.activeIndex === i || (!!e.groupId && active.groupId === e.groupId)),
          grouped: !!e.groupId,
          ent,
          pic: e.kind === 'player' ? portraitRev(e.ref) : 0,
          d: s.display,
          priv: privateHidden,
          mark: !tickable,
          pend: ent ? pendingVital(e.kind, e.ref, 'hp') : null,
          open: entity && opens(e.kind) ? { kind: e.kind, ref: e.ref } : null,
        },
        ctl: {
          tick: tickable ? { kind: e.kind, ref: e.ref, name: e.name, busy: isBusy(lock), value: isBusy(lock) ? locks.get(lock).value : e.hasActed } : null,
          hp: ent && e.ref ? hpCtl(e.kind, e.ref, e.name, s.display) : null,
          roll: rollCtl(e.kind, e.ref, entity, e.name),
        },
      });
    });

    reconcile($('list-turn'), items,
      vm => (vm.k === 'g' ? 'g:' + vm.id : 'e:' + vm.e.kind + ':' + (vm.e.ref ?? vm.e.name)),
      (node, vm) => (vm.k === 'g' ? patchGroup(node, vm) : patchRow(node, vm, turnEntryBody, rowControls)));
  }

  // Le relais ne donne pas la liste au co-MJ : « Aucun joueur » serait faux.
  function renderPlayers(s) {
    $('sec-players').hidden = s.access === 'coGm';
    setCount('count-players', s.party.length);
    $('empty-players').hidden = s.party.length > 0;
    reconcile($('list-players'), s.party.map(p => ({
      hash: p.hash,
      body: {
        p, d: s.display, priv: privateHidden, pic: portraitRev(p.hash), pend: pendingVital('player', p.hash, 'hp'),
        open: opens('player') ? { kind: 'player', ref: p.hash } : null,
      },
      ctl: { hp: hpCtl('player', p.hash, p.name, s.display) },
    })), vm => 'p:' + vm.hash, (node, vm) => patchRow(node, vm, playerBody, rowControls));
  }

  function renderField(s) {
    const items = [
      ...s.markers.map(m => {
        const name = displayName('marker', m, m.id);
        return {
          k: 'm', id: m.id,
          body: { m, d: s.display, priv: privateHidden, pend: pendingVital('marker', m.id, 'hp'), open: opens('marker') ? { kind: 'marker', ref: m.id } : null },
          ctl: { hp: hpCtl('marker', m.id, name, s.display), roll: rollCtl('marker', m.id, m, name) },
        };
      }),
      ...s.npcs.map(n => ({
        k: 'n', id: n.id,
        body: { n, d: s.display, priv: privateHidden, pend: pendingVital('npc', n.id, 'hp'), open: opens('npc') ? { kind: 'npc', ref: n.id } : null },
        ctl: { hp: hpCtl('npc', n.id, n.name, s.display), roll: rollCtl('npc', n.id, n, n.name) },
      })),
    ];
    setCount('count-field', items.length);
    $('empty-field').hidden = items.length > 0;
    $('note-field').hidden = s.access !== 'coGm';
    reconcile($('list-field'), items, vm => vm.k + ':' + vm.id,
      (node, vm) => patchRow(node, vm, vm.k === 'm' ? markerBody : npcBody, rowControls));
  }

  // Masqué, le contenu privé quitte aussi le DOM : un partage d'écran ne montre rien par l'inspecteur.
  function renderPending(s) {
    const groups = privateHidden ? [] : visiblePending(s);
    $('sec-pending').hidden = groups.length === 0;
    setCount('count-pending', groups.length);
    const admit = can('admit');
    const deny = can('deny');
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
          busy: isBusy('pend:' + rep),
          confirm: !confirm ? null : g.members.length === 1
            ? fmt('Web.DenyConfirmOne', g.members[0].name)
            : fmt('Web.DenyConfirmFmt', g.members.length),
        },
      };
    }), vm => 'q:' + vm.rep, patchPending);
  }

  function renderRolls(s) {
    $('sec-rolls').hidden = privateHidden;
    const rolls = privateHidden ? [] : s.rolls;
    setCount('count-rolls', rolls.length);
    $('empty-rolls').hidden = privateHidden || rolls.length > 0;
    reconcile($('list-rolls'), rolls, r => r.at + '|' + r.roller + '|' + r.raw + '|' + r.total, rebuild(buildRoll), flash);
  }

  function renderNotes() {
    $('sec-notes').hidden = privateHidden;
    const body = $('notes-text');
    // Le texte reste en mémoire sans accès : le plugin ne renvoie les notes que lorsqu'elles changent.
    const value = privateHidden || current?.access === 'none' ? '' : notesText;
    if (body.textContent !== value) body.textContent = value;
    const empty = value.trim() === '';
    body.hidden = empty;
    $('empty-notes').hidden = privateHidden || !empty;
  }

  // ── Détail : jauges, jet, demande de jet ──

  let detail = null;
  let detailShell = null;
  let detailOpener = null;
  const gaugeParts = new WeakMap();

  function buildDetailShell(box, key) {
    const head = el('header', 'detail-head');
    const glyph = el('span', 'detail-glyph');
    const titles = el('div', 'detail-titles');
    const name = el('h2', 'detail-name');
    name.id = 'detail-name';
    const kind = el('span', 'detail-kind');
    titles.append(name, kind);
    const close = control('icon-btn', 'detail-close');
    close.append(icon('close'));
    close.title = t('Scene.CloseDetail');
    close.setAttribute('aria-label', t('Scene.CloseDetail'));
    head.append(glyph, titles, close);

    const gone = el('p', 'detail-gone', t('Web.Err.gone'));
    const gauges = el('div', 'detail-gauges');
    const roll = el('div', 'detail-roll');
    const rollButtons = el('div', 'detail-roll-buttons');
    roll.append(el('span', 'detail-label', t('Web.Roll')), rollButtons);
    const ask = el('div', 'detail-ask');
    const askButton = control('btn btn-tint', 'ask', t('Group.AskRoll'));
    askButton.setAttribute('aria-haspopup', 'dialog');
    ask.append(askButton);

    box.setAttribute('aria-label', t('Web.Detail'));
    box.replaceChildren(head, gone, gauges, roll, ask);
    return { key, glyph, glyphSig: null, name, kind, close, gone, gauges, roll, rollButtons, ask, askButton, lastName: '' };
  }

  function detailGlyph(kind, entity, ref) {
    if (kind === 'player') return playerGlyph(entity.group, ref);
    if (kind === 'marker') return markerGlyph(ref);
    return npcGlyph(entity.creature);
  }

  function detailKind(kind, entity) {
    if (kind === 'npc') return t(entity.creature ? 'Web.Creature' : 'Web.Npc');
    if (kind === 'marker') return t(ATTITUDES.get(entity.attitude));
    return t(entity.connected ? 'Web.Connected' : 'Web.Disconnected');
  }

  function renderDetail(s) {
    const box = $('detail');
    // Le co-MJ ne voit que les marqueurs : un joueur ou un PNJ ne lui est pas « parti », il lui est caché.
    if (detail && (s.access === 'none' || (s.access === 'coGm' && detail.kind !== 'marker'))) detail = null;
    const open = !!detail;
    box.hidden = !open;
    $('app').classList.toggle('has-detail', open);
    document.body.classList.toggle('has-sheet', open);
    if (!open) {
      if (detailShell) {
        box.replaceChildren();
        detailShell = null;
      }
      return;
    }

    const { kind, ref } = detail;
    const key = kind + ':' + ref;
    if (!detailShell || detailShell.key !== key) detailShell = buildDetailShell(box, key);
    const sh = detailShell;
    const entity = entityOf(s, kind, ref);
    sh.gone.hidden = !!entity;
    if (entity) {
      sh.lastName = displayName(kind, entity, ref);
      const glyphSig = JSON.stringify([kind, entity.group, entity.creature, kind === 'player' ? portraitRev(ref) : 0]);
      if (glyphSig !== sh.glyphSig) {
        sh.glyphSig = glyphSig;
        sh.glyph.replaceChildren(hidden(recycling(sh.glyph, () => detailGlyph(kind, entity, ref))));
      }
      setText(sh.kind, detailKind(kind, entity));
    }
    setText(sh.name, sh.lastName);
    syncControls(sh.gauges, entity ? gaugeSpecs(s, kind, ref, entity) : []);

    const roll = rollCtl(kind, ref, entity, sh.lastName);
    sh.roll.hidden = !roll;
    if (roll) {
      const stats = rollStats(entity);
      const choices = stats.length > 0
        ? [{ id: '', text: t('Dice.NoStat') }, ...stats.map(st => ({ id: st.id, text: st.name + ' ' + statValue(st.mod, s.display) }))]
        : [{ id: '', text: t('Marker.Roll') }];
      syncControls(sh.rollButtons, choices.map(choice => ({
        k: 's:' + choice.id,
        make: () => control('btn btn-small', 'roll-stat'),
        update: button => {
          setText(button, choice.text);
          setData(button, { kind, ref, stat: choice.id });
          setBusy(button, roll.busy, roll.unnamed);
          button.title = roll.unnamed ? t('Marker.RollDisabled') : '';
        },
      })));
    }

    const askable = !!entity && kind === 'player' && can('rollRequest') && !privateHidden && !entity.isGm;
    sh.ask.hidden = !askable;
    if (askable) {
      setData(sh.askButton, { ref });
      setAttr(sh.askButton, 'aria-disabled', entity.connected ? null : 'true');
      sh.askButton.title = entity.connected ? '' : t('Web.Err.targetOffline');
    }
  }

  // Bouclier, PV puis PM, dans l'ordre de l'éditeur de vitales du jeu.
  function gaugeSpecs(s, kind, ref, entity) {
    const d = s.display;
    const hpPct = d.hpMode === 'percent';
    const gauges = [];
    if (d.showShield) gauges.push({ field: 'shield', value: entity.shield, max: entity.hpMax, pct: hpPct });
    gauges.push({ field: 'hp', value: entity.hp, max: entity.hpMax, pct: hpPct });
    if (kind !== 'npc' && hasMp(d, entity.mpMax)) gauges.push({ field: 'mp', value: entity.mp, max: entity.mpMax, pct: d.mpMode === 'percent' });
    const name = displayName(kind, entity, ref);
    return gauges.map(g => ({ k: g.field, make: makeGauge, update: row => updateGauge(row, kind, ref, name, g, d) }));
  }

  function makeGauge() {
    const row = el('div', 'gauge');
    const label = el('span', 'gauge-label');
    const box = el('span', 'gauge-ctl');
    const down = control('step', 'step');
    down.dataset.dir = '-1';
    down.append(icon('minus'));
    const input = el('input', 'gauge-input');
    input.type = 'text';
    input.autocomplete = 'off';
    input.spellcheck = false;
    input.maxLength = 8;
    input.setAttribute('enterkeyhint', 'done');
    const up = control('step', 'step');
    up.dataset.dir = '1';
    up.append(icon('plus'));
    box.append(down, input, up);
    const meta = el('span', 'gauge-meta');
    const change = el('span', 'gauge-pending');
    const hint = el('p', 'gauge-hint', t('Web.ValueHint'));
    hint.hidden = true;
    row.append(label, box, meta, change, hint);
    gaugeParts.set(row, { label, down, input, up, meta, change, hint });
    return row;
  }

  function updateGauge(row, kind, ref, name, g, d) {
    const p = gaugeParts.get(row);
    const label = vitalLabel(g.field, d);
    setText(p.label, label);
    const editable = can('vitals');
    p.down.hidden = !editable;
    p.up.hidden = !editable;
    p.input.readOnly = !editable;
    const target = { kind, ref, field: g.field };
    setData(p.down, target);
    setData(p.up, target);
    setData(p.input, target);
    const step = stepSpecs({ kind, ref, field: g.field, pct: g.pct, name });
    step[0].update(p.down);
    step[1].update(p.up);
    p.hint.id = 'gauge-hint-' + g.field;
    setAttr(p.input, 'aria-describedby', p.hint.id);
    setAttr(p.input, 'aria-label', fmt('Web.VitalForFmt', label, name));
    // Le champ en cours de saisie n'est jamais réécrit par un état entrant.
    if (document.activeElement !== p.input) {
      if (p.input.value !== String(g.value)) p.input.value = String(g.value);
      clearInvalid(p);
    }
    setText(p.meta, g.pct ? '%' : '/ ' + g.max);
    setText(p.change, pendingVital(kind, ref, g.field) ?? '');
  }

  function clearInvalid(p) {
    p.input.classList.remove('is-invalid');
    p.input.removeAttribute('aria-invalid');
    p.hint.hidden = true;
  }

  function restoreGauge(input) {
    const p = gaugeParts.get(input.closest('.gauge'));
    const entity = entityOf(current, input.dataset.kind, input.dataset.ref);
    if (p && entity) input.value = String(entity[input.dataset.field] ?? '');
    if (p) clearInvalid(p);
  }

  function submitGauge(input) {
    const p = gaugeParts.get(input.closest('.gauge'));
    if (!p || input.readOnly) return;
    const change = parseValue(input.value);
    if (!change) {
      input.classList.add('is-invalid');
      input.setAttribute('aria-invalid', 'true');
      p.hint.hidden = false;
      return;
    }
    clearInvalid(p);
    if (!('delta' in change) || change.delta !== 0) queueVital(input.dataset.kind, input.dataset.ref, input.dataset.field, change);
    input.blur();
  }

  function openDetail(button) {
    const { kind, ref } = button.dataset;
    closeMenu(false);
    detail = { kind, ref };
    detailOpener = button;
    render();
    const box = $('detail');
    if (box.scrollIntoView && box.getBoundingClientRect().top < 0) box.scrollIntoView({ block: 'nearest' });
    detailShell?.close.focus({ preventScroll: true });
  }

  function closeDetail() {
    detail = null;
    render();
    if (detailOpener?.isConnected) detailOpener.focus({ preventScroll: true });
    detailOpener = null;
  }

  // ── Menu des stats d'un jet ──

  let menuFor = null;

  function openMenu(anchor, kind, ref) {
    const entity = entityOf(current, kind, ref);
    if (!entity) return;
    const menu = $('menu');
    const choices = [{ id: '', text: t('Dice.NoStat') },
      ...rollStats(entity).map(st => ({ id: st.id, text: st.name + ' ' + statValue(st.mod, current.display) }))];
    menu.replaceChildren(el('div', 'menu-title', t('Dice.SelectStat')), ...choices.map(choice => {
      const item = control('menu-item', 'roll-stat', choice.text);
      item.setAttribute('role', 'menuitem');
      setData(item, { kind, ref, stat: choice.id });
      return item;
    }));
    menu.setAttribute('aria-label', fmt('Web.RollForFmt', targetName(current, kind, ref)));
    menu.hidden = false;
    menuFor = { kind, ref, anchor };
    anchor.setAttribute('aria-expanded', 'true');
    placeMenu();
    menu.querySelector('.menu-item')?.focus({ preventScroll: true });
  }

  function placeMenu() {
    if (!menuFor) return;
    const menu = $('menu');
    const rect = menuFor.anchor.getBoundingClientRect();
    const width = menu.offsetWidth;
    const height = menu.offsetHeight;
    const left = clamp(rect.right - width, 8, window.innerWidth - width - 8);
    const below = rect.bottom + 4;
    const top = below + height > window.innerHeight - 8 ? Math.max(8, rect.top - 4 - height) : below;
    menu.style.left = left + 'px';
    menu.style.top = top + 'px';
  }

  function closeMenu(restoreFocus) {
    if (!menuFor) return;
    const anchor = menuFor.anchor;
    menuFor = null;
    $('menu').hidden = true;
    $('menu').replaceChildren();
    anchor.removeAttribute('aria-expanded');
    if (restoreFocus && anchor.isConnected) anchor.focus({ preventScroll: true });
  }

  function renderMenu(s) {
    if (!menuFor) return;
    const roll = rollCtl(menuFor.kind, menuFor.ref, entityOf(s, menuFor.kind, menuFor.ref), '');
    if (!roll || roll.busy || roll.unnamed || !menuFor.anchor.isConnected) closeMenu(false);
  }

  // ── Feuilles : annonce et demande de jet ──

  let announceDraft = '';
  let announceError = '';

  const normalizeAnnounce = text => text.replace(/\r\n?/g, '\n').replace(CONTROL_CHARS, ' ').trim();

  function openAnnounce() {
    if (!can('announce')) return;
    closeMenu(false);
    const text = $('announce-text');
    text.value = announceDraft;
    announceError = '';
    updateAnnounce();
    $('dlg-announce').showModal();
    text.focus();
  }

  // Motif affiché dans une feuille ouverte quand l'action n'est plus permise.
  function missingRight(op) {
    if (can(op) || lostAt || final || !current) return '';
    return current.access === 'gm' && current.caps.length === 0 ? t('Web.Err.offline') : t('Web.Err.forbidden');
  }

  function updateAnnounce() {
    const dialog = $('dlg-announce');
    if (current && current.access !== 'gm') {
      if (dialog.open) dialog.close();
      return;
    }
    const area = $('announce-text');
    const count = area.value.length;
    const counter = $('announce-count');
    setText(counter, fmt('Web.CounterFmt', count, ANNOUNCE_MAX));
    counter.classList.toggle('is-near', count >= ANNOUNCE_MAX * 0.8 && count < ANNOUNCE_MAX);
    counter.classList.toggle('is-full', count >= ANNOUNCE_MAX);
    const text = normalizeAnnounce(area.value);
    const tooManyLines = text.split('\n').length > ANNOUNCE_LINES;
    setText($('announce-error'), tooManyLines ? t('Web.Err.tooManyLines') : announceError || missingRight('announce'));
    const busy = isBusy('announce');
    setBusy($('announce-send'), busy, text === '' || tooManyLines || !can('announce'));
  }

  function sendAnnounce(event) {
    if (event.detail > 1) return;
    const send = $('announce-send');
    if (send.getAttribute('aria-disabled') === 'true' || isLocked('announce')) return;
    const text = normalizeAnnounce($('announce-text').value);
    if (text === '' || text.split('\n').length > ANNOUNCE_LINES) return;
    announceError = '';
    command('announce', { text }, {
      label: t('Gm.AnnounceButton'),
      lock: 'announce',
      done: (ok, error) => {
        const dialog = $('dlg-announce');
        if (ok) {
          announceDraft = '';
          $('announce-text').value = '';
          if (dialog.open) dialog.close();
          return true;
        }
        if (!dialog.open) return false;
        announceError = describe('announce', error);
        updateAnnounce();
        return true;
      },
    });
  }

  let ask = null;
  const askStat = new Map();
  let askThreshold = Number.parseInt(load(KEY_THRESHOLD) ?? '', 10);

  function openAsk(button) {
    const player = current?.party.find(p => p.hash === button.dataset.ref);
    if (!player || !can('rollRequest')) return;
    closeMenu(false);
    ask = { hash: player.hash, error: '' };
    // Comme la fenêtre du jeu : la stat choisie reste celle du joueur, le seuil vaut pour tout l'onglet.
    if (!askStat.has(player.hash)) askStat.set(player.hash, player.stats.find(st => st.id !== '')?.id ?? '');
    if (!Number.isInteger(askThreshold)) askThreshold = Math.max(1, Math.trunc((current.rules?.diceMax ?? 0) / 2));

    const select = $('ask-stat');
    const options = [new Option(t('Dice.NoStat'), '')];
    for (const st of player.stats) {
      if (st.id !== '') options.push(new Option(st.name + ' (' + statValue(st.mod, current.display) + ')', st.id));
    }
    select.replaceChildren(...options);
    select.value = askStat.get(player.hash);
    if (select.value !== askStat.get(player.hash)) select.value = '';
    $('ask-threshold').value = String(current.display.statTarget ? prefill(player, select.value) ?? askThreshold : askThreshold);
    setText($('ask-for'), fmt('RollRequest.For', player.name));
    const hint = current.display.statTarget ? 'RollRequest.HintTarget' : current.rules?.lowerIsBetter ? 'RollRequest.HintLower' : 'RollRequest.HintHigher';
    setText($('ask-hint'), t(hint));
    updateAsk(current);
    $('dlg-ask').showModal();
    select.focus();
  }

  // En mode à seuils, le seuil part de la stat et du bonus temporaire du joueur.
  function prefill(player, statId) {
    const st = player.stats.find(x => x.id === statId && statId !== '');
    return st ? st.mod + player.tempMod : null;
  }

  function updateAsk(s) {
    const dialog = $('dlg-ask');
    if (!ask) return;
    const player = s.party.find(p => p.hash === ask.hash);
    if (!player || privateHidden || s.access !== 'gm') {
      if (dialog.open) dialog.close();
      return;
    }
    const error = !player.connected ? t('Web.Err.targetOffline') : ask.error || missingRight('rollRequest');
    setText($('ask-error'), error);
    setBusy($('ask-send'), isBusy('ask'), !player.connected || !can('rollRequest'));
  }

  function sendAsk(event) {
    if (event.detail > 1 || !ask) return;
    const send = $('ask-send');
    if (send.getAttribute('aria-disabled') === 'true' || isLocked('ask')) return;
    const player = current?.party.find(p => p.hash === ask.hash);
    if (!player) return;
    const input = $('ask-threshold');
    const threshold = Number(input.value);
    if (input.value.trim() === '' || !Number.isInteger(threshold) || Math.abs(threshold) > VALUE_MAX) {
      ask.error = t('Web.ThresholdHint');
      input.setAttribute('aria-invalid', 'true');
      updateAsk(current);
      input.focus();
      return;
    }
    input.removeAttribute('aria-invalid');
    askThreshold = threshold;
    save(KEY_THRESHOLD, String(threshold));
    const stat = $('ask-stat').value || null;
    const hash = player.hash;
    ask.error = '';
    command('rollRequest', { player: hash, stat, threshold }, {
      label: fmt('Web.AskRollForFmt', player.name),
      lock: 'ask',
      done: (ok, error) => {
        const dialog = $('dlg-ask');
        const here = dialog.open && ask?.hash === hash;
        if (ok) {
          if (here) dialog.close();
          return true;
        }
        if (!here) return false;
        ask.error = describe('rollRequest', error);
        updateAsk(current);
        return true;
      },
    });
  }

  function renderSheets(s) {
    if ($('dlg-announce').open) updateAnnounce();
    if ($('dlg-ask').open) updateAsk(s);
  }

  function closeSheets() {
    for (const id of ['dlg-announce', 'dlg-ask']) {
      const dialog = $(id);
      if (dialog.open) dialog.close();
    }
  }

  // ── Gestes ──

  function onPrimary(button, event) {
    const cmd = primaryCmd;
    const armed = primaryArmed;
    primaryArmed = null;
    if (!cmd || !cmd.ready) return;
    // Op et cible relevées à l'appui : si l'état a changé avant le relâchement, le clic visait autre chose.
    if (event.detail >= 1 && armed !== cmd.key) return;
    if (event.detail === 0 && !primaryKeyFocus) return;
    if (Date.now() - primaryChangedAt < PRIMARY_INERT_MS || isLocked('primary')) return;
    command(cmd.op, cmd.fields, { label: cmd.text, lock: 'primary', cooldown: PRIMARY_INERT_MS });
  }

  function onActed(button) {
    const turn = current?.turn;
    if (!turn) return;
    const { group, kind, ref, name } = button.dataset;
    const target = group ? { group } : { kind, ref };
    const lock = group ? 'acted:g:' + group : 'acted:' + kind + ':' + ref;
    if (isLocked(lock)) return;
    // La valeur montrée, et non celle de l'état : on coche ce qu'on voit.
    const value = button.getAttribute('aria-checked') !== 'true';
    command('turnSetActed', { encounter: turn.encounter, round: turn.round, ...target, value },
      { label: fmt('Web.ActedFor', name), lock, value });
  }

  function onStep(button, event) {
    const { kind, ref, field } = button.dataset;
    const step = (event.shiftKey ? 5 : 1) * (button.dataset.dir === '-1' ? -1 : 1);
    queueVital(kind, ref, field, { delta: step });
  }

  function sendRoll(kind, ref, stat) {
    const lock = 'roll:' + kind + ':' + ref;
    if (isLocked(lock)) return;
    command('roll', { kind, ref, stat: stat || null }, { label: fmt('Web.RollForFmt', targetName(current, kind, ref)), lock });
  }

  function onRoll(button) {
    const { kind, ref } = button.dataset;
    if (menuFor?.anchor === button) {
      closeMenu(true);
      return;
    }
    const entity = entityOf(current, kind, ref);
    if (!entity) return;
    if (rollStats(entity).length > 0) openMenu(button, kind, ref);
    else sendRoll(kind, ref, null);
  }

  function onRollStat(button) {
    const { kind, ref, stat } = button.dataset;
    closeMenu(button.closest('#menu') !== null);
    sendRoll(kind, ref, stat);
  }

  function findPending(rep) {
    return current ? visiblePending(current).find(g => g.members[0].hash === rep) ?? null : null;
  }

  function onAdmit(button) {
    const rep = button.dataset.ref;
    const group = findPending(rep);
    if (!group || isLocked('pend:' + rep)) return;
    const hashes = group.members.map(m => m.hash);
    command('admit', { hash: rep }, {
      label: fmt('Web.AdmitFmt', pendingTitle(group)),
      lock: 'pend:' + rep,
      done: ok => {
        if (ok) hidePending(hashes);
        return false;
      },
    });
  }

  // Refuser vise tout le sous-groupe : la page demande confirmation et n'envoie que les membres montrés.
  function onDeny(button) {
    const rep = button.dataset.ref;
    const group = findPending(rep);
    if (!group || isLocked('pend:' + rep)) return;
    const row = button.closest('.pending-group');
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

  function onDenyConfirm(button) {
    const rep = button.dataset.ref;
    const confirm = denyConfirms.get(rep);
    const group = findPending(rep);
    if (!confirm || !group || isLocked('pend:' + rep)) return;
    const row = button.closest('.pending-group');
    const hadFocus = row?.contains(document.activeElement) ?? false;
    clearTimeout(confirm.timer);
    denyConfirms.delete(rep);
    command('deny', { hash: rep, hashes: confirm.hashes.slice(0, DENY_MAX) }, {
      label: fmt('Web.DenyFmt', pendingTitle(group)),
      lock: 'pend:' + rep,
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

  function onDenyCancel(button) {
    const row = button.closest('.pending-group');
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

  const ACTIONS = {
    primary: onPrimary,
    announce: openAnnounce,
    acted: onActed,
    step: onStep,
    roll: onRoll,
    'roll-stat': onRollStat,
    detail: openDetail,
    'detail-close': closeDetail,
    ask: openAsk,
    admit: onAdmit,
    deny: onDeny,
    'deny-confirm': onDenyConfirm,
    'deny-cancel': onDenyCancel,
  };

  function onClick(event) {
    const button = event.target instanceof Element ? event.target.closest('[data-action]') : null;
    if (!button) return;
    const action = button.dataset.action;
    const handler = ACTIONS[action];
    // Un clic de souris ne garde pas le focus : Espace ou Entrée, tapés ensuite pour le jeu, referaient le geste.
    const pointer = event.detail >= 1;
    if (pointer) button.blur();
    if (!handler || button.getAttribute('aria-disabled') === 'true') return;
    // Un double clic ne fait jamais deux gestes : le second tomberait sur l'état suivant.
    if (event.detail > 1 && !REPEATABLE.has(action)) return;
    const row = button.closest('.row, .turn-group, .pending-group');
    if (row && action !== 'detail' && Date.now() - (movedAt.get(row) ?? 0) < CONTROL_INERT_MS) return;
    handler(button, event);
    if (pointer && document.activeElement === button) button.blur();
  }

  // ── Rendu ──

  let renderQueued = false;

  function renderSoon() {
    if (renderQueued) return;
    renderQueued = true;
    queueMicrotask(() => {
      renderQueued = false;
      render();
    });
  }

  let currentTab = TABS.includes(load(KEY_TAB)) ? load(KEY_TAB) : 'players';
  let wasCombat = false;

  function visibleTabs(s) {
    return {
      turn: !!s?.turn,
      players: s?.access !== 'coGm',
      field: true,
      rolls: !privateHidden,
      pending: !privateHidden && !!s && visiblePending(s).length > 0,
      notes: !privateHidden,
    };
  }

  function renderTabs(s) {
    const visible = visibleTabs(s);
    if (!visible[currentTab]) currentTab = visible.turn ? 'turn' : visible.players ? 'players' : 'field';

    for (const button of $('tabs').querySelectorAll('.tab')) {
      const name = button.dataset.tab;
      const selected = name === currentTab;
      button.hidden = !visible[name];
      button.setAttribute('aria-selected', String(selected));
      button.tabIndex = selected ? 0 : -1;
    }
    setCount('tab-count-pending', visible.pending ? visiblePending(s).length : 0);

    for (const section of document.querySelectorAll('.section'))
      section.classList.toggle('is-current', section.dataset.tab === currentTab);

    $('layout').classList.toggle('no-side', !visible.rolls && !visible.pending && !visible.notes && !detail);
  }

  function selectTab(name, focus) {
    if (!TABS.includes(name) || name === currentTab) return;
    currentTab = name;
    save(KEY_TAB, name);
    renderTabs(current);
    const button = $('tabs').querySelector('.tab[data-tab="' + name + '"]');
    if (focus) button?.focus();
    const layout = $('layout');
    if (layout.getBoundingClientRect().top < 0) layout.scrollIntoView({ block: 'start' });
  }

  function render() {
    if (!current) return;
    const combat = !!current.turn?.active;
    // Comme l'onglet Scène : le combat qui commence prend la place de la liste choisie.
    if (combat && !wasCombat) currentTab = 'turn';
    wasCombat = combat;

    renderBar(current);
    renderActions(current);
    renderTurn(current);
    renderPlayers(current);
    renderField(current);
    renderPending(current);
    renderRolls(current);
    renderNotes();
    renderDetail(current);
    renderTabs(current);
    renderPrivacy();
    renderMenu(current);
    renderSheets(current);
  }

  // ── Écrans d'état et textes fixes ──

  let screen = 'connecting';

  function showScreen(kind) {
    screen = kind;
    renderScreen();
  }

  function renderScreen() {
    const spec = screen ? SCREENS[screen] : null;
    $('screen').hidden = !spec;
    $('app').hidden = !!spec || !current;
    renderBanner();
    if (!spec) return;

    $('screen').classList.toggle('is-busy', !!spec.busy);
    $('screen-title').textContent = t(spec.title);
    const text = $('screen-text');
    text.hidden = !spec.text;
    text.textContent = spec.text ? t(spec.text) : '';
  }

  function renderBanner() {
    const banner = $('banner');
    const show = lostAt > 0 && !screen;
    banner.hidden = !show;
    if (show) banner.textContent = t('Web.Reconnecting');
  }

  function renderPrivacy() {
    const button = $('privacy');
    setText(button, t(privateHidden ? 'Web.ShowPrivate' : 'Web.HidePrivate'));
    button.title = t('Web.HidePrivateHint');
    button.setAttribute('aria-pressed', String(privateHidden));
  }

  function applyTexts() {
    document.title = t('Web.Title');
    for (const node of document.querySelectorAll('[data-t]')) node.textContent = t(node.dataset.t);
    for (const node of document.querySelectorAll('[data-t-label]')) node.setAttribute('aria-label', t(node.dataset.tLabel));
  }

  // ── Connexion ──

  function takeTicket() {
    if (!location.hash) return null;
    const value = new URLSearchParams(location.hash.slice(1)).get('t');
    history.replaceState(null, '', location.pathname + location.search);
    return value && CREDENTIAL.test(value) ? value : null;
  }

  let ticket = takeTicket();
  let session = null;
  if (ticket) {
    forget(KEY_SESSION);
  } else {
    const stored = load(KEY_SESSION);
    session = stored && CREDENTIAL.test(stored) ? stored : null;
  }

  // « 1 » ou « 0 » une fois choisi par le bouton de cet onglet ; sinon le hello donne le réglage du MJ.
  let privateChoice = load(KEY_PRIVATE);
  let privateHidden = privateChoice === '1';
  let current = null;
  let notesText = '';
  let socket = null;
  let final = false;
  let lostAt = 0;
  let retryTimer = 0;
  let unreachableTimer = 0;

  function connect() {
    clearTimeout(retryTimer);
    let ws;
    try {
      ws = new WebSocket('ws://' + location.host + '/ws');
    } catch {
      connectionLost();
      return;
    }
    socket = ws;

    const openTimer = setTimeout(() => {
      if (ws.readyState === WebSocket.CONNECTING) ws.close();
    }, OPEN_TIMEOUT_MS);

    ws.addEventListener('open', () => clearTimeout(openTimer));
    ws.addEventListener('message', event => {
      if (socket === ws) onMessage(event.data);
    });
    ws.addEventListener('close', event => {
      clearTimeout(openTimer);
      if (socket !== ws) return;
      socket = null;
      onClose(event.code);
    });
  }

  function send(message) {
    if (socket && socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify(message));
  }

  function closeSocket() {
    if (!socket) return;
    const ws = socket;
    socket = null;
    ws.close(1000);
  }

  function stop(kind) {
    final = true;
    clearTimeout(retryTimer);
    clearTimeout(unreachableTimer);
    lostAt = 0;
    abandonCommands(true);
    closeSheets();
    detail = null;
    portraits = new Map();
    showScreen(kind);
    closeSocket();
  }

  function dropCredentials() {
    ticket = null;
    session = null;
    forget(KEY_SESSION);
  }

  function onMessage(data) {
    if (typeof data !== 'string') return;
    let message;
    try { message = JSON.parse(data); } catch { return; }
    if (!message || typeof message !== 'object') return;

    switch (message.type) {
      case 'hello': onHello(message); break;
      case 'welcome': onWelcome(message); break;
      case 'ack': onAck(message); break;
      case 'state': onState(message); break;
      case 'notes': onNotes(message); break;
      case 'portraits': onPortraits(message); break;
      case 'bye': onBye(message); break;
    }
  }

  function onHello(message) {
    strings = new Map();
    for (const [key, value] of Object.entries(obj(message.strings)))
      if (typeof value === 'string') strings.set(key, value);
    helloVersion = int(message.v);
    if (typeof message.lang === 'string' && LANG.test(message.lang)) document.documentElement.lang = message.lang;
    numberFormat = null;
    timeFormat = null;
    if (privateChoice !== '1' && privateChoice !== '0') privateHidden = yes(message.privateByDefault);

    applyTexts();
    forceRebuild();
    detailShell = null;
    renderScreen();
    render();

    if (session) send({ type: 'auth', session });
    else if (ticket) send({ type: 'auth', ticket });
    else stop('expired');
  }

  function onWelcome(message) {
    if (typeof message.session !== 'string' || !CREDENTIAL.test(message.session)) return;
    session = message.session;
    ticket = null;
    save(KEY_SESSION, session);
    connectionRestored();
  }

  function connectionRestored() {
    if (!lostAt) return;
    lostAt = 0;
    clearTimeout(unreachableTimer);
    renderBanner();
  }

  function onState(message) {
    // Une reprise par session peut se passer de welcome : le premier état suffit à lever l'alerte.
    connectionRestored();
    current = readState(message);
    settle(current.seq);
    // Rendu même sans accès : l'état « none » arrive vide et purge ce qui restait affiché.
    render();
    if (current.access === 'none') showScreen('none');
    else if (screen) showScreen(null);
  }

  function onNotes(message) {
    notesText = str(message.text);
    if (current) renderNotes();
  }

  // Gardé même avant le premier état : le plugin ne renvoie la liste que lorsqu'elle change.
  function onPortraits(message) {
    const next = new Map();
    for (const [hash, uri] of Object.entries(obj(message.items))) {
      if (hash === '' || typeof uri !== 'string' || !PORTRAIT.test(uri)) continue;
      const known = portraits.get(hash);
      next.set(hash, known && known.uri === uri ? known : { uri, rev: ++portraitSerial });
    }
    portraits = next;
    renderSoon();
  }

  function onBye(message) {
    dropCredentials();
    stop(message.reason === 'unloaded' || message.reason === 'ended' ? message.reason : 'revoked');
  }

  function onClose(code) {
    if (final) return;

    if (code === 4401) {
      dropCredentials();
      stop('expired');
      return;
    }
    if (code === 4409) {
      stop('busy');
      return;
    }
    connectionLost();
  }

  function connectionLost() {
    if (!lostAt) {
      lostAt = Date.now();
      clearTimeout(unreachableTimer);
      unreachableTimer = setTimeout(() => {
        if (lostAt && !final) showScreen('unreachable');
      }, UNREACHABLE_MS);
    }
    abandonCommands(true);
    renderBanner();
    render();
    clearTimeout(retryTimer);
    retryTimer = setTimeout(connect, RETRY_MS);
  }

  // Lien rouvert depuis le jeu dans un onglet déjà ouvert : seul le fragment change, sans rechargement.
  window.addEventListener('hashchange', () => {
    const fresh = takeTicket();
    if (!fresh) return;
    abandonCommands(true);
    closeSheets();
    detail = null;
    ticket = fresh;
    session = null;
    forget(KEY_SESSION);
    final = false;
    lostAt = 0;
    clearTimeout(retryTimer);
    clearTimeout(unreachableTimer);
    closeSocket();
    current = null;
    notesText = '';
    portraits = new Map();
    showScreen('connecting');
    connect();
  });

  $('privacy').addEventListener('click', () => {
    privateHidden = !privateHidden;
    privateChoice = privateHidden ? '1' : '0';
    save(KEY_PRIVATE, privateChoice);
    closeMenu(false);
    forceRebuild();
    render();
  });

  $('tabs').addEventListener('click', event => {
    const button = event.target instanceof Element ? event.target.closest('.tab') : null;
    if (button) selectTab(button.dataset.tab, false);
  });

  $('tabs').addEventListener('keydown', event => {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
    const visible = Array.from($('tabs').querySelectorAll('.tab')).filter(b => !b.hidden).map(b => b.dataset.tab);
    const index = visible.indexOf(currentTab);
    if (index < 0) return;
    const step = event.key === 'ArrowRight' ? 1 : -1;
    selectTab(visible[(index + step + visible.length) % visible.length], true);
    event.preventDefault();
  });

  document.addEventListener('click', onClick);

  $('primary').addEventListener('pointerdown', event => {
    if (event.button === 0) primaryArmed = primaryCmd?.key ?? null;
  });
  document.addEventListener('keydown', event => {
    if (event.key === 'Tab') tabAt = Date.now();
  }, true);
  $('primary').addEventListener('focus', () => {
    primaryKeyFocus = Date.now() - tabAt < 1000;
  });
  $('primary').addEventListener('blur', () => {
    primaryKeyFocus = false;
  });

  // Fermeture du menu : clic à côté, Échap, défilement ou redimensionnement.
  document.addEventListener('pointerdown', event => {
    if (!menuFor || !(event.target instanceof Node)) return;
    if ($('menu').contains(event.target) || menuFor.anchor.contains(event.target)) return;
    closeMenu(false);
  }, true);
  window.addEventListener('resize', () => closeMenu(false));
  document.addEventListener('scroll', event => {
    if (menuFor && !(event.target instanceof Node && $('menu').contains(event.target))) closeMenu(false);
  }, true);

  $('menu').addEventListener('keydown', event => {
    const items = Array.from($('menu').querySelectorAll('.menu-item'));
    const index = items.indexOf(document.activeElement);
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      const step = event.key === 'ArrowDown' ? 1 : -1;
      items[(index + step + items.length) % items.length]?.focus();
      event.preventDefault();
    } else if (event.key === 'Tab') {
      closeMenu(false);
    }
  });

  document.addEventListener('keydown', event => {
    if (event.key !== 'Escape' || document.querySelector('dialog[open]')) return;
    if (menuFor) {
      closeMenu(true);
      event.preventDefault();
    } else if (detail && $('detail').contains(document.activeElement)) {
      closeDetail();
      event.preventDefault();
    }
  });

  $('detail').addEventListener('keydown', event => {
    const input = event.target instanceof Element ? event.target.closest('.gauge-input') : null;
    if (!input) return;
    if (event.key === 'Enter') {
      event.preventDefault();
      submitGauge(input);
    } else if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      restoreGauge(input);
      input.select();
    }
  });

  $('detail').addEventListener('focusout', event => {
    const input = event.target instanceof Element ? event.target.closest('.gauge-input') : null;
    if (input) restoreGauge(input);
  });

  $('announce-text').addEventListener('input', () => {
    announceDraft = $('announce-text').value;
    announceError = '';
    updateAnnounce();
  });
  $('announce-send').addEventListener('click', sendAnnounce);
  $('announce-cancel').addEventListener('click', () => $('dlg-announce').close());

  $('ask-stat').addEventListener('change', () => {
    if (!ask) return;
    const value = $('ask-stat').value;
    askStat.set(ask.hash, value);
    const player = current?.party.find(p => p.hash === ask.hash);
    const seeded = player && current.display.statTarget ? prefill(player, value) : null;
    if (seeded != null) $('ask-threshold').value = String(seeded);
  });
  $('ask-threshold').addEventListener('input', () => {
    if (!ask) return;
    ask.error = '';
    $('ask-threshold').removeAttribute('aria-invalid');
    updateAsk(current);
  });
  $('ask-threshold').addEventListener('keydown', event => {
    if (event.key === 'Enter') {
      event.preventDefault();
      sendAsk({ detail: 0 });
    }
  });
  $('ask-send').addEventListener('click', sendAsk);
  $('ask-cancel').addEventListener('click', () => $('dlg-ask').close());
  $('dlg-ask').addEventListener('close', () => {
    ask = null;
    setBusy($('ask-send'), false, false);
  });
  $('dlg-announce').addEventListener('close', () => setBusy($('announce-send'), false, false));

  applyTexts();
  renderPrivacy();
  renderScreen();
  connect();
})();
