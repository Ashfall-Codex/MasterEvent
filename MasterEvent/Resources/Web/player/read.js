import {
  ATTITUDES, ENTRY_KINDS, HEX_COLOR, HOLD, HOLDS, KIND, LINK, LINK_STATES, OPS, PERCENT, REJECTIONS, VIEW, VIEWS, WAYMARKS,
} from './constants.js';

const num = x => (typeof x === 'number' && Number.isFinite(x) ? x : 0);
export const int = x => Math.trunc(num(x));
export const str = x => (typeof x === 'string' ? x : '');
export const opt = x => (typeof x === 'string' && x !== '' ? x : null);
const list = x => (Array.isArray(x) ? x : []);
export const obj = x => (x && typeof x === 'object' && !Array.isArray(x) ? x : {});
const flag = (x, fallback) => (typeof x === 'boolean' ? x : fallback);
export const yes = x => flag(x, false);
const attitude = x => (ATTITUDES.has(x) ? x : 'neutral');
const maybeInt = x => (typeof x === 'number' && Number.isFinite(x) ? Math.trunc(x) : null);
const gaugeMode = x => (x === PERCENT ? PERCENT : 'points');

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
  const marker = x.kind === KIND.marker;
  return {
    kind: marker ? KIND.marker : KIND.npc,
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

function readDetail(detail) {
  return {
    lobby: opt(detail.lobby),
    minVersion: opt(detail.minVersion),
    rejection: REJECTIONS.has(detail.rejection) ? detail.rejection : null,
    consent: flag(detail.consent, true),
  };
}

function readLink(link) {
  return {
    state: LINK_STATES.has(link.state) ? link.state : LINK.offline,
    lobbyCode: opt(link.lobbyCode),
    onlinePlayers: int(link.onlinePlayers),
    partyShown: int(link.partyShown),
    partyOnline: int(link.partyOnline),
  };
}

function readDisplay(display) {
  return {
    hpMode: gaugeMode(display.hpMode),
    mpMode: gaugeMode(display.mpMode),
    showMp: flag(display.showMp, true),
    showShield: flag(display.showShield, true),
    hpLabel: opt(display.hpLabel),
    mpLabel: opt(display.mpLabel),
    statTarget: yes(display.statTarget),
  };
}

const readGm = gm => ({ name: opt(gm.name), connected: yes(gm.connected) });
const readAmbiance = ambiance => ({ weather: opt(ambiance.weather), time: opt(ambiance.time) });

function readPrimary(primary) {
  if (primary.kind !== 'roll' && primary.kind !== 'endTurn') return null;
  return {
    kind: primary.kind,
    statName: opt(primary.statName),
    canEndTurn: yes(primary.canEndTurn),
    groupTurn: yes(primary.groupTurn),
    hold: HOLDS.has(primary.hold) ? primary.hold : HOLD.none,
  };
}

function readRequest(request) {
  return {
    statId: opt(request.statId),
    statName: str(request.statName),
    threshold: int(request.threshold),
    missing: yes(request.missing),
  };
}

function readMe(me) {
  return {
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
  };
}

function readPlayer(p) {
  return {
    name: str(p.name),
    hash: str(p.hash),
    connected: yes(p.connected),
    hp: int(p.hp),
    hpMax: int(p.hpMax),
    shield: int(p.shield),
    coGm: yes(p.coGm),
  };
}

function readEntry(e) {
  return {
    name: str(e.name),
    initiative: int(e.initiative),
    acted: yes(e.acted),
    active: yes(e.active),
    mine: yes(e.mine),
    kind: ENTRY_KINDS.has(e.kind) ? e.kind : KIND.other,
    ref: opt(e.ref),
    group: opt(e.group),
    groupLabel: opt(e.groupLabel),
    roll: int(e.roll),
    mod: int(e.mod),
    stat: opt(e.stat),
  };
}

function readTurn(turn) {
  return {
    round: int(turn.round),
    acted: int(turn.acted),
    total: int(turn.total),
    diceMax: int(turn.diceMax),
    activeIndex: Number.isInteger(turn.activeIndex) ? turn.activeIndex : -1,
    turnOf: opt(turn.turnOf),
    entries: list(turn.entries).map(obj).map(readEntry),
    alsoOnField: {
      field: list(obj(turn.alsoOnField).field).filter(x => typeof x === 'string'),
      players: list(obj(turn.alsoOnField).players).filter(x => typeof x === 'string'),
    },
  };
}

export function readState(m) {
  return {
    seq: int(m.seq),
    access: m.access === 'player' ? 'player' : 'none',
    caps: list(m.caps).filter(x => OPS.includes(x)),
    room: opt(m.room),
    view: VIEWS.has(m.view) ? m.view : VIEW.offline,
    combatPresent: yes(m.combatPresent),
    detail: readDetail(obj(m.viewDetail)),
    link: readLink(obj(m.link)),
    display: readDisplay(obj(m.display)),
    gm: m.gm ? readGm(obj(m.gm)) : null,
    template: opt(m.template),
    lowerIsBetter: yes(m.lowerIsBetter),
    primary: m.primary ? readPrimary(obj(m.primary)) : null,
    request: m.request ? readRequest(obj(m.request)) : null,
    announcement: opt(m.announcement),
    ambiance: m.ambiance ? readAmbiance(obj(m.ambiance)) : null,
    me: m.me ? readMe(obj(m.me)) : null,
    field: list(m.field).map(obj).map(readField).filter(f => f.ref !== '' && (f.kind === KIND.npc || WAYMARKS.has(f.ref))),
    players: list(m.players).map(obj).map(readPlayer),
    turn: m.turn ? readTurn(obj(m.turn)) : null,
    rolls: list(m.rolls).slice(0, 20).map(obj).map(readRoll),
    lastRoll: m.lastRoll ? readLastRoll(obj(m.lastRoll)) : null,
  };
}
