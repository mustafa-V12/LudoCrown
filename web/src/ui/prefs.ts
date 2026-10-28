// Per-device preferences and local statistics (browser storage; everything works without it).
import type { BotDifficulty } from '../engine/bots';
import type { GameMode } from '../engine/model';
import { AVATARS } from './avatars';

export interface Prefs {
  name: string;
  avatar: string;
  difficulty: BotDifficulty;
  mode: GameMode;
  autoMove: boolean;
}

export interface Stats {
  played: number;
  wins: number;
}

const PREFS_KEY = 'ludocrown.prefs.v2';
const STATS_KEY = 'ludocrown.stats.v1';
const DEFAULT: Prefs = { name: 'You', avatar: 'king', difficulty: 'Normal', mode: 'Classic', autoMove: true };

function read<T>(key: string): Partial<T> {
  try {
    const raw = localStorage.getItem(key);
    return raw ? (JSON.parse(raw) as Partial<T>) : {};
  } catch {
    return {};
  }
}

function write(key: string, value: unknown): void {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    /* storage unavailable */
  }
}

export function loadPrefs(): Prefs {
  const p = { ...DEFAULT, ...read<Prefs>(PREFS_KEY) };
  if (!AVATARS.some((a) => a.id === p.avatar)) p.avatar = DEFAULT.avatar;
  if (!['Easy', 'Normal', 'Hard'].includes(p.difficulty)) p.difficulty = DEFAULT.difficulty;
  if (p.mode !== 'Quick') p.mode = 'Classic';
  p.name = String(p.name || DEFAULT.name).trim().slice(0, 14) || DEFAULT.name;
  p.autoMove = p.autoMove !== false;
  return p;
}

export const savePrefs = (p: Prefs): void => write(PREFS_KEY, p);

export function loadStats(): Stats {
  const s = read<Stats>(STATS_KEY);
  return { played: Math.max(0, Number(s.played) || 0), wins: Math.max(0, Number(s.wins) || 0) };
}

export function recordResult(won: boolean): Stats {
  const s = loadStats();
  const next = { played: s.played + 1, wins: s.wins + (won ? 1 : 0) };
  write(STATS_KEY, next);
  return next;
}
