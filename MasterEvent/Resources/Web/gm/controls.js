import { KIND_MARKER, KIND_NPC, KIND_PLAYER, OP_ROLL, OP_ROLL_REQUEST, OP_VITALS, PERCENT } from './constants.js';
import { isBusy } from './commands.js';
import { ARIA_LABEL, control, icon, setAttr, setBusy, setData } from './dom.js';
import { fmt, t } from './i18n.js';
import { app, can, isNamed } from './state.js';

// ── Commandes des lignes ──

export function tickSpec(c) {
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
      setAttr(button, ARIA_LABEL, label);
      button.title = label;
      setData(button, { group: c.group ?? null, kind: c.kind ?? null, ref: c.ref ?? null, name: c.name });
    },
  };
}

// Bulles des boutons - et + : celles des PV reprennent les textes de l'onglet Scène.
const STEP_TIPS = {
  hp: { down: 'Scene.HpDown', downPct: 'Scene.HpDownPct', up: 'Scene.HpUp', upPct: 'Scene.HpUpPct' },
  other: { down: 'Web.StepDown', downPct: 'Web.StepDownPct', up: 'Web.StepUp', upPct: 'Web.StepUpPct' },
};

export function stepSpecs(c) {
  if (!c) return [];
  const tips = c.field === 'hp' ? STEP_TIPS.hp : STEP_TIPS.other;
  const down = c.pct ? tips.downPct : tips.down;
  const up = c.pct ? tips.upPct : tips.up;
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
      setAttr(button, ARIA_LABEL, fmt('Web.NoticeFmt', c.name, tip));
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
      setAttr(button, ARIA_LABEL, fmt('Web.NoticeFmt', c.name, tip));
      setBusy(button, c.busy, c.unnamed);
      setAttr(button, 'aria-haspopup', c.menu ? 'menu' : null);
      setData(button, { kind: c.kind, ref: c.ref });
    },
  };
}

export const rowControls = c => ({
  lead: c.tick ? [tickSpec(c.tick)] : [],
  hp: [...(c.roll ? [diceSpec(c.roll)] : []), ...stepSpecs(c.hp)],
});

// Le nom ouvre le détail s'il y a quelque chose à y faire.
export function opens(kind) {
  return can(OP_VITALS) || (!app.privateHidden && (kind === KIND_PLAYER ? can(OP_ROLL_REQUEST) : can(OP_ROLL)));
}

export function hpCtl(kind, ref, name, d) {
  return can(OP_VITALS) ? { kind, ref, field: 'hp', pct: d.hpMode === PERCENT, name } : null;
}

// Le co-MJ ne reçoit pas les stats de ses marqueurs : ses jets partent toujours sans stat.
export function rollStats(entity) {
  return app.current?.access === 'gm' ? entity.stats.filter(s => s.id !== '') : [];
}

export const rollLock = (kind, ref) => 'roll:' + kind + ':' + ref;
export const actedLock = (group, kind, ref) => (group ? 'acted:g:' + group : 'acted:' + kind + ':' + ref);

export function rollCtl(kind, ref, entity, name) {
  if (!entity || !can(OP_ROLL) || app.privateHidden || kind === KIND_PLAYER) return null;
  if (kind === KIND_NPC && app.current.access !== 'gm') return null;
  return {
    kind, ref, name,
    unnamed: kind === KIND_MARKER && !isNamed(entity),
    busy: isBusy(rollLock(kind, ref)),
    menu: rollStats(entity).length > 0,
  };
}
