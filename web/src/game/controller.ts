// Runs an offline game in the browser: humans (pass-and-play) and computer opponents, saved after every change.
// Mirrors LudoCrown.App.Logic/Game/OfflineGameSession.cs (bot delays, auto-move of a single legal move).
import { type Bot, type BotDifficulty, createBot } from '../engine/bots';
import { type EngineResult, type GameEvent, type LegalMove, applyRoll, forfeitTurn, getLegalMoves, move, rollDie } from '../engine/engine';
import { FINISHED_PROGRESS, YARD } from '../engine/geometry';
import { type GameMode, type GameState, AllModes, currentPlayer, newGame, rulesForMode } from '../engine/model';

export interface SeatConfig {
  name: string;
  bot: boolean;
  difficulty: BotDifficulty;
}

export interface GameView {
  /** Draw the current state. `legal` is non-empty only when a local human must choose a piece. */
  render(state: GameState, legal: readonly LegalMove[], canRoll: boolean): void;
  /** Animate the change from `prev` to `result.state` (dice, piece hops, captures). Resolves when done. */
  animate(prev: GameState, result: EngineResult): Promise<void>;
  notice(text: string): void;
  ended(state: GameState, seats: readonly SeatConfig[]): void;
}

export const BOT_ROLL_DELAY = 750;
export const BOT_MOVE_DELAY = 650;
export const AUTO_MOVE_DELAY = 450;
const SAVE_KEY = 'ludocrown.offline.v1';

export interface SavedGame {
  v: 1;
  state: GameState;
  seats: SeatConfig[];
  savedAt: string;
}

export class GameController {
  private busy = false;
  private stopped = false;
  private readonly bots = new Map<number, Bot>();

  constructor(
    private state: GameState,
    readonly seats: readonly SeatConfig[],
    private readonly view: GameView,
    private readonly options: { autoMoveSingle: boolean; delays?: boolean } = { autoMoveSingle: true },
  ) {
    seats.forEach((s, i) => {
      if (s.bot) this.bots.set(i, createBot(s.difficulty));
    });
  }

  static create(seats: readonly SeatConfig[], mode: GameMode, view: GameView, options?: { autoMoveSingle: boolean; delays?: boolean }): GameController {
    const id = 'web-' + Math.random().toString(36).slice(2, 10);
    const state = newGame(id, seats.map((s) => ({ name: s.name, kind: s.bot ? 'bot' : 'human' })), rulesForMode(mode));
    return new GameController(state, seats, view, options);
  }

  get current(): GameState {
    return this.state;
  }

  start(): void {
    this.publish();
    void this.pump();
  }

  stop(): void {
    this.stopped = true;
  }

  isLocalHumanTurn(): boolean {
    return this.state.phase !== 'Finished' && !this.bots.has(currentPlayer(this.state).seat);
  }

  /** The local human taps the die. */
  async humanRoll(): Promise<void> {
    if (this.busy || !this.isLocalHumanTurn() || this.state.phase !== 'AwaitingRoll') return;
    await this.run(async () => {
      await this.apply(applyRoll(this.state, currentPlayer(this.state).seat, rollDie()));
      await this.autoMoveIfSingle();
    });
  }

  /** The local human taps a piece. Ignored unless it is a legal move. */
  async humanMove(piece: number): Promise<void> {
    if (this.busy || !this.isLocalHumanTurn() || this.state.phase !== 'AwaitingMove') return;
    if (!getLegalMoves(this.state).some((m) => m.piece === piece)) return;
    await this.run(() => this.apply(move(this.state, currentPlayer(this.state).seat, piece)));
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy = true;
    try {
      await action();
    } finally {
      this.busy = false;
      this.publish();
    }
    void this.pump();
  }

  private async autoMoveIfSingle(): Promise<void> {
    if (!this.options.autoMoveSingle || this.state.phase !== 'AwaitingMove' || !this.isLocalHumanTurn()) return;
    const moves = getLegalMoves(this.state);
    if (moves.length !== 1) return;
    this.publish();
    await this.wait(AUTO_MOVE_DELAY);
    if (this.stopped) return;
    await this.apply(move(this.state, currentPlayer(this.state).seat, moves[0].piece));
  }

  /** Plays computer turns until it is a human's turn or the game ends. */
  private async pump(): Promise<void> {
    if (this.busy || this.stopped) return;
    this.busy = true;
    try {
      while (!this.stopped && this.state.phase !== 'Finished') {
        const seat = currentPlayer(this.state).seat;
        const bot = this.bots.get(seat);
        if (!bot) break;
        this.publish();
        if (this.state.phase === 'AwaitingRoll') {
          await this.wait(BOT_ROLL_DELAY);
          if (this.stopped) return;
          await this.apply(applyRoll(this.state, seat, rollDie()));
        } else {
          await this.wait(BOT_MOVE_DELAY);
          if (this.stopped) return;
          const choice = bot.chooseMove(this.state);
          await this.apply(choice === null ? forfeitTurn(this.state, seat) : move(this.state, seat, choice));
        }
      }
    } finally {
      this.busy = false;
      if (!this.stopped) this.publish();
    }
  }

  private async apply(result: EngineResult): Promise<void> {
    if (!result.ok) return;
    const prev = this.state;
    this.state = result.state;
    this.save();
    await this.view.animate(prev, result);
    for (const e of result.events) {
      const text = describe(e, this.state);
      if (text) this.view.notice(text);
    }
    if (this.state.phase === 'Finished') {
      clearSave();
      this.view.ended(this.state, this.seats);
    }
  }

  private publish(): void {
    const human = this.isLocalHumanTurn() && !this.busy;
    const legal = human ? getLegalMoves(this.state) : [];
    this.view.render(this.state, legal, human && this.state.phase === 'AwaitingRoll');
  }

  private wait(ms: number): Promise<void> {
    if (this.options.delays === false) return Promise.resolve();
    return new Promise((r) => setTimeout(r, ms));
  }

  private save(): void {
    if (this.state.phase === 'Finished') return;
    const data: SavedGame = { v: 1, state: this.state, seats: [...this.seats], savedAt: new Date().toISOString() };
    try {
      localStorage.setItem(SAVE_KEY, JSON.stringify(data));
    } catch {
      /* storage unavailable (private mode): the game simply isn't saved */
    }
  }
}

/** Event text, as in the app's GameText.Describe. */
export function describe(e: GameEvent, s: GameState): string {
  const name = (seat: number) => s.players[seat]?.name ?? `Player ${seat + 1}`;
  switch (e.type) {
    case 'NoLegalMoves': return `${name(e.seat)} rolled ${e.value} — no moves.`;
    case 'ThreeSixesForfeit': return `Three sixes! ${name(e.seat)}'s turn is forfeited.`;
    case 'PieceCaptured': return `${name(e.value)} captured ${name(e.seat)}!`;
    case 'PieceFinished': return s.phase === 'Finished' ? '' : `${name(e.seat)} crowned a piece!`;
    default: return '';
  }
}

export function clearSave(): void {
  try {
    localStorage.removeItem(SAVE_KEY);
  } catch {
    /* ignore */
  }
}

/** Loads a saved, unfinished game. Anything unreadable or inconsistent is discarded. */
export function loadSave(): SavedGame | null {
  let raw: string | null = null;
  try {
    raw = localStorage.getItem(SAVE_KEY);
  } catch {
    return null;
  }
  if (!raw) return null;
  try {
    const data = JSON.parse(raw) as SavedGame;
    if (!isValidSave(data)) throw new Error('invalid save');
    // Rules always come from the mode table, never from stored data.
    data.state.rules = rulesForMode(data.state.rules.mode);
    return data;
  } catch {
    clearSave();
    return null;
  }
}

function isValidSave(d: SavedGame): boolean {
  const s = d?.state;
  if (d?.v !== 1 || !s || !Array.isArray(d.seats) || !Array.isArray(s.players)) return false;
  if (s.players.length < 2 || s.players.length > 4 || d.seats.length !== s.players.length) return false;
  if (!AllModes.some((m) => m.mode === s.rules?.mode)) return false;
  if (!['AwaitingRoll', 'AwaitingMove'].includes(s.phase)) return false;
  if (!Number.isInteger(s.currentPlayerIndex) || s.currentPlayerIndex < 0 || s.currentPlayerIndex >= s.players.length) return false;
  if (s.phase === 'AwaitingMove' && !(Number.isInteger(s.pendingRoll) && s.pendingRoll! >= 1 && s.pendingRoll! <= 6)) return false;
  return s.players.every(
    (p, i) =>
      p.seat === i &&
      Array.isArray(p.pieces) &&
      p.pieces.length === 4 &&
      p.pieces.every((x) => Number.isInteger(x) && x >= YARD && x <= FINISHED_PROGRESS) &&
      typeof d.seats[i]?.name === 'string',
  );
}
