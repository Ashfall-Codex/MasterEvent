'use strict';

(function () {
  // Écrans possibles avant le hello, donc avant de connaître les textes du plugin.
  const FALLBACK = new Map([
    ['PlayerWeb.Title', 'Vue joueur · MasterEvent'],
    ['PlayerWeb.ConnectingTitle', 'Connexion au jeu...'],
    ['PlayerWeb.UnreachableTitle', 'Le jeu ne répond pas'],
    ['PlayerWeb.UnreachableText', 'Nouvel essai toutes les 2 secondes. Le jeu et MasterEvent doivent rester lancés.'],
    ['PlayerWeb.ExpiredTitle', 'Lien expiré'],
    ['PlayerWeb.ExpiredText', 'Ce lien a expiré. Rouvrez-le depuis la vue joueur du jeu.'],
    ['PlayerWeb.BusyTitle', "Trop d'écrans ouverts"],
    ['PlayerWeb.BusyText', 'Fermez un onglet, puis rouvrez le lien depuis le jeu.'],
  ]);

  const RETRY_MS = 2000;
  const UNREACHABLE_MS = 10000;
  const OPEN_TIMEOUT_MS = 5000;
  const KEY_SESSION = 'masterevent.player.session';
  const KEY_TAB = 'masterevent.player.tab';
  const CREDENTIAL = /^[A-Za-z0-9._~+/=-]{8,512}$/;
  const HEX_COLOR = /^#[0-9a-fA-F]{6}$/;
  const LANG = /^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$/;
  const PORTRAIT = /^data:image\/(?:png|jpeg);base64,[A-Za-z0-9+/]+={0,2}$/;
  const WAYMARKS = new Map([
    ['A', 'wm-a'], ['B', 'wm-b'], ['C', 'wm-c'], ['D', 'wm-d'],
    ['1', 'wm-1'], ['2', 'wm-2'], ['3', 'wm-3'], ['4', 'wm-4'],
  ]);
  const ATTITUDES = new Map([['hostile', 'Attitude.Hostile'], ['neutral', 'Attitude.Neutral'], ['friendly', 'Attitude.Friendly']]);
  const LINK_STATES = new Set(['offline', 'reconnecting', 'serverOnly', 'inSession']);
  const VIEWS = new Set(['instance', 'versionRejected', 'joinRejected', 'awaitingApproval', 'offline', 'reconnecting',
    'outOfSession', 'joining', 'handover', 'gmAway', 'inSession']);
  const REJECTIONS = new Set(['denied', 'roomLimit', 'rateLimited', 'invalid']);
  const HOLDS = new Set(['none', 'sent', 'handover', 'gmAway']);
  const ENTRY_KINDS = new Set(['player', 'marker', 'npc', 'other']);
  const TABS = ['list', 'sheet'];
  const OPS = ['selfRoll', 'selfAnswer', 'selfEndTurn', 'selfDismiss'];

  // Les prédicats de PlayerWindow : message à la place des listes, salle montrée, salle où l'on répond.
  const STATE_VIEWS = new Set(['instance', 'versionRejected', 'joinRejected', 'awaitingApproval', 'offline', 'outOfSession', 'joining']);
  const SESSION_VIEWS = new Set(['inSession', 'handover', 'gmAway', 'reconnecting']);
  const ROOM_VIEWS = new Set(['inSession', 'handover', 'gmAway']);

  // Le plugin abandonne une commande au bout de 2,5 s : ce que la page croit perdue ne s'exécute pas après.
  const ANSWER_MS = 5000;
  const SETTLE_MS = 1500;
  // Sous le budget de 6 par seconde du plugin.
  const RATE_PER_SECOND = 5;
  const PRIMARY_INERT_MS = 600;
  const NOTICE_MS = 6000;
  const NOTICES_MAX = 3;
  const PAGE_NOTICES_MAX = 2;
  // VitalsControls.StatFilterThreshold : le champ de recherche paraît au-delà.
  const STAT_FILTER_THRESHOLD = 5;
  const NARROW = '(max-width: 899px)';

  const SCREENS = {
    connecting: { title: 'PlayerWeb.ConnectingTitle', busy: true },
    unreachable: { title: 'PlayerWeb.UnreachableTitle', text: 'PlayerWeb.UnreachableText', busy: true },
    expired: { title: 'PlayerWeb.ExpiredTitle', text: 'PlayerWeb.ExpiredText' },
    none: { title: 'PlayerWeb.NoneTitle', text: 'PlayerWeb.NoneText' },
    revoked: { title: 'PlayerWeb.RevokedTitle', text: 'PlayerWeb.RevokedText' },
    ended: { title: 'PlayerWeb.EndedTitle', text: 'PlayerWeb.EndedText' },
    unloaded: { title: 'PlayerWeb.UnloadedTitle', text: 'PlayerWeb.UnloadedText' },
    // Le ticket refusé pour 4409 est consommé : réessayer ne servirait à rien.
    busy: { title: 'PlayerWeb.BusyTitle', text: 'PlayerWeb.BusyText' },
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

  function setTitle(node, text) {
    if (text) {
      if (node.title !== text) node.title = text;
    } else if (node.hasAttribute('title')) {
      node.removeAttribute('title');
    }
  }

  const SVG = 'http://www.w3.org/2000/svg';
  const ICONS = {
    close: [['path', { d: 'M6.5 6.5l11 11M17.5 6.5l-11 11' }]],
    die: [
      ['rect', { x: 4, y: 4, width: 16, height: 16, rx: 3 }],
      ['circle', { cx: 9, cy: 9, r: 1.3, class: 'pip' }],
      ['circle', { cx: 12, cy: 12, r: 1.3, class: 'pip' }],
      ['circle', { cx: 15, cy: 15, r: 1.3, class: 'pip' }],
    ],
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
  const maybeInt = x => (typeof x === 'number' && Number.isFinite(x) ? Math.trunc(x) : null);

  const readStats = x => list(x).map(obj).map(s => ({ id: str(s.id), name: str(s.name), mod: int(s.mod) }));
  const readCounters = x => list(x).map(obj).map(c => ({
    name: str(c.name),
    value: int(c.value),
    max: int(c.max),
    color: HEX_COLOR.test(str(c.color)) ? c.color : null,
  }));

  function readRoll(r) {
    return {
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
    };
  }

  // Le dernier jet de Ma fiche sépare la stat du bonus temporaire, comme DrawSheetLastRoll.
  function readLastRoll(r) {
    return { ...readRoll(r), statMod: maybeInt(r.statMod), tempMod: maybeInt(r.tempMod) };
  }

  function readField(x) {
    const marker = x.kind === 'marker';
    return {
      kind: marker ? 'marker' : 'npc',
      ref: str(x.ref),
      name: str(x.name),
      creature: !marker && yes(x.creature),
      attitude: attitude(x.attitude),
      hp: int(x.hp),
      hpMax: int(x.hpMax),
      shield: int(x.shield),
      boss: yes(x.boss),
      mp: marker ? maybeInt(x.mp) : null,
      mpMax: marker ? maybeInt(x.mpMax) : null,
      counters: marker ? readCounters(x.counters) : [],
      tempModifier: marker ? int(x.tempModifier) : 0,
      tempRounds: marker ? int(x.tempRounds) : 0,
      lastRoll: marker ? int(x.lastRoll) : 0,
      lastRollMax: marker ? int(x.lastRollMax) : 0,
    };
  }

  function readState(m) {
    const link = obj(m.link);
    const display = obj(m.display);
    const detail = obj(m.viewDetail);
    const gm = m.gm ? obj(m.gm) : null;
    const primary = m.primary ? obj(m.primary) : null;
    const request = m.request ? obj(m.request) : null;
    const ambiance = m.ambiance ? obj(m.ambiance) : null;
    const me = m.me ? obj(m.me) : null;
    const turn = m.turn ? obj(m.turn) : null;

    return {
      seq: int(m.seq),
      access: m.access === 'player' ? 'player' : 'none',
      caps: list(m.caps).filter(x => OPS.includes(x)),
      room: opt(m.room),
      view: VIEWS.has(m.view) ? m.view : 'offline',
      combatPresent: yes(m.combatPresent),
      detail: {
        lobby: opt(detail.lobby),
        minVersion: opt(detail.minVersion),
        rejection: REJECTIONS.has(detail.rejection) ? detail.rejection : null,
        consent: detail.consent !== false,
      },
      link: {
        state: LINK_STATES.has(link.state) ? link.state : 'offline',
        lobbyCode: opt(link.lobbyCode),
        onlinePlayers: int(link.onlinePlayers),
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
      gm: gm && { name: opt(gm.name), connected: yes(gm.connected) },
      template: opt(m.template),
      lowerIsBetter: yes(m.lowerIsBetter),
      primary: primary && (primary.kind === 'roll' || primary.kind === 'endTurn')
        ? {
          kind: primary.kind,
          statName: opt(primary.statName),
          canEndTurn: yes(primary.canEndTurn),
          groupTurn: yes(primary.groupTurn),
          hold: HOLDS.has(primary.hold) ? primary.hold : 'none',
        }
        : null,
      request: request && {
        statId: opt(request.statId),
        statName: str(request.statName),
        threshold: int(request.threshold),
        missing: yes(request.missing),
      },
      announcement: opt(m.announcement),
      ambiance: ambiance && { weather: opt(ambiance.weather), time: opt(ambiance.time) },
      me: me && {
        name: str(me.name),
        hash: str(me.hash),
        sheet: opt(me.sheet),
        hp: int(me.hp),
        hpMax: int(me.hpMax),
        mp: int(me.mp),
        mpMax: int(me.mpMax),
        shield: int(me.shield),
        counters: readCounters(me.counters),
        tempModifier: int(me.tempModifier),
        tempRounds: int(me.tempRounds),
        moveLeft: num(me.moveLeft),
        moveMax: num(me.moveMax),
        stats: readStats(me.stats).filter(s => s.id !== ''),
        myTurn: yes(me.myTurn),
      },
      field: list(m.field).map(obj).map(readField).filter(f => f.ref !== '' && (f.kind === 'npc' || WAYMARKS.has(f.ref))),
      players: list(m.players).map(obj).map(p => ({
        name: str(p.name),
        hash: str(p.hash),
        connected: yes(p.connected),
        hp: int(p.hp),
        hpMax: int(p.hpMax),
        shield: int(p.shield),
        coGm: yes(p.coGm),
      })),
      turn: turn && {
        round: int(turn.round),
        acted: int(turn.acted),
        total: int(turn.total),
        diceMax: int(turn.diceMax),
        activeIndex: Number.isInteger(turn.activeIndex) ? turn.activeIndex : -1,
        turnOf: opt(turn.turnOf),
        entries: list(turn.entries).map(obj).map(e => ({
          name: str(e.name),
          initiative: int(e.initiative),
          acted: yes(e.acted),
          active: yes(e.active),
          mine: yes(e.mine),
          kind: ENTRY_KINDS.has(e.kind) ? e.kind : 'other',
          ref: opt(e.ref),
          group: opt(e.group),
          groupLabel: opt(e.groupLabel),
          roll: int(e.roll),
          mod: int(e.mod),
          stat: opt(e.stat),
        })),
        alsoOnField: {
          field: list(obj(turn.alsoOnField).field).filter(x => typeof x === 'string'),
          players: list(obj(turn.alsoOnField).players).filter(x => typeof x === 'string'),
        },
      },
      rolls: list(m.rolls).slice(0, 20).map(obj).map(readRoll),
      lastRoll: m.lastRoll ? readLastRoll(obj(m.lastRoll)) : null,
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
    return helloVersion >= 2 && !lostAt && !final && !!current && current.access === 'player'
      && !!current.room && current.caps.includes(op);
  }

  const isLocked = key => locks.has(key) || (cooldowns.get(key) ?? 0) > Date.now();

  function newId() {
    const bytes = new Uint8Array(12);
    crypto.getRandomValues(bytes);
    return btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  }

  // options : label (pour l'avis), lock (geste verrouillé jusqu'à l'état qui montre l'effet), value
  // (ce que le geste vise, pour marquer le bon bouton), cooldown.
  function command(op, fields, options = {}) {
    if (!can(op)) return false;
    const entry = { id: newId(), op, phase: 'queued', ackSeq: 0, timer: 0, ...options };
    pending.set(entry.id, entry);
    if (entry.lock) locks.set(entry.lock, entry);
    outbox.push({ entry, message: { type: 'cmd', id: entry.id, op, lobby: current.room, seen: current.seq, ...fields } });
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
    // L'état de ce seq contient déjà l'effet : sans lui, le bouton reviendrait un instant à l'ancien geste.
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
      const inert = entry.cooldown ?? 0;
      if (inert > 0) {
        cooldowns.set(entry.lock, Date.now() + inert);
        setTimeout(renderSoon, inert + 10);
      }
    }
    if (!ok) notify(entry.label, describe(error), error === 'stale' ? 'info' : 'error');
    renderSoon();
  }

  function describe(error) {
    if (error === 'noAnswer') return t('PlayerWeb.NoAnswer');
    const key = 'PlayerWeb.Err.' + error;
    return strings.has(key) ? t(key) : t('PlayerWeb.Err.unknown');
  }

  // Coupure, changement de lien, fin de session : rien n'est renvoyé de soi-même.
  function abandonCommands(lost) {
    const had = pending.size > 0;
    for (const entry of pending.values()) clearTimeout(entry.timer);
    pending.clear();
    locks.clear();
    outbox.length = 0;
    clearTimeout(pumpTimer);
    if (had && lost) notify(null, t('PlayerWeb.LostCommand'), 'error');
  }

  function notify(label, text, tone) {
    const box = $('notices');
    const item = el('div', 'notice notice-' + tone);
    item.setAttribute('role', tone === 'error' ? 'alert' : 'status');
    const close = el('button', 'icon-btn notice-close');
    close.type = 'button';
    close.setAttribute('aria-label', t('PlayerWeb.Close'));
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

  // ── Rendu par lignes réutilisées ──

  // Une ligne n'est reconstruite que si ses données changent : à 4 états par seconde,
  // tout reconstruire ferait sauter le survol et la sélection de texte.
  let rowCache = new WeakMap();
  let sectionSigs = new Map();

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

  // Les boutons d'une boîte gardent leurs nœuds d'un état à l'autre : un clic commencé sur un
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

  // Le corps d'une ligne est réécrit quand il change ; le nom, qui peut être un bouton, garde son nœud.
  const rowParts = new WeakMap();

  function patchRow(node, vm, body) {
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

  function playerGlyph(hash) {
    const pic = hash ? portraits.get(hash) : null;
    if (!pic) return el('span', 'glyph glyph-player');
    const box = el('span', 'glyph glyph-avatar');
    box.append(portraitImage(pic.uri));
    return box;
  }

  function markerGlyph(id) {
    return el('span', 'glyph wm ' + (WAYMARKS.get(id) ?? 'wm-other'), id ? Array.from(id)[0] : '?');
  }

  function npcGlyph(creature) {
    return el('span', 'glyph ' + (creature ? 'glyph-creature' : 'glyph-npc'));
  }

  function dimGlyph() {
    return el('span', 'glyph glyph-dim');
  }

  function hidden(node) {
    node.setAttribute('aria-hidden', 'true');
    return node;
  }

  // Les jauges portent le nom choisi par le modèle actif (« Éther », « Mana »), à défaut celui du jeu.
  function vitalLabel(field, d) {
    if (field === 'mp') return d.mpLabel || t('Marker.Mp');
    return d.hpLabel || t('Marker.Hp');
  }

  // Jauge compacte d'une ligne, comme ActorRow : « 30/40 » ou « 75% ».
  function meter(kind, tone, value, max, d, shield) {
    const isPercent = (kind === 'mp' ? d.mpMode : d.hpMode) === 'percent';
    const name = vitalLabel(kind, d);
    const cap = isPercent ? 100 : max > 0 ? max : 100;
    const ratio = clamp(value / cap, 0, 1);
    const label = isPercent ? value + '%' : value + '/' + max;
    const shieldText = '+' + shield + (isPercent ? '%' : '');

    const box = el('div', 'meter meter-' + kind);
    box.setAttribute('role', 'img');
    box.title = name + ' ' + label + (shield > 0 ? ', ' + t('Marker.Shield') + ' ' + shieldText : '');
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
    if (shield > 0) box.append(el('span', 'meter-shield-text', shieldText));
    return box;
  }

  // Jauge du détail d'un marqueur, avec son libellé comme les barres d'éther et de compteur du jeu.
  function detailMeter(label, ratio, tone, color) {
    const box = el('div', 'meter meter-mp');
    box.setAttribute('role', 'img');
    box.title = label;
    box.setAttribute('aria-label', label);
    const track = el('span', 'meter-track');
    const fill = el('span', 'meter-fill ' + tone);
    if (color) fill.style.setProperty('--c', color);
    fill.style.width = percent(clamp(ratio, 0, 1));
    track.append(fill);
    box.append(track, el('span', 'meter-text', label));
    return box;
  }

  // Un modèle à seuils affiche ses stats sans signe, comme StatDisplay.Stat dans le jeu.
  const statValue = (mod, d) => (d.statTarget ? String(mod) : signed(mod));

  function tempTag(modifier, rounds) {
    let label = fmt(modifier > 0 ? 'PlayerView.TempBonusFmt' : 'PlayerView.TempMalusFmt', signed(modifier));
    if (rounds > 0)
      label += ' · ' + (rounds === 1 ? t('PlayerView.TempRoundsLeftOne') : fmt('PlayerView.TempRoundsLeftFmt', rounds));
    return el('span', 'tag tag-temp', label);
  }

  function attitudeText(value) {
    return el('span', 'trail att-' + value, t(ATTITUDES.get(value)));
  }

  function connection(connected) {
    const box = el('span', 'trail conn ' + (connected ? 'is-on' : 'is-off'));
    box.append(el('span', 'dot'), document.createTextNode(t(connected ? 'Group.Connected' : 'Group.Disconnected')));
    return box;
  }

  function readOnlyTick(acted) {
    const tick = el('span', 'tick p-tick');
    tick.setAttribute('role', 'checkbox');
    tick.setAttribute('aria-readonly', 'true');
    tick.setAttribute('aria-checked', acted ? 'true' : 'false');
    tick.setAttribute('aria-label', t('PlayerWeb.Acted'));
    return tick;
  }

  // ── Lignes ──

  // Ce que le MJ partage d'un marqueur en plus de ses PV ; jamais ses stats.
  function markerDetail(m, d) {
    const box = el('div', 'p-detail');
    if (d.showMp && m.mp != null) {
      const max = m.mpMax ?? 0;
      const pct = d.mpMode === 'percent';
      box.append(detailMeter(vitalLabel('mp', d) + ': ' + (pct ? m.mp + '%' : m.mp + ' / ' + max),
        pct ? m.mp / 100 : max > 0 ? m.mp / max : 0, 'tone-mp', null));
    }
    for (const c of m.counters)
      box.append(detailMeter(c.name + ': ' + c.value + ' / ' + c.max, c.max > 0 ? c.value / c.max : 0, 'tone-counter', c.color));
    if (m.tempModifier) {
      const tags = el('div', 'tags');
      tags.append(tempTag(m.tempModifier, m.tempRounds));
      box.append(tags);
    }
    if (m.lastRoll > 0) {
      const roll = el('div', 'p-detail-roll');
      roll.append(el('span', null, t('PlayerView.LastRoll')), el('strong', null, m.lastRoll + ' / ' + m.lastRollMax));
      box.append(roll);
    }
    if (!box.firstChild) box.append(el('p', 'p-quiet', t('PlayerView.NoDetail')));
    return box;
  }

  function markerName(f, open) {
    return {
      text: f.name,
      action: 'detail',
      ref: f.ref,
      expanded: open,
      title: t(open ? 'PlayerWeb.HideDetail' : 'PlayerWeb.ShowDetail'),
    };
  }

  function fieldBody(vm) {
    const f = vm.f;
    const marker = f.kind === 'marker';
    return {
      lead: vm.reserve ? [el('span', 'p-tick-space')] : [],
      head: [hidden(marker ? markerGlyph(f.ref) : npcGlyph(f.creature))],
      name: marker ? markerName(f, vm.open) : { text: f.name },
      extra: [...(f.boss ? [pill(t('Marker.Boss'), 'accent')] : []), attitudeText(f.attitude)],
      hp: meter('hp', f.attitude, f.hp, f.hpMax, vm.d, vm.d.showShield ? f.shield : 0),
      rest: marker && vm.open ? [markerDetail(f, vm.d)] : [],
    };
  }

  function playerBody(vm) {
    const p = vm.p;
    return {
      cls: p.connected ? '' : 'is-dim',
      title: p.connected ? null : t('Group.Disconnected'),
      lead: vm.reserve ? [el('span', 'p-tick-space')] : [],
      head: [hidden(playerGlyph(p.hash))],
      name: { text: p.name, muted: !p.connected },
      extra: [...(p.coGm ? [pill(t('Group.CoGm'), 'accent')] : []), connection(p.connected)],
      hp: meter('hp', 'player', p.hp, p.hpMax, vm.d, vm.d.showShield ? p.shield : 0),
    };
  }

  // Comme TurnText.InitiativeTooltip ; sans jet d'initiative, le seul mot.
  function initiativeTitle(e) {
    if (e.roll <= 0) return t('PlayerWeb.Initiative');
    if (!e.stat) return t('Turns.InitRoll') + ': ' + e.roll;
    return t('Turns.InitRoll') + ': ' + e.roll + ' (' + e.stat + ' ' + signed(e.mod) + ') = ' + e.initiative;
  }

  function turnBody(vm) {
    const e = vm.e;
    const x = vm.x;
    const glyph = e.kind === 'marker' && e.ref ? markerGlyph(e.ref)
      : e.kind === 'npc' ? npcGlyph(x?.creature === true)
        : e.kind === 'player' && x ? playerGlyph(e.ref)
          : dimGlyph();
    const init = el('span', 'init', e.initiative);
    init.title = initiativeTitle(e);

    const extra = [];
    if (e.mine) extra.push(pill(t('PlayerView.YouPill'), 'player'));
    if (x?.boss) extra.push(pill(t('Marker.Boss'), 'accent'));
    // « À vous » sur sa seule ligne : la barre dit déjà que tout le bloc est à lui.
    if (e.active) extra.push(pill(t(e.mine && vm.myTurn ? 'PlayerView.YourTurnPill' : 'Scene.TheirTurn'), 'pending'));
    if (x && e.kind !== 'player') extra.push(attitudeText(x.attitude));

    let name = { text: e.name };
    if (e.kind === 'marker' && x) name = markerName(x, vm.open);
    else if (e.mine) name = { text: e.name, action: 'focus-sheet', title: t('PlayerWeb.FocusSheet') };

    return {
      cls: ['turn-row', e.active && 'is-active', e.acted && 'is-acted', e.group && 'is-grouped'].filter(Boolean).join(' '),
      title: e.kind === 'player' && x && !e.mine && !x.connected ? t('Group.Disconnected') : null,
      lead: [readOnlyTick(e.acted)],
      head: [init, hidden(glyph)],
      name,
      extra,
      hp: x ? meter('hp', e.kind === 'player' ? 'player' : x.attitude, x.hp, x.hpMax, vm.d, vm.d.showShield ? x.shield : 0) : null,
      rest: e.kind === 'marker' && x && vm.open ? [markerDetail(x, vm.d)] : [],
    };
  }

  function buildGroup(vm) {
    const node = el('li', 'turn-group' + (vm.active ? ' is-active' : '') + (vm.acted ? ' is-acted' : ''));
    node.append(el('span', 'p-tick-space'), el('span', 'turn-group-name', vm.label));
    return node;
  }

  function makeRow(node, vm) {
    if (vm.k === 'g') return buildGroup(vm);
    return patchRow(node, vm, vm.k === 'f' ? fieldBody : vm.k === 'p' ? playerBody : turnBody);
  }

  function rowKey(vm) {
    if (vm.k === 'g') return 'g:' + vm.id;
    if (vm.k === 'f') return 'f:' + vm.f.kind + ':' + vm.f.ref;
    if (vm.k === 'p') return 'p:' + vm.p.hash;
    return 'e:' + vm.e.kind + ':' + (vm.e.ref ?? vm.e.name);
  }

  // Ce que la page sait de la cible d'une entrée, par sa ref : sa propre fiche, un autre joueur ou le terrain.
  function entryTarget(s, e) {
    if (!e.ref) return null;
    if (e.kind === 'player') {
      if (e.mine && s.me) return { hp: s.me.hp, hpMax: s.me.hpMax, shield: s.me.shield, connected: true };
      const p = s.players.find(x => x.hash === e.ref);
      return p ? { hp: p.hp, hpMax: p.hpMax, shield: p.shield, connected: p.connected } : null;
    }
    if (e.kind === 'marker' || e.kind === 'npc') return s.field.find(f => f.kind === e.kind && f.ref === e.ref) ?? null;
    return null;
  }

  function buildRoll(r) {
    const node = el('li', 'roll');
    const head = el('div', 'roll-head');
    head.append(el('span', 'roll-name', r.roller), el('span', 'roll-total', r.total));

    const sub = el('div', 'roll-sub');
    sub.append(el('span', 'roll-detail', rollDetail(r)));
    if (r.target != null && r.success != null) {
      sub.append(el('span', 'roll-verdict ' + (r.success ? 'is-ok' : 'is-ko'),
        fmt(r.success ? 'Dice.Success' : 'Dice.Failure', r.target)));
    }
    if (r.at > 0) sub.append(el('span', 'roll-time', clock(r.at)));

    node.append(head, sub);
    return node;
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

  function flash(node) {
    node.classList.add('is-new');
  }

  // ── Barre ──

  function chip(cls, title, text) {
    const node = el('span', 'chip' + (cls ? ' ' + cls : ''));
    setTitle(node, title);
    if (text != null) node.textContent = text;
    return node;
  }

  const isLobby = s => !!(s.link.lobbyCode || s.detail.lobby);
  const lobbyCode = s => s.detail.lobby ?? s.link.lobbyCode ?? '';

  function offlineHint(s) {
    return t(s.detail.consent ? 'PlayerView.OfflineHint' : 'PlayerView.OfflineConsentHint');
  }

  function versionHint(s) {
    return fmt('PlayerView.VersionHintFmt', s.detail.minVersion ?? '');
  }

  function rejectionHint(s) {
    switch (s.detail.rejection) {
      case 'denied':
        return isLobby(s) ? fmt('PlayerView.RejectedDeniedFmt', lobbyCode(s)) : t('PlayerView.RejectedInvalid');
      case 'roomLimit':
        return t('PlayerView.RejectedRoomLimit');
      case 'rateLimited':
        return t('PlayerView.RejectedRateLimited');
      default:
        return t('PlayerView.RejectedInvalid');
    }
  }

  function linkText(link) {
    switch (link.state) {
      case 'inSession':
        return link.onlinePlayers === 1 ? t('Link.InSessionOne') : fmt('Link.InSession', link.onlinePlayers);
      case 'serverOnly':
        return t('Gm.LinkServer');
      case 'reconnecting':
        return t('Gm.LinkReconnecting');
      default:
        return t('Gm.Local');
    }
  }

  // Le MJ et son lien, comme la fenêtre Séance du jeu, puis le modèle actif.
  function roomTooltip(s) {
    const lines = [];
    if (s.gm) {
      lines.push((s.gm.name ? fmt('PlayerView.GmFmt', s.gm.name) : t('Group.Gm'))
        + ' · ' + t(s.gm.connected ? 'Group.Connected' : 'Group.Disconnected'));
    }
    if (s.template) lines.push(fmt('PlayerWeb.TemplateFmt', s.template));
    return lines.join('\n');
  }

  function roomChip(s) {
    const tip = roomTooltip(s);
    const node = chip('chip-session', null, null);
    node.append(el('span', 'dot link-' + s.link.state));
    if (s.link.lobbyCode) {
      const code = s.link.lobbyCode;
      const full = fmt('Scene.LobbyFmt', code) + ' · ' + fmt('Scene.OnlineFmt', s.link.partyOnline, s.link.partyShown);
      // Étroite, la puce se raccourcit et garde le texte complet en infobulle, comme en jeu.
      node.append(el('span', 'p-chip-full', full),
        el('span', 'p-chip-short', fmt('PlayerView.LobbyShortFmt', code, s.link.partyOnline, s.link.partyShown)));
      setTitle(node, tip ? full + '\n' + tip : full);
    } else {
      node.append(el('span', null, linkText(s.link)));
      setTitle(node, tip);
    }
    return node;
  }

  // La puce de séance de la vue en jeu (PlayerWindow.SessionChip), état par état.
  function sessionChip(s) {
    const code = lobbyCode(s);
    switch (s.view) {
      case 'instance':
        return chip('chip-muted', t('PlayerView.InstanceHint'), t('PlayerView.InstanceChip'));
      case 'versionRejected':
        return chip('chip-danger', versionHint(s), t('PlayerView.VersionChip'));
      case 'joinRejected':
        return chip('chip-danger', rejectionHint(s),
          isLobby(s) ? fmt('PlayerView.LobbyRejectedFmt', code) : t('PlayerView.RejectedTitle'));
      case 'awaitingApproval':
        return chip('chip-pending', fmt('PlayerView.PendingHintFmt', code), fmt('PlayerView.LobbyPendingFmt', code));
      case 'offline':
        return chip('chip-danger', offlineHint(s), t('Gm.Local'));
      case 'reconnecting':
        return chip('chip-warning', t('PlayerView.ReconnectingNotice'), t('Gm.LinkReconnecting'));
      case 'outOfSession':
        return chip('chip-muted', null, t('PlayerView.LinkServer'));
      case 'joining':
        return chip('chip-muted', null, t('PlayerView.LinkJoining'));
      case 'handover':
        return chip('chip-pending', t('PlayerView.HandoverTooltip'), t('PlayerView.Handover'));
      case 'gmAway':
        return chip('chip-warning', t('PlayerView.GmAwayTooltip'),
          isLobby(s) ? fmt('PlayerView.LobbyGmAwayFmt', code) : t('PlayerView.GmAway'));
      default:
        return roomChip(s);
    }
  }

  function turnChip(s) {
    if (s.turn.activeIndex < 0) return chip('chip-muted', null, t('PlayerView.AllActed'));
    if (s.me?.myTurn) return chip('chip-pending', null, t('PlayerView.YourTurn'));
    return chip(null, null, fmt('PlayerView.TurnOfFmt', s.turn.turnOf ?? ''));
  }

  function ambianceText(s) {
    if (!s.ambiance) return null;
    const parts = [s.ambiance.weather, s.ambiance.time].filter(Boolean);
    return parts.length > 0 ? parts.join(' · ') : null;
  }

  function renderBar(s) {
    const vm = {
      view: s.view,
      link: s.link,
      detail: s.detail,
      gm: s.gm,
      template: s.template,
      turn: s.turn && { round: s.turn.round, acted: s.turn.acted, total: s.turn.total, activeIndex: s.turn.activeIndex, turnOf: s.turn.turnOf },
      myTurn: !!s.me?.myTurn,
      // Comme la barre du jeu : pas de puce d'ambiance pendant un combat.
      ambiance: !s.turn && SESSION_VIEWS.has(s.view) ? ambianceText(s) : null,
    };
    if (!changed('bar', vm)) return;

    const items = [];
    if (s.turn) {
      items.push(el('span', 'bar-round', fmt('Turns.Round', s.turn.round)));
      items.push(el('span', 'bar-progress', fmt('Turns.Progress', s.turn.acted, s.turn.total)));
    }
    items.push(sessionChip(s));
    if (vm.ambiance) items.push(chip('chip-ambiance', t('PlayerView.AmbianceTooltip'), vm.ambiance));
    if (s.turn) items.push(turnChip(s));
    $('bar-state').replaceChildren(...items);
  }

  // ── Action pleine et action teintée (Bar.cs) ──

  let barActions = null;
  let actionKey = null;
  let actionChangedAt = 0;
  let lastRollAt = 0;
  const armed = { primary: null, tinted: null };
  // Un bouton de la barre n'obéit au clavier que si l'on y est arrivé par Tab.
  const keyFocus = { primary: false, tinted: false };
  let tabAt = 0;

  function endTurnAction(p) {
    const label = p.hold === 'sent'
      ? t('PlayerView.EndTurnSent')
      : t(p.groupTurn ? 'Tactical.EndGroupTurn' : 'Tactical.EndMyTurn');
    const tip = p.hold === 'handover' ? t('PlayerView.EndTurnHandover')
      : p.hold === 'gmAway' ? t('PlayerView.EndTurnGmAway')
        : p.hold === 'none' ? t('PlayerView.EndMyTurnTooltip')
          : '';
    return { op: 'selfEndTurn', text: label, tip, disabled: p.hold !== 'none' || !can('selfEndTurn') };
  }

  function resolveActions(s) {
    const p = s.primary;
    if (!p) return null;
    const end = p.canEndTurn ? endTurnAction(p) : null;
    if (p.kind === 'roll') {
      const text = p.statName != null ? fmt('PlayerView.RollStatFmt', p.statName) : t('PlayerView.RollNoStat');
      return { primary: { op: 'selfAnswer', text, tip: text, disabled: !can('selfAnswer') }, tinted: end };
    }
    return end ? { primary: end, tinted: null } : null;
  }

  const inert = () => Date.now() - actionChangedAt < PRIMARY_INERT_MS || isLocked('bar');

  function paintAction(button, action) {
    button.hidden = !action;
    if (!action) return;
    setText(button, action.text);
    setTitle(button, action.tip);
    setBusy(button, locks.get('bar')?.op === action.op, action.disabled);
    button.classList.toggle('is-settling', inert());
  }

  function renderActions(s) {
    const actions = resolveActions(s);
    // Même clé que la barre du jeu : le geste, la stat, la fin de tour possible, la manche et l'entrée active.
    const key = actions
      ? JSON.stringify([s.primary.kind, s.primary.statName, s.primary.canEndTurn, s.turn?.round ?? 0, s.turn?.activeIndex ?? -1])
      : null;
    if (key !== actionKey) {
      actionKey = key;
      actionChangedAt = Date.now();
      setTimeout(renderSoon, PRIMARY_INERT_MS + 10);
    }
    barActions = actions;
    paintAction($('primary'), actions?.primary ?? null);
    paintAction($('tinted'), actions?.tinted ?? null);
  }

  // ── Avis de la page : deux au plus, dans l'ordre de la vue en jeu ──

  let hiddenAnnouncement = null;
  const noticeParts = new WeakMap();

  function noticeSpec(k, tone, text, details, action) {
    return {
      k,
      make: () => {
        const node = el('div', 'p-notice p-notice-' + tone);
        const body = el('div', 'p-notice-body');
        const main = el('p', 'p-notice-text');
        const more = el('p', 'p-notice-details');
        body.append(main, more);
        const end = el('div', 'p-notice-end');
        node.append(body, end);
        noticeParts.set(node, { main, more, end });
        return node;
      },
      update: node => {
        const p = noticeParts.get(node);
        setText(p.main, text);
        setText(p.more, details);
        p.more.hidden = details === '';
        syncControls(p.end, action ? [action] : []);
      },
    };
  }

  function requestNotice(s) {
    const r = s.request;
    const text = r.statId == null && !r.missing
      ? fmt('PlayerView.RollRequestNoStatFmt', r.threshold)
      : fmt('RollRequest.Banner', r.statName, r.threshold);
    const details = [];
    if (r.missing) details.push(fmt('PlayerView.RollMissingStatFmt', r.statName));
    if (r.statId != null && s.display.statTarget) details.push(t('PlayerView.RollHintTarget'));
    if (s.lowerIsBetter) details.push(t('RollRequest.HintLower'));
    return noticeSpec('request', 'accent', text, details.join('\n'), {
      k: 'dismiss',
      make: () => control('btn btn-small', 'dismiss'),
      update: button => {
        setText(button, t('RollRequest.Dismiss'));
        setBusy(button, locks.has('dismiss'), !can('selfDismiss'));
      },
    });
  }

  function announcementNotice(text) {
    return noticeSpec('announcement', 'accent', fmt('PlayerView.AnnouncementFmt', text), '', {
      k: 'hide',
      make: () => {
        const button = control('icon-btn', 'hide-announcement');
        button.append(icon('close'));
        return button;
      },
      update: button => {
        setTitle(button, t('PlayerView.HideAnnouncement'));
        setAttr(button, 'aria-label', t('PlayerView.HideAnnouncement'));
      },
    });
  }

  function renderNotices(s) {
    // Une autre annonce, ou plus d'annonce : celle qu'on avait masquée ne compte plus.
    if (hiddenAnnouncement !== null && s.announcement !== hiddenAnnouncement) hiddenAnnouncement = null;
    const specs = [];
    if (ROOM_VIEWS.has(s.view) && s.request) specs.push(requestNotice(s));
    if (s.view === 'reconnecting') specs.push(noticeSpec('reconnecting', 'warning', t('PlayerView.ReconnectingNotice'), '', null));
    if (SESSION_VIEWS.has(s.view) && s.announcement && s.announcement !== hiddenAnnouncement)
      specs.push(announcementNotice(s.announcement));
    syncControls($('p-notices'), specs.slice(0, PAGE_NOTICES_MAX));
  }

  // ── Liste : message d'état, Scène ou Ordre du tour ──

  let expanded = null;
  let scrolledBlock = null;
  let activeBlock = null;

  function setCount(id, n) {
    setText($(id), n > 0 ? String(n) : '');
  }

  function stateMessage(s) {
    switch (s.view) {
      case 'outOfSession':
        return { title: t('Lobby.JoinTitle'), text: t('PlayerWeb.JoinHint') };
      case 'joining':
        return { title: t('PlayerView.LinkJoining'), text: t('PlayerView.OnFieldEmptyHint') };
      case 'offline':
        return { title: t('Gm.Local'), text: offlineHint(s), note: t('PlayerWeb.InGameHint') };
      case 'awaitingApproval':
        return { title: t('PlayerView.PendingTitle'), text: fmt('PlayerView.PendingHintFmt', lobbyCode(s)), note: t('PlayerWeb.InGameHint') };
      case 'joinRejected':
        return { title: t('PlayerView.RejectedTitle'), text: rejectionHint(s), note: t('PlayerWeb.InGameHint') };
      case 'versionRejected':
        return { title: t('PlayerView.VersionTitle'), text: versionHint(s) };
      default:
        return { title: t('PlayerView.InstanceTitle'), text: t('PlayerView.InstanceHint') };
    }
  }

  function renderStateMessage(s) {
    const msg = stateMessage(s);
    setText($('state-title'), msg.title);
    setText($('state-text'), msg.text);
    setText($('state-note'), msg.note ?? '');
    $('state-note').hidden = !msg.note;
  }

  function fieldItem(f, s, reserve) {
    return { k: 'f', f, d: s.display, open: f.kind === 'marker' && expanded === f.ref, reserve };
  }

  function renderScene(s) {
    const field = s.field.map(f => fieldItem(f, s, false));
    setCount('count-field', field.length);
    const empty = $('empty-field');
    empty.hidden = field.length > 0;
    empty.classList.toggle('is-compact', s.players.length > 0);
    reconcile($('list-field'), field, rowKey, makeRow);

    $('players-block').hidden = s.players.length === 0;
    setText($('count-players'), String(s.players.length));
    reconcile($('list-players'), s.players.map(p => ({ k: 'p', p, pic: portraitRev(p.hash), d: s.display, reserve: false })), rowKey, makeRow);
  }

  function renderCombat(s) {
    const turn = s.turn;
    setText($('turn-sub'), fmt('Turns.SubtitleFmt', turn.diceMax));

    const items = [];
    const groups = new Set();
    turn.entries.forEach(e => {
      // Le MJ peut effacer le nom d'un groupe : l'en-tête n'aurait plus rien à dire.
      if (e.group && !groups.has(e.group)) {
        groups.add(e.group);
        const members = turn.entries.filter(x => x.group === e.group);
        items.push({
          k: 'g',
          id: e.group,
          label: e.groupLabel ?? t('Turns.GroupUnnamed'),
          active: members.some(x => x.active),
          acted: members.every(x => x.acted),
        });
      }
      const x = entryTarget(s, e);
      items.push({
        k: 'e', e, x, pic: e.kind === 'player' ? portraitRev(e.ref) : 0, d: s.display, myTurn: !!s.me?.myTurn,
        open: e.kind === 'marker' && !!x && expanded === e.ref,
      });
    });
    reconcile($('list-turn'), items, rowKey, makeRow);

    const also = [
      ...turn.alsoOnField.field.map(ref => s.field.find(f => f.ref === ref)).filter(Boolean).map(f => fieldItem(f, s, true)),
      ...turn.alsoOnField.players.map(hash => s.players.find(p => p.hash === hash)).filter(Boolean)
        .map(p => ({ k: 'p', p, pic: portraitRev(p.hash), d: s.display, reserve: true })),
    ];
    $('also-block').hidden = also.length === 0;
    reconcile($('list-also'), also, rowKey, makeRow);

    const active = turn.activeIndex >= 0 ? turn.entries[turn.activeIndex] : null;
    activeBlock = active ? turn.round + ':' + (active.group ?? turn.activeIndex) : null;
  }

  // La liste ne défile vers le bloc actif qu'une fois, quand il change. Masquée (segment Ma fiche,
  // premier rendu), elle ne peut pas défiler : le bloc reste à atteindre jusqu'à ce qu'elle s'affiche.
  function scrollToActiveBlock() {
    if (!activeBlock || activeBlock === scrolledBlock) return;
    const row = $('list-turn').querySelector('.turn-row.is-active');
    if (!row || row.offsetParent === null) return;
    scrolledBlock = activeBlock;
    row.scrollIntoView({ block: 'nearest' });
  }

  function renderList(s) {
    // Un marqueur caché ou parti referme son détail.
    if (expanded && !s.field.some(f => f.kind === 'marker' && f.ref === expanded)) expanded = null;

    const stateful = STATE_VIEWS.has(s.view);
    $('state-msg').hidden = !stateful;
    $('scene').hidden = stateful || !!s.turn;
    $('combat').hidden = stateful || !s.turn;
    if (!s.turn) activeBlock = null;
    if (stateful) renderStateMessage(s);
    else if (s.turn) renderCombat(s);
    else renderScene(s);
  }

  // ── Ma fiche ──

  let statQuery = '';
  // Le portrait ouvre la ligne d'identité, comme DrawOwnPortrait ; son nœud reste quand la ligne change.
  const sheetAvatar = el('span', 'p-avatar');

  // Sans accents ni casse, comme la recherche des réglages : « erudition » trouve « Érudition ».
  const fold = text => text.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();
  const matches = (stat, query) => query.trim() === '' || fold(stat.name).includes(fold(query.trim()));

  // « Acrobaties (Dex) » : nom et base, découpés comme StatGrid.ToTile.
  function toTile(stat) {
    let name = stat.name.trim();
    let basis = '';
    const open = name.lastIndexOf('(');
    if (name.endsWith(')') && open > 0 && open < name.length - 2) {
      basis = name.slice(open + 1, -1).trim();
      name = name.slice(0, open).trimEnd();
    }
    return { stat, name, basis };
  }

  const tileParts = new WeakMap();

  function tileSpecs(tiles, s, highlighted, highlightTip) {
    const d = s.display;
    const temp = s.me.tempModifier;
    const rolling = locks.get('roll')?.value;
    return tiles.map(tile => ({
      k: 's:' + tile.stat.id,
      make: () => {
        const button = control('p-tile', 'roll-stat');
        const name = el('span', 'p-tile-name');
        const label = el('span', 'p-tile-label');
        const basis = el('span', 'p-tile-basis');
        name.append(label, basis);
        const value = el('span', 'p-tile-value');
        button.append(name, icon('die'), value);
        tileParts.set(button, { label, basis, value });
        return button;
      },
      update: button => {
        const p = tileParts.get(button);
        const mod = tile.stat.mod;
        const value = statValue(mod, d);
        setText(p.label, tile.name);
        setText(p.basis, tile.basis);
        setText(p.value, value);
        p.value.className = 'p-tile-value' + (d.statTarget || mod > 0 ? '' : mod < 0 ? ' is-neg' : ' is-zero');
        const requested = highlighted != null && highlighted === tile.stat.id;
        button.classList.toggle('is-requested', requested);
        setData(button, { stat: tile.stat.id });

        const lines = [];
        if (tile.basis) lines.push(tile.stat.name);
        if (requested && highlightTip) lines.push(highlightTip);
        lines.push(temp !== 0
          ? fmt('StatGrid.RollBonusFmt', tile.name, value, signed(temp))
          : fmt('StatGrid.RollFmt', tile.name, value));
        setTitle(button, lines.join('\n'));
        const rollLine = lines[lines.length - 1];
        setAttr(button, 'aria-label', requested && highlightTip ? highlightTip + '. ' + rollLine : rollLine);
        setBusy(button, rolling === tile.stat.id, !can('selfRoll'));
      },
    }));
  }

  function sheetBar(label, value, ratio, fillClass, shieldRatio, color) {
    const box = el('div', 'p-bar');
    box.setAttribute('role', 'img');
    box.setAttribute('aria-label', label + ' ' + value);
    const head = el('div', 'p-bar-head');
    head.append(el('span', 'p-bar-label', label), el('span', 'p-bar-value', value));
    const track = el('span', 'p-bar-track');
    const fill = el('span', 'p-bar-fill ' + fillClass);
    fill.style.width = percent(clamp(ratio, 0, 1));
    if (color) fill.style.setProperty('--c', color);
    track.append(fill);
    if (shieldRatio > 0) {
      const shield = el('span', 'p-bar-shield');
      shield.style.left = percent(clamp(ratio, 0, 1));
      shield.style.width = percent(clamp(shieldRatio, 0, 1 - clamp(ratio, 0, 1)));
      track.append(shield);
    }
    box.append(head, track);
    return box;
  }

  // Textes de HpBar : « 30 (+5) / 40 », ou « 75% (+10%) » en pourcentage.
  function sheetGauges(me, d) {
    const nodes = [];
    const shield = d.showShield ? me.shield : 0;
    const hpPercent = d.hpMode === 'percent';
    const hpRatio = hpPercent ? me.hp / 100 : me.hpMax > 0 ? me.hp / me.hpMax : 0;
    const hpText = hpPercent
      ? me.hp + '%' + (shield > 0 ? ' (+' + shield + '%)' : '')
      : me.hp + (shield > 0 ? ' (+' + shield + ')' : '') + ' / ' + me.hpMax;
    const shieldRatio = shield > 0 ? (hpPercent ? shield / 100 : me.hpMax > 0 ? shield / me.hpMax : 0) : 0;
    nodes.push(sheetBar(vitalLabel('hp', d), hpText, hpRatio, 'tone-player' + (hpRatio <= 0.25 ? ' is-low' : ''), shieldRatio, null));

    if (d.showMp) {
      const mpPercent = d.mpMode === 'percent';
      const mpRatio = mpPercent ? me.mp / 100 : me.mpMax > 0 ? me.mp / me.mpMax : 0;
      nodes.push(sheetBar(vitalLabel('mp', d), mpPercent ? me.mp + '%' : me.mp + ' / ' + me.mpMax, mpRatio, 'tone-mp', 0, null));
    }

    for (const c of me.counters)
      nodes.push(sheetBar(c.name, c.value + ' / ' + c.max, c.max > 0 ? c.value / c.max : 0, 'tone-counter', 0, c.color));
    return nodes;
  }

  function lastRollNodes(r, d) {
    const parts = [];
    if (r.stat) parts.push(r.stat + ' ' + statValue(r.statMod ?? r.mod - (r.tempMod ?? 0), d));
    if (r.tempMod != null) parts.push(fmt('PlayerView.BonusFmt', signed(r.tempMod)));
    let detail = r.raw + ' / ' + r.diceMax;
    if (parts.length > 0) detail += ' · ' + parts.join(', ');
    const nodes = [
      el('span', 'p-last-label', t('PlayerView.LastRoll')),
      el('span', 'p-last-total', r.total),
      el('span', 'p-last-detail', detail),
    ];
    if (r.target != null && r.success != null)
      nodes.push(el('span', 'p-verdict ' + (r.success ? 'is-ok' : 'is-ko'), fmt(r.success ? 'Dice.Success' : 'Dice.Failure', r.target)));
    return nodes;
  }

  function renderSheet(s) {
    const me = s.me;
    const d = s.display;
    setText($('sheet-name'), me ? me.name : t('PlayerView.MySheet'));

    for (const id of ['sheet-gauges', 'sheet-temp', 'tiles-main', 'tiles-skills', 'sheet-free'])
      $(id).hidden = !me;
    if (!me) {
      if (changed('identity', null)) $('sheet-identity').replaceChildren(el('span', null, t('Sheets.NoneLoaded')));
      $('sheet-move').hidden = true;
      $('stat-search-box').hidden = true;
      $('sheet-stats-empty').hidden = true;
      $('sheet-last').hidden = true;
      return;
    }

    if (changed('avatar', [me.hash, portraitRev(me.hash)]))
      sheetAvatar.replaceChildren(hidden(recycling(sheetAvatar, () => playerGlyph(me.hash))));

    if (changed('identity', [me.myTurn, me.sheet, s.template])) {
      const nodes = [sheetAvatar];
      if (me.myTurn) nodes.push(pill(t('PlayerView.YourTurnPill'), 'pending'));
      nodes.push(el('span', null, me.sheet
        ? fmt('PlayerView.SheetNameFmt', me.sheet) + (s.template ? ' · ' + s.template : '')
        : t('Sheets.NoneLoaded')));
      $('sheet-identity').replaceChildren(...nodes);
    }

    if (changed('gauges', [me.hp, me.hpMax, me.mp, me.mpMax, me.shield, me.counters, d]))
      $('sheet-gauges').replaceChildren(...sheetGauges(me, d));

    if (changed('temp', [me.tempModifier, me.tempRounds]))
      $('sheet-temp').replaceChildren(...(me.tempModifier !== 0 ? [tempTag(me.tempModifier, me.tempRounds)] : []));

    // Le quota du tour en cours, seulement pendant son tour, comme le bandeau tactique.
    const move = $('sheet-move');
    move.hidden = !(me.myTurn && me.moveMax > 0);
    if (!move.hidden) {
      const ratio = clamp(me.moveLeft / me.moveMax, 0, 1);
      setText($('move-value'), fmt('Tactical.MovementValue', decimal(me.moveLeft), decimal(me.moveMax)));
      setData(move, { level: ratio <= 0 ? 'out' : ratio <= 0.25 ? 'low' : 'ok' });
      $('move-fill').style.width = percent(ratio);
    }

    renderStats(s);

    setBusy($('roll-free'), locks.get('roll')?.value === '', !can('selfRoll'));

    const last = $('sheet-last');
    last.hidden = !s.lastRoll;
    if (s.lastRoll && changed('last', [s.lastRoll, d.statTarget])) last.replaceChildren(...lastRollNodes(s.lastRoll, d));
  }

  function renderStats(s) {
    const me = s.me;
    const stats = me.stats;
    // En mode cible, le seuil du MJ remplace la valeur de la stat : la surligner tromperait.
    const request = ROOM_VIEWS.has(s.view) ? s.request : null;
    const highlighted = request && !s.display.statTarget ? request.statId : null;
    const highlightTip = request ? fmt('PlayerView.RequestedTileFmt', request.threshold) : null;

    const search = $('stat-search');
    const searchable = stats.length > STAT_FILTER_THRESHOLD;
    $('stat-search-box').hidden = !searchable;
    // Sans champ pour l'effacer, une saisie restante masquerait des tuiles.
    if (!searchable && statQuery !== '') {
      statQuery = '';
      search.value = '';
    }
    $('stat-search-clear').hidden = statQuery === '';

    // La stat que le MJ attend reste visible, quoi qu'on cherche.
    const shown = stats.filter(st => (request?.statId != null && st.id === request.statId) || matches(st, statQuery));
    const empty = $('sheet-stats-empty');
    const message = stats.length === 0 ? t(s.template ? 'Player.NoStats' : 'Player.NoTemplate')
      : shown.length === 0 ? t('Sheets.NoStatMatch') : '';
    setText(empty, message);
    empty.hidden = message === '';

    const tiles = shown.map(toTile);
    const abilities = tiles.filter(x => x.basis === '');
    const skills = tiles.filter(x => x.basis !== '');
    const split = abilities.length > 0 && skills.length > 0;
    syncControls($('tiles-main'), tileSpecs(split ? abilities : tiles, s, highlighted, highlightTip));
    syncControls($('tiles-skills'), tileSpecs(split ? skills : [], s, highlighted, highlightTip));
  }

  function renderRolls(s) {
    setCount('count-rolls', s.rolls.length);
    $('empty-rolls').hidden = s.rolls.length > 0;
    reconcile($('list-rolls'), s.rolls, r => r.at + '|' + r.roller + '|' + r.raw + '|' + r.total, rebuild(buildRoll), flash);
  }

  // ── Gestes ──

  function onBarAction(button, event) {
    const which = button.id === 'tinted' ? 'tinted' : 'primary';
    const action = barActions?.[which] ?? null;
    const armedKey = armed[which];
    armed[which] = null;
    if (!action || action.disabled) return;
    // Geste relevé à l'appui : si l'action a changé avant le relâchement, le clic visait autre chose.
    if (event.detail >= 1 && armedKey !== actionKey) return;
    if (event.detail === 0 && !keyFocus[which]) return;
    if (inert()) return;
    actionChangedAt = lastRollAt = Date.now();
    setTimeout(renderSoon, PRIMARY_INERT_MS + 10);
    command(action.op, {}, { label: action.text, lock: 'bar', cooldown: PRIMARY_INERT_MS });
  }

  // Comme RollOnce : un double clic, ou un clic juste après la barre, ne lance qu'un dé.
  function onRollStat(button) {
    if (Date.now() - lastRollAt < PRIMARY_INERT_MS || isLocked('roll') || !current?.me) return;
    const id = button.dataset.stat || '';
    const stat = id ? current.me.stats.find(x => x.id === id) : null;
    if (id && !stat) return;
    lastRollAt = Date.now();
    command('selfRoll', stat ? { stat: stat.id } : {}, {
      label: stat ? fmt('PlayerView.RollStatFmt', stat.name) : t('PlayerWeb.RollNoStat'),
      lock: 'roll',
      value: id,
      cooldown: PRIMARY_INERT_MS,
    });
  }

  function onDismiss() {
    if (isLocked('dismiss')) return;
    command('selfDismiss', {}, { label: t('RollRequest.Dismiss'), lock: 'dismiss' });
  }

  function onHideAnnouncement() {
    hiddenAnnouncement = current?.announcement ?? null;
    render();
  }

  function onDetail(button) {
    const ref = button.dataset.ref;
    expanded = expanded === ref ? null : ref;
    render();
  }

  // Sa propre ligne de l'ordre du tour mène à Ma fiche, déjà à côté en largeur normale.
  function onFocusSheet() {
    if (window.matchMedia(NARROW).matches) {
      selectTab('sheet', false);
    } else {
      const sheet = $('sec-sheet');
      if (sheet.scrollIntoView) sheet.scrollIntoView({ block: 'nearest' });
    }
  }

  function onSearchClear() {
    statQuery = '';
    $('stat-search').value = '';
    render();
    $('stat-search').focus({ preventScroll: true });
  }

  const ACTIONS = {
    primary: onBarAction,
    tinted: onBarAction,
    'roll-stat': onRollStat,
    dismiss: onDismiss,
    'hide-announcement': onHideAnnouncement,
    detail: onDetail,
    'focus-sheet': onFocusSheet,
    'search-clear': onSearchClear,
  };

  function onClick(event) {
    const button = event.target instanceof Element ? event.target.closest('[data-action]') : null;
    if (!button) return;
    const handler = ACTIONS[button.dataset.action];
    // Un clic de souris ne garde pas le focus : Espace ou Entrée, tapés ensuite pour le jeu, referaient le geste.
    const pointer = event.detail >= 1;
    if (pointer) button.blur();
    if (!handler || button.getAttribute('aria-disabled') === 'true') return;
    // Un double clic ne fait jamais deux gestes : le second tomberait sur l'état suivant.
    if (event.detail > 1) return;
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

  let currentTab = TABS.includes(load(KEY_TAB)) ? load(KEY_TAB) : 'sheet';
  let wasCombat = false;

  function renderTabs(s) {
    const stateful = STATE_VIEWS.has(s.view);
    // États à message : le message puis Ma fiche, sans segments, comme la vue en jeu.
    $('tabs').hidden = stateful;
    setText($('tab-list'), t(s.turn ? 'Scene.TurnOrder' : 'Sidebar.Scene'));
    for (const button of $('tabs').querySelectorAll('.tab')) {
      const selected = button.dataset.tab === currentTab;
      button.setAttribute('aria-selected', String(selected));
      button.tabIndex = selected ? 0 : -1;
    }
    for (const section of document.querySelectorAll('.section'))
      section.classList.toggle('is-current', stateful || section.dataset.tab === currentTab);
  }

  function selectTab(name, focus) {
    if (!TABS.includes(name) || name === currentTab) return;
    currentTab = name;
    save(KEY_TAB, name);
    if (current) renderTabs(current);
    const button = $('tabs').querySelector('.tab[data-tab="' + name + '"]');
    if (focus) button?.focus();
    const layout = $('layout');
    if (layout.getBoundingClientRect().top < 0) layout.scrollIntoView({ block: 'start' });
    // Après le recalage : avant, le défilement vers le bloc actif était aussitôt défait.
    if (current) scrollToActiveBlock();
  }

  function render() {
    if (!current) return;
    // Ordre du tour au début de chaque combat, Ma fiche quand il se termine. Une coupure masque le
    // combat sans le finir : c'est sa présence qui compte, pas son affichage, comme en jeu.
    const combat = current.combatPresent;
    if (combat !== wasCombat) {
      currentTab = combat ? 'list' : 'sheet';
      save(KEY_TAB, currentTab);
      scrolledBlock = null;
    }
    wasCombat = combat;

    renderBar(current);
    renderActions(current);
    renderNotices(current);
    renderList(current);
    renderSheet(current);
    renderRolls(current);
    renderTabs(current);
    scrollToActiveBlock();
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
    if (show) banner.textContent = t('PlayerWeb.Reconnecting');
  }

  function applyTexts() {
    document.title = t('PlayerWeb.Title');
    for (const node of document.querySelectorAll('[data-t]')) node.textContent = t(node.dataset.t);
    for (const node of document.querySelectorAll('[data-t-label]')) node.setAttribute('aria-label', t(node.dataset.tLabel));
    const search = $('stat-search');
    search.placeholder = t('Models.StatsFilter');
    search.setAttribute('aria-label', t('Models.StatsFilter'));
    const clear = $('stat-search-clear');
    clear.title = t('Search.Clear');
    clear.setAttribute('aria-label', t('Search.Clear'));
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

  let current = null;
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
    expanded = null;
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

    applyTexts();
    forceRebuild();
    syncControls($('p-notices'), []);
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
    else if (screen) {
      showScreen(null);
      // Le rendu a eu lieu sous l'écran d'attente, liste masquée : le défilement n'a pas pu se faire.
      scrollToActiveBlock();
    }
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
    expanded = null;
    ticket = fresh;
    session = null;
    forget(KEY_SESSION);
    final = false;
    lostAt = 0;
    clearTimeout(retryTimer);
    clearTimeout(unreachableTimer);
    closeSocket();
    current = null;
    portraits = new Map();
    showScreen('connecting');
    connect();
  });

  $('tabs').addEventListener('click', event => {
    const button = event.target instanceof Element ? event.target.closest('.tab') : null;
    if (button) selectTab(button.dataset.tab, false);
  });

  $('tabs').addEventListener('keydown', event => {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
    const index = TABS.indexOf(currentTab);
    const step = event.key === 'ArrowRight' ? 1 : -1;
    selectTab(TABS[(index + step + TABS.length) % TABS.length], true);
    event.preventDefault();
  });

  document.addEventListener('click', onClick);

  for (const which of ['primary', 'tinted']) {
    const button = $(which);
    button.addEventListener('pointerdown', event => {
      if (event.button === 0) armed[which] = actionKey;
    });
    button.addEventListener('focus', () => {
      keyFocus[which] = Date.now() - tabAt < 1000;
    });
    button.addEventListener('blur', () => {
      keyFocus[which] = false;
    });
  }
  document.addEventListener('keydown', event => {
    if (event.key === 'Tab') tabAt = Date.now();
  }, true);

  $('stat-search').addEventListener('input', () => {
    statQuery = $('stat-search').value;
    render();
  });
  $('stat-search').addEventListener('keydown', event => {
    if (event.key === 'Escape' && statQuery !== '') {
      event.preventDefault();
      onSearchClear();
    }
  });

  $('stat-search-clear').append(icon('close'));

  applyTexts();
  renderScreen();
  connect();
})();
