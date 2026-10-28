// Port of LudoCrown.Core/Board/BoardGeometry.cs: board math shared by the rules engine and the renderer.
// Progress: -1 yard, 0..50 main track (0 = own start square), 51..55 home column, 56 finished.
import { PlayerColor } from './model';

export interface GridPoint {
  x: number;
  y: number;
}

export const GRID_SIZE = 15;
export const TRACK_LENGTH = 52;
export const PIECES_PER_PLAYER = 4;
export const YARD = -1;
export const START_PROGRESS = 0;
export const LAST_TRACK_PROGRESS = 50;
export const HOME_COLUMN_START = 51;
export const FINISHED_PROGRESS = 56;
export const QUARTER_LENGTH = 13;

export const STAR_SQUARES: ReadonlySet<number> = new Set([8, 21, 34, 47]);
export const START_SQUARES: ReadonlySet<number> = new Set([0, 13, 26, 39]);

/** Rotates 90° clockwise around the board centre (screen coordinates, y grows down). */
export const rotateClockwise = (p: GridPoint): GridPoint => ({ x: GRID_SIZE - 1 - p.y, y: p.x });

function rotate(p: GridPoint, color: PlayerColor): GridPoint {
  let r = p;
  for (let i = 0; i < color; i++) r = rotateClockwise(r);
  return r;
}

const TRACK_CELLS: readonly GridPoint[] = (() => {
  const quarter: GridPoint[] = [
    { x: 6, y: 13 }, { x: 6, y: 12 }, { x: 6, y: 11 }, { x: 6, y: 10 }, { x: 6, y: 9 },
    { x: 5, y: 8 }, { x: 4, y: 8 }, { x: 3, y: 8 }, { x: 2, y: 8 }, { x: 1, y: 8 }, { x: 0, y: 8 },
    { x: 0, y: 7 },
    { x: 0, y: 6 },
  ];
  const track: GridPoint[] = [];
  for (let q = 0; q < 4; q++) for (let i = 0; i < QUARTER_LENGTH; i++) track.push(rotate(quarter[i], q));
  return track;
})();

export const startIndex = (color: PlayerColor): number => color * QUARTER_LENGTH;

export function absoluteIndex(color: PlayerColor, progress: number): number {
  if (progress < START_PROGRESS || progress > LAST_TRACK_PROGRESS) throw new RangeError('Only main-track progress has an absolute index.');
  return (startIndex(color) + progress) % TRACK_LENGTH;
}

export const isOnMainTrack = (progress: number): boolean => progress >= START_PROGRESS && progress <= LAST_TRACK_PROGRESS;
export const isSafeSquare = (abs: number): boolean => STAR_SQUARES.has(abs) || START_SQUARES.has(abs);

export function trackCell(abs: number): GridPoint {
  if (abs < 0 || abs >= TRACK_LENGTH) throw new RangeError('absoluteIndex');
  return TRACK_CELLS[abs];
}

export function homeColumnCell(color: PlayerColor, step: number): GridPoint {
  if (step < 0 || step > 4) throw new RangeError('step');
  return rotate({ x: 7, y: 13 - step }, color);
}

export const finishPoint = (color: PlayerColor): GridPoint => rotate({ x: 7, y: 8.1 }, color);

const YARD_SLOTS: readonly GridPoint[] = [
  { x: 1.6, y: 10.6 }, { x: 3.4, y: 10.6 }, { x: 1.6, y: 12.4 }, { x: 3.4, y: 12.4 },
];

export function yardSlot(color: PlayerColor, piece: number): GridPoint {
  if (piece < 0 || piece >= PIECES_PER_PLAYER) throw new RangeError('piece');
  return rotate(YARD_SLOTS[piece], color);
}

export function piecePosition(color: PlayerColor, piece: number, progress: number): GridPoint {
  if (progress === YARD) return yardSlot(color, piece);
  if (progress >= START_PROGRESS && progress <= LAST_TRACK_PROGRESS) return trackCell(absoluteIndex(color, progress));
  if (progress >= HOME_COLUMN_START && progress < FINISHED_PROGRESS) return homeColumnCell(color, progress - HOME_COLUMN_START);
  if (progress === FINISHED_PROGRESS) return finishPoint(color);
  throw new RangeError('progress');
}

export function yardOrigin(color: PlayerColor): GridPoint {
  switch (color) {
    case PlayerColor.Ruby: return { x: 0, y: 9 };
    case PlayerColor.Emerald: return { x: 0, y: 0 };
    case PlayerColor.Topaz: return { x: 9, y: 0 };
    case PlayerColor.Sapphire: return { x: 9, y: 9 };
  }
}
