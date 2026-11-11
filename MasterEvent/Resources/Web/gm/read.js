import { ATTITUDES, HEX_COLOR, KIND_MARKER, KIND_NPC, KIND_PLAYER, LINK_STATES, OPS, PERCENT, WAYMARKS } from './constants.js';
import { flag, int, list, num, obj, opt, str, yes } from './util.js';

const attitude = x => (ATTITUDES.has(x) ? x : 'neutral');
const mode = x => (x === PERCENT ? PERCENT : 'points');

const readStats = x => list(x).map(obj).map(s => ({ id: str(s.id), name: str(s.name), mod: int(s.mod) }));
const readCounters = x => list(x).map(obj).map(c => ({
  name: str(c.name),
  value: int(c.value),
  max: int(c.max),
  color: HEX_COLOR.test(str(c.color)) ? c.color : null,
}));

const readLink = link => ({
  state: LINK_STATES.has(link.state) ? link.state : 'offline',
  lobbyCode: opt(link.lobbyCode),
  onlinePlayers: int(link.onlinePlayers),
  suspended: yes(link.suspended),
  partyShown: int(link.partyShown),
  partyOnline: int(link.partyOnline),
});

const readDisplay = display => ({
  hpMode: mode(display.hpMode),
  mpMode: mode(display.mpMode),
  showMp: flag(display.showMp, true),
  showShield: flag(display.showShield, true),
  hpLabel: opt(display.hpLabel),
  mpLabel: opt(display.mpLabel),
  statTarget: yes(display.statTarget),
});

const readPlayer = p => ({
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
});

const readMarker = x => ({
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
});

const readNpc = x => ({
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
});

const readTurnEntry = e => ({
  name: str(e.name),
  kind: [KIND_PLAYER, KIND_MARKER, KIND_NPC].includes(e.kind) ? e.kind : KIND_PLAYER,
  ref: opt(e.ref),
  initiative: int(e.initiative),
  roll: int(e.roll),
  mod: int(e.mod),
  stat: opt(e.stat),
  hasActed: yes(e.hasActed),
  groupId: opt(e.groupId),
  group: opt(e.group),
});

const readTurn = turn => turn && {
  active: yes(turn.active),
  encounter: int(turn.encounter),
  round: int(turn.round),
  activeIndex: Number.isInteger(turn.activeIndex) ? turn.activeIndex : -1,
  diceMax: int(turn.diceMax),
  entries: list(turn.entries).map(obj).map(readTurnEntry),
};

const readPendingGroup = g => ({
  label: opt(g.label),
  grouped: yes(g.grouped),
  members: list(g.members).map(obj).map(x => ({ name: str(x.name), hash: str(x.hash) })).filter(x => x.hash !== ''),
});

const readRoll = r => ({
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
});

export function readState(m) {
  const ambiance = obj(m.ambiance);
  const primary = m.primary ? obj(m.primary) : null;
  const rules = m.rules ? obj(m.rules) : null;

  return {
    seq: int(m.seq),
    access: ['gm', 'coGm', 'none'].includes(m.access) ? m.access : 'none',
    caps: list(m.caps).filter(x => OPS.includes(x)),
    rules: rules && { diceMax: int(rules.diceMax), lowerIsBetter: yes(rules.lowerIsBetter) },
    link: readLink(obj(m.link)),
    display: readDisplay(obj(m.display)),
    primary: primary && ['start', 'endTurn', 'nextRound'].includes(primary.kind)
      ? { kind: primary.kind, name: str(primary.name) }
      : null,
    party: list(m.party).map(obj).map(readPlayer),
    markers: list(m.markers).map(obj).filter(x => WAYMARKS.has(x.id)).map(readMarker),
    npcs: list(m.npcs).map(obj).map(readNpc),
    turn: readTurn(m.turn ? obj(m.turn) : null),
    pending: list(m.pending).map(obj).map(readPendingGroup).filter(g => g.members.length > 0),
    rolls: list(m.rolls).slice(0, 20).map(obj).map(readRoll),
    ambiance: { weather: opt(ambiance.weather), time: opt(ambiance.time) },
  };
}
