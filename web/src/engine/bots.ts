// Port of LudoCrown.Core/AI/Bots.cs. Bots only ever return a legal move.
import { HOME_COLUMN_START, LAST_TRACK_PROGRESS, TRACK_LENGTH, absoluteIndex, isOnMainTrack, isSafeSquare } from './geometry';
import { type LegalMove, getLegalMoves, move } from './engine';
import { type GameState, type PlayerColor, currentPlayer, hasFinishedAll } from './model';

export type BotDifficulty = 'Easy' | 'Normal' | 'Hard';

export interface BotWeights {
  finish: number;
  capture: number;
  capturedProgressFactor: number;
  enter: number;
  progressFactor: number;
  landOnSafe: number;
  enterHomeColumn: number;
  dangerPerThreat: number;
  escapePerThreat: number;
  noise: number;
}

export const NormalWeights: BotWeights = {
  finish: 100,
  capture: 60,
  capturedProgressFactor: 0.5,
  enter: 45,
  progressFactor: 1,
  landOnSafe: 20,
  enterHomeColumn: 25,
  dangerPerThreat: 0,
  escapePerThreat: 0,
  noise: 2,
};

export const HardWeights: BotWeights = { ...NormalWeights, dangerPerThreat: 18, escapePerThreat: 14, noise: 0.5 };

export interface Bot {
  /** Index of the piece to move, or null if there are no legal moves. */
  chooseMove(state: GameState): number | null;
}

export class RandomBot implements Bot {
  constructor(private readonly random: () => number = Math.random) {}
  chooseMove(state: GameState): number | null {
    const moves = getLegalMoves(state);
    return moves.length === 0 ? null : moves[Math.floor(this.random() * moves.length)].piece;
  }
}

export class HeuristicBot implements Bot {
  constructor(
    private readonly weights: BotWeights,
    private readonly random: () => number = Math.random,
  ) {}

  chooseMove(state: GameState): number | null {
    const moves = getLegalMoves(state);
    if (moves.length === 0) return null;
    if (moves.length === 1) return moves[0].piece;
    const scored = moves.map((m) => ({ piece: m.piece, score: this.score(state, m) + this.random() * this.weights.noise }));
    scored.sort((a, b) => b.score - a.score || a.piece - b.piece);
    return scored[0].piece;
  }

  score(state: GameState, m: LegalMove): number {
    const w = this.weights;
    const player = currentPlayer(state);
    let score = 0;
    if (m.finishes) score += w.finish;
    if (m.enters) score += w.enter;
    if (m.captures) {
      score += w.capture;
      score += capturedProgress(state, player.color, player.seat, m.to) * w.capturedProgressFactor;
    }
    if (!m.enters) score += (m.to - m.from) * w.progressFactor;
    if (m.from <= LAST_TRACK_PROGRESS && m.to >= HOME_COLUMN_START && !m.finishes) score += w.enterHomeColumn;
    if (isOnMainTrack(m.to) && isSafeSquare(absoluteIndex(player.color, m.to))) score += w.landOnSafe;

    if (w.dangerPerThreat > 0 || w.escapePerThreat > 0) {
      // Simulate the move so captured opponents no longer count as threats.
      const after = move(state, player.seat, m.piece);
      const afterState = after.ok ? after.state : state;
      score -= threatsAt(afterState, player.color, player.seat, m.to) * w.dangerPerThreat;
      if (!m.enters) score += threatsAt(state, player.color, player.seat, m.from) * w.escapePerThreat;
    }
    return score;
  }
}

function capturedProgress(state: GameState, color: PlayerColor, seat: number, to: number): number {
  const abs = absoluteIndex(color, to);
  let sum = 0;
  for (const o of state.players) {
    if (o.seat === seat) continue;
    for (const p of o.pieces) if (isOnMainTrack(p) && absoluteIndex(o.color, p) === abs) sum += p;
  }
  return sum;
}

/** Opponent pieces that could land on this progress square with one roll (1–6). */
export function threatsAt(state: GameState, color: PlayerColor, seat: number, progress: number): number {
  if (!isOnMainTrack(progress)) return 0;
  const abs = absoluteIndex(color, progress);
  if (isSafeSquare(abs)) return 0;
  let threats = 0;
  for (const o of state.players) {
    if (o.seat === seat || hasFinishedAll(o)) continue;
    for (const p of o.pieces) {
      if (!isOnMainTrack(p)) continue;
      const oAbs = absoluteIndex(o.color, p);
      const distance = (abs - oAbs + TRACK_LENGTH) % TRACK_LENGTH;
      if (distance >= 1 && distance <= 6 && p + distance <= LAST_TRACK_PROGRESS) threats++;
    }
  }
  return threats;
}

export function createBot(difficulty: BotDifficulty, random: () => number = Math.random): Bot {
  switch (difficulty) {
    case 'Easy': return new RandomBot(random);
    case 'Normal': return new HeuristicBot(NormalWeights, random);
    case 'Hard': return new HeuristicBot(HardWeights, random);
  }
}
