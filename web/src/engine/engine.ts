// Port of LudoCrown.Core/Engine (GameEngine, GameEvent, ErrorCode). Stateless and deterministic: every function
// takes a state and returns a new one, never mutating its input. Dice values come from outside.
import {
  FINISHED_PROGRESS,
  PIECES_PER_PLAYER,
  START_PROGRESS,
  YARD,
  absoluteIndex,
  isOnMainTrack,
  isSafeSquare,
} from './geometry';
import { type GamePhase, type GameState, type PlayerState, cloneState, currentPlayer, hasFinishedAll, hasWon } from './model';

export type GameEventType =
  | 'DiceRolled'
  | 'DiceRerolled'
  | 'NoLegalMoves'
  | 'ThreeSixesForfeit'
  | 'PieceEntered'
  | 'PieceMoved'
  | 'PieceCaptured'
  | 'PieceFinished'
  | 'ExtraTurn'
  | 'TurnPassed'
  | 'GameWon'
  | 'ShieldActivated'
  | 'ShieldExpired'
  | 'CaptureBlockedByShield';

export interface GameEvent {
  type: GameEventType;
  seat: number;
  piece: number;
  from: number;
  to: number;
  value: number;
}

const ev = (type: GameEventType, seat: number, piece = -1, from = 0, to = 0, value = 0): GameEvent => ({ type, seat, piece, from, to, value });

export type ErrorCode =
  | 'None'
  | 'NotYourTurn'
  | 'WrongPhase'
  | 'InvalidPiece'
  | 'IllegalMove'
  | 'GameOver'
  | 'NoRerollsLeft'
  | 'RerollNotAllowed'
  | 'BoostNotAllowed'
  | 'NoBoostsLeft';

export interface EngineResult {
  ok: boolean;
  state: GameState;
  events: GameEvent[];
  error: ErrorCode;
}

export interface LegalMove {
  piece: number;
  from: number;
  to: number;
  captures: boolean;
  finishes: boolean;
  enters: boolean;
}

const fail = (state: GameState, error: ErrorCode): EngineResult => ({ ok: false, state, events: [], error });

function done(next: GameState, events: GameEvent[]): EngineResult {
  next.version++;
  return { ok: true, state: next, events, error: 'None' };
}

function validateTurn(state: GameState, seat: number, expected: GamePhase): ErrorCode {
  if (state.phase === 'Finished') return 'GameOver';
  if (currentPlayer(state).seat !== seat) return 'NotYourTurn';
  if (state.phase !== expected) return 'WrongPhase';
  return 'None';
}

const checkDie = (value: number): void => {
  if (!Number.isInteger(value) || value < 1 || value > 6) throw new RangeError('die value');
};

/** Applies a die value for the current player. holdWhenNoMoves keeps a roll with no legal move pending (online rerolls). */
export function applyRoll(state: GameState, seat: number, value: number, holdWhenNoMoves = false): EngineResult {
  const error = validateTurn(state, seat, 'AwaitingRoll');
  if (error !== 'None') return fail(state, error);
  checkDie(value);
  const next = cloneState(state);
  const events: GameEvent[] = [ev('DiceRolled', seat, -1, 0, 0, value)];
  next.sixesBeforePendingRoll = state.consecutiveSixes;
  next.consecutiveSixes = value === 6 ? state.consecutiveSixes + 1 : 0;
  resolveRoll(next, seat, value, holdWhenNoMoves, events);
  return done(next, events);
}

/** Gem reroll (online feature, kept for rule parity): replaces the pending roll. */
export function applyReroll(state: GameState, seat: number, value: number, holdWhenNoMoves = false): EngineResult {
  const error = validateReroll(state, seat);
  if (error !== 'None') return fail(state, error);
  checkDie(value);
  const next = cloneState(state);
  const old = state.pendingRoll as number;
  currentPlayer(next).rerollsUsed++;
  next.consecutiveSixes = value === 6 ? state.sixesBeforePendingRoll + 1 : 0;
  const events: GameEvent[] = [ev('DiceRerolled', seat, -1, old, 0, value)];
  resolveRoll(next, seat, value, holdWhenNoMoves, events);
  return done(next, events);
}

export function validateReroll(state: GameState, seat: number): ErrorCode {
  if (state.phase === 'Finished') return 'GameOver';
  if (currentPlayer(state).seat !== seat) return 'NotYourTurn';
  if (state.phase !== 'AwaitingMove' || state.pendingRoll === null) return 'RerollNotAllowed';
  if (currentPlayer(state).rerollsUsed >= state.rules.maxRerolls) return 'NoRerollsLeft';
  return 'None';
}

/** Ends the turn when the pending roll has no legal move (after a held roll). */
export function pass(state: GameState, seat: number): EngineResult {
  const error = validateTurn(state, seat, 'AwaitingMove');
  if (error !== 'None') return fail(state, error);
  if (getLegalMoves(state).length > 0) return fail(state, 'IllegalMove');
  const next = cloneState(state);
  const events: GameEvent[] = [ev('NoLegalMoves', seat, -1, 0, 0, state.pendingRoll ?? 0)];
  passTurn(next, events);
  return done(next, events);
}

function resolveRoll(next: GameState, seat: number, value: number, holdWhenNoMoves: boolean, events: GameEvent[]): void {
  next.lastRoll = value;
  if (value === 6 && next.consecutiveSixes >= 3 && next.rules.threeSixesForfeitsTurn) {
    events.push(ev('ThreeSixesForfeit', seat, -1, 0, 0, value));
    passTurn(next, events);
    return;
  }
  next.pendingRoll = value;
  next.phase = 'AwaitingMove';
  if (getLegalMoves(next).length === 0 && !holdWhenNoMoves) {
    // Documented rule: with no legal move the turn passes, even after a 6.
    events.push(ev('NoLegalMoves', seat, -1, 0, 0, value));
    passTurn(next, events);
  }
}

/** Legal moves for the current player and pending roll. Empty when not awaiting a move. */
export function getLegalMoves(state: GameState): LegalMove[] {
  if (state.phase !== 'AwaitingMove' || state.pendingRoll === null) return [];
  const player = currentPlayer(state);
  const moves: LegalMove[] = [];
  for (let i = 0; i < player.pieces.length; i++) {
    const m = tryGetMove(state, player, i, state.pendingRoll);
    if (m) moves.push(m);
  }
  return moves;
}

/** Shield bottle (online casual feature, kept for rule parity). */
export function useShield(state: GameState, seat: number): EngineResult {
  const error = validateShield(state, seat);
  if (error !== 'None') return fail(state, error);
  const next = cloneState(state);
  const player = currentPlayer(next);
  player.shieldActive = true;
  player.boostsUsed++;
  return done(next, [ev('ShieldActivated', seat, -1, 0, 0, player.boostsUsed)]);
}

export function validateShield(state: GameState, seat: number): ErrorCode {
  if (state.phase === 'Finished') return 'GameOver';
  if (currentPlayer(state).seat !== seat) return 'NotYourTurn';
  if (state.rules.maxBoosts <= 0) return 'BoostNotAllowed';
  const player = currentPlayer(state);
  if (player.boostsUsed >= state.rules.maxBoosts) return 'NoBoostsLeft';
  if (state.phase !== 'AwaitingRoll' || player.shieldActive) return 'BoostNotAllowed';
  return 'None';
}

/** Moves a piece of the current player by the pending roll. */
export function move(state: GameState, seat: number, pieceIndex: number): EngineResult {
  const error = validateTurn(state, seat, 'AwaitingMove');
  if (error !== 'None') return fail(state, error);
  if (!Number.isInteger(pieceIndex) || pieceIndex < 0 || pieceIndex >= PIECES_PER_PLAYER) return fail(state, 'InvalidPiece');

  const roll = state.pendingRoll as number;
  const m = tryGetMove(state, currentPlayer(state), pieceIndex, roll);
  if (!m) return fail(state, 'IllegalMove');

  const next = cloneState(state);
  const player = currentPlayer(next);
  const events: GameEvent[] = [];
  player.pieces[pieceIndex] = m.to;
  events.push(ev(m.enters ? 'PieceEntered' : 'PieceMoved', seat, pieceIndex, m.from, m.to, roll));

  let captured = false;
  if (isOnMainTrack(m.to)) {
    const abs = absoluteIndex(player.color, m.to);
    if (!isSafeSquare(abs)) {
      for (const other of next.players) {
        if (other.seat === player.seat) continue;
        for (let j = 0; j < other.pieces.length; j++) {
          const p = other.pieces[j];
          if (isOnMainTrack(p) && absoluteIndex(other.color, p) === abs) {
            if (other.shieldActive) {
              events.push(ev('CaptureBlockedByShield', other.seat, j, p, p, player.seat));
              continue;
            }
            other.pieces[j] = YARD;
            captured = true;
            events.push(ev('PieceCaptured', other.seat, j, p, YARD, player.seat));
          }
        }
      }
    }
  }

  const finished = m.to === FINISHED_PROGRESS;
  if (finished) events.push(ev('PieceFinished', seat, pieceIndex, m.from, m.to));

  next.pendingRoll = null;

  if (hasWon(next, player)) {
    next.phase = 'Finished';
    next.winnerSeat = player.seat;
    events.push(ev('GameWon', seat));
    return done(next, events);
  }

  const extra =
    (roll === 6 && next.rules.sixGrantsExtraTurn) ||
    (captured && next.rules.captureGrantsExtraTurn) ||
    (finished && next.rules.finishGrantsExtraTurn);

  if (extra) {
    if (roll !== 6) next.consecutiveSixes = 0; // only an unbroken run of sixes counts toward the forfeit
    next.phase = 'AwaitingRoll';
    events.push(ev('ExtraTurn', seat));
  } else {
    passTurn(next, events);
  }
  return done(next, events);
}

/** Passes the turn without moving (used for timeouts); works in either phase. */
export function forfeitTurn(state: GameState, seat: number): EngineResult {
  if (state.phase === 'Finished') return fail(state, 'GameOver');
  if (currentPlayer(state).seat !== seat) return fail(state, 'NotYourTurn');
  const next = cloneState(state);
  const events: GameEvent[] = [];
  passTurn(next, events);
  return done(next, events);
}

function tryGetMove(state: GameState, player: PlayerState, pieceIndex: number, roll: number): LegalMove | null {
  const from = player.pieces[pieceIndex];
  if (from === FINISHED_PROGRESS) return null;
  if (from === YARD) {
    if (!state.rules.entryRolls.includes(roll)) return null;
    return { piece: pieceIndex, from, to: START_PROGRESS, captures: wouldCapture(state, player, START_PROGRESS), finishes: false, enters: true };
  }
  const to = from + roll;
  if (to > FINISHED_PROGRESS) return null; // exact roll needed to finish
  return { piece: pieceIndex, from, to, captures: wouldCapture(state, player, to), finishes: to === FINISHED_PROGRESS, enters: false };
}

function wouldCapture(state: GameState, player: PlayerState, to: number): boolean {
  if (!isOnMainTrack(to)) return false;
  const abs = absoluteIndex(player.color, to);
  if (isSafeSquare(abs)) return false;
  return state.players.some(
    (o) => o.seat !== player.seat && !o.shieldActive && o.pieces.some((p) => isOnMainTrack(p) && absoluteIndex(o.color, p) === abs),
  );
}

function passTurn(next: GameState, events: GameEvent[]): void {
  const from = currentPlayer(next).seat;
  next.pendingRoll = null;
  next.consecutiveSixes = 0;
  next.phase = 'AwaitingRoll';
  const count = next.players.length;
  for (let step = 1; step <= count; step++) {
    const candidate = (next.currentPlayerIndex + step) % count;
    if (!hasFinishedAll(next.players[candidate])) {
      next.currentPlayerIndex = candidate;
      break;
    }
  }
  next.turnNumber++;
  events.push(ev('TurnPassed', from, -1, 0, 0, currentPlayer(next).seat));
  const now = currentPlayer(next);
  if (now.shieldActive) {
    // The shield lasts until its owner's next turn begins.
    now.shieldActive = false;
    events.push(ev('ShieldExpired', now.seat));
  }
}

/** Unbiased die value 1..6 from the browser's cryptographic random source. */
export function rollDie(): number {
  const buf = new Uint8Array(1);
  // Rejection sampling: 252 is the largest multiple of 6 below 256.
  for (;;) {
    crypto.getRandomValues(buf);
    if (buf[0] < 252) return (buf[0] % 6) + 1;
  }
}
