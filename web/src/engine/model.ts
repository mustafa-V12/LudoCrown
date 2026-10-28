// Port of LudoCrown.Core: Model (PlayerColor, RuleSet, GameModes, GameState, GameSetup).
// Keep in step with src/LudoCrown.Core. test/conformance.test.ts replays traces from the C# engine to prove it.

export enum PlayerColor {
  Ruby = 0,
  Emerald = 1,
  Topaz = 2,
  Sapphire = 3,
}

export type PlayerKind = 'human' | 'bot';
export type GamePhase = 'AwaitingRoll' | 'AwaitingMove' | 'Finished';
export type GameMode = 'Classic' | 'Quick';

export interface RuleSet {
  readonly mode: GameMode;
  /** Pieces a player must bring to the crown to win: 4 in Classic, 1 in Quick. */
  readonly piecesToWin: number;
  /** Gem rerolls per player per match (online only; the web version has no gems). */
  readonly maxRerolls: number;
  /** Boost bottles per player per match (0 = disabled; online casual only). */
  readonly maxBoosts: number;
  readonly entryRolls: readonly number[];
  readonly sixGrantsExtraTurn: boolean;
  readonly captureGrantsExtraTurn: boolean;
  readonly finishGrantsExtraTurn: boolean;
  readonly threeSixesForfeitsTurn: boolean;
}

const baseRules = {
  maxBoosts: 0,
  entryRolls: [6],
  sixGrantsExtraTurn: true,
  captureGrantsExtraTurn: true,
  finishGrantsExtraTurn: true,
  threeSixesForfeitsTurn: true,
} as const;

export interface GameModeInfo {
  readonly mode: GameMode;
  readonly name: string;
  readonly tagline: string;
  readonly winCondition: string;
  readonly description: string;
  readonly rules: RuleSet;
}

export const GameModes: Record<GameMode, GameModeInfo> = {
  Classic: {
    mode: 'Classic',
    name: 'Classic',
    tagline: 'The full royal race',
    winCondition: 'Bring all 4 pieces to the crown to win.',
    description:
      'Standard Ludo Crown rules. Enter with a 6, capture by landing on opponents, stars and start squares are safe. ' +
      'The first player with all four pieces on the crown wins.',
    rules: { ...baseRules, mode: 'Classic', piecesToWin: 4, maxRerolls: 7 },
  },
  Quick: {
    mode: 'Quick',
    name: 'Quick',
    tagline: 'First crown wins',
    winCondition: 'Bring just 1 piece to the crown to win.',
    description:
      'Same board and rules as Classic, but the match ends as soon as any player brings one piece to the crown. ' +
      'Great for short games.',
    rules: { ...baseRules, mode: 'Quick', piecesToWin: 1, maxRerolls: 5 },
  },
};

export const AllModes: readonly GameModeInfo[] = [GameModes.Classic, GameModes.Quick];

export function rulesForMode(mode: GameMode): RuleSet {
  return GameModes[mode].rules;
}

export interface PlayerState {
  seat: number;
  color: PlayerColor;
  name: string;
  kind: PlayerKind;
  /** Progress per piece: -1 yard, 0..50 track, 51..55 home column, 56 finished. */
  pieces: number[];
  rerollsUsed: number;
  boostsUsed: number;
  shieldActive: boolean;
}

export interface GameState {
  matchId: string;
  rules: RuleSet;
  players: PlayerState[];
  currentPlayerIndex: number;
  phase: GamePhase;
  pendingRoll: number | null;
  lastRoll: number | null;
  consecutiveSixes: number;
  sixesBeforePendingRoll: number;
  turnNumber: number;
  version: number;
  winnerSeat: number | null;
}

export const FINISHED = 56;

export const finishedCount = (p: PlayerState): number => p.pieces.filter((x) => x === FINISHED).length;
export const hasFinishedAll = (p: PlayerState): boolean => p.pieces.every((x) => x === FINISHED);
export const totalProgress = (p: PlayerState): number => p.pieces.reduce((s, x) => s + (x < 0 ? 0 : x + 1), 0);
export const currentPlayer = (s: GameState): PlayerState => s.players[s.currentPlayerIndex];
export const hasWon = (s: GameState, p: PlayerState): boolean => finishedCount(p) >= s.rules.piecesToWin;

export function cloneState(s: GameState): GameState {
  return {
    ...s,
    players: s.players.map((p) => ({ ...p, pieces: [...p.pieces] })),
  };
}

/** Seats ordered by final placement: winner first, then pieces crowned, then progress, then seat. */
export function standings(s: GameState): number[] {
  return [...s.players]
    .sort(
      (a, b) =>
        Number(b.seat === s.winnerSeat) - Number(a.seat === s.winnerSeat) ||
        finishedCount(b) - finishedCount(a) ||
        totalProgress(b) - totalProgress(a) ||
        a.seat - b.seat,
    )
    .map((p) => p.seat);
}

export interface SeatSetup {
  name: string;
  kind: PlayerKind;
}

/** Two players sit opposite (Ruby vs Topaz); three use Ruby, Emerald, Topaz; four use every colour. */
export function newGame(matchId: string, seats: readonly SeatSetup[], rules: RuleSet): GameState {
  if (seats.length < 2 || seats.length > 4) throw new Error('Ludo Crown supports 2 to 4 players.');
  const colors =
    seats.length === 2
      ? [PlayerColor.Ruby, PlayerColor.Topaz]
      : seats.length === 3
        ? [PlayerColor.Ruby, PlayerColor.Emerald, PlayerColor.Topaz]
        : [PlayerColor.Ruby, PlayerColor.Emerald, PlayerColor.Topaz, PlayerColor.Sapphire];
  return {
    matchId,
    rules,
    players: seats.map((s, i) => ({
      seat: i,
      color: colors[i],
      name: s.name,
      kind: s.kind,
      pieces: [-1, -1, -1, -1],
      rerollsUsed: 0,
      boostsUsed: 0,
      shieldActive: false,
    })),
    currentPlayerIndex: 0,
    phase: 'AwaitingRoll',
    pendingRoll: null,
    lastRoll: null,
    consecutiveSixes: 0,
    sixesBeforePendingRoll: 0,
    turnNumber: 1,
    version: 0,
    winnerSeat: null,
  };
}
