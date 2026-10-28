// Draws the Ludo Crown board on a <canvas>: a port of src/LudoCrown.Mobile/Controls/BoardDrawable.cs (velvet frame,
// ivory track, jewel yards, gold stars, crown centre, glossy gem pieces) plus piece animation and tap hit-testing.
// Purely visual: it reads game state and never changes it.
import {
  GRID_SIZE,
  type GridPoint,
  QUARTER_LENGTH,
  STAR_SQUARES,
  START_SQUARES,
  TRACK_LENGTH,
  homeColumnCell,
  piecePosition,
  startIndex,
  trackCell,
  yardOrigin,
  yardSlot,
} from '../engine/geometry';
import type { GameEvent } from '../engine/engine';
import { type GameState, PlayerColor } from '../engine/model';
import { Palette, dark, homeStep, light, muted, of } from './palette';

interface Sprite {
  seat: number;
  piece: number;
  color: PlayerColor;
  pos: GridPoint;
  scale: number;
}

const ALL_COLORS = [PlayerColor.Ruby, PlayerColor.Emerald, PlayerColor.Topaz, PlayerColor.Sapphire];
const HOP_MS = 120;
const CAPTURE_MS = 380;

export class BoardRenderer {
  private readonly ctx: CanvasRenderingContext2D;
  private sprites: Sprite[] = [];
  private state: GameState | null = null;
  private legal = new Set<number>();
  private size = 0;
  private dpr = 1;
  private cache: HTMLCanvasElement | null = null;
  private frame = 0;
  private animating = 0;
  private readonly reduceMotion = typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;

  constructor(private readonly canvas: HTMLCanvasElement) {
    const ctx = canvas.getContext('2d');
    if (!ctx) throw new Error('Canvas is not supported.');
    this.ctx = ctx;
    new ResizeObserver(() => this.resize()).observe(canvas);
    this.resize();
  }

  /** Shows a state immediately (no animation). */
  setState(state: GameState, legal: readonly number[]): void {
    const colorsChanged = this.state?.players.length !== state.players.length;
    this.state = state;
    this.legal = new Set(legal);
    if (colorsChanged) this.cache = null;
    if (this.animating === 0) this.syncSprites();
    this.requestDraw();
  }

  /** Animates the pieces from `prev` to `next` following the engine events, then settles on `next`. */
  async animate(prev: GameState, next: GameState, events: readonly GameEvent[]): Promise<void> {
    this.state = prev;
    this.legal.clear();
    this.syncSprites();
    if (!this.reduceMotion) {
      this.animating++;
      try {
        for (const e of events) {
          const player = prev.players[e.seat];
          if (!player) continue;
          if (e.type === 'PieceEntered') {
            await this.tween(e.seat, e.piece, [piecePosition(player.color, e.piece, 0)], HOP_MS * 2);
          } else if (e.type === 'PieceMoved') {
            const path: GridPoint[] = [];
            for (let p = e.from + 1; p <= e.to; p++) path.push(piecePosition(player.color, e.piece, p));
            await this.tween(e.seat, e.piece, path, HOP_MS);
          } else if (e.type === 'PieceCaptured') {
            await this.tween(e.seat, e.piece, [yardSlot(player.color, e.piece)], CAPTURE_MS);
          }
        }
      } finally {
        this.animating--;
      }
    }
    this.state = next;
    this.syncSprites();
    this.requestDraw();
  }

  /** Index of the current player's legal piece under a tap (client coordinates), or null. */
  hitTest(clientX: number, clientY: number): number | null {
    if (!this.state || this.legal.size === 0) return null;
    const rect = this.canvas.getBoundingClientRect();
    const x = clientX - rect.left, y = clientY - rect.top;
    const seat = this.state.players[this.state.currentPlayerIndex].seat;
    const cell = this.cell();
    let best: number | null = null;
    let bestDist = cell * 0.95; // generous target on a phone
    for (const s of this.sprites) {
      if (s.seat !== seat || !this.legal.has(s.piece)) continue;
      const p = this.toScreen(s.pos);
      const d = Math.hypot(p.x - x, p.y - y);
      if (d < bestDist) {
        bestDist = d;
        best = s.piece;
      }
    }
    return best;
  }

  private syncSprites(): void {
    if (!this.state) return;
    this.sprites = this.state.players.flatMap((p) =>
      p.pieces.map((progress, i) => ({ seat: p.seat, piece: i, color: p.color, pos: piecePosition(p.color, i, progress), scale: 1 })),
    );
  }

  private tween(seat: number, piece: number, path: readonly GridPoint[], stepMs: number): Promise<void> {
    const sprite = this.sprites.find((s) => s.seat === seat && s.piece === piece);
    if (!sprite || path.length === 0) return Promise.resolve();
    return new Promise((resolve) => {
      let index = 0;
      let from = { ...sprite.pos };
      let start = performance.now();
      const tick = (now: number) => {
        const t = Math.min(1, (now - start) / stepMs);
        const to = path[index];
        const e = t < 0.5 ? 2 * t * t : 1 - (-2 * t + 2) ** 2 / 2;
        sprite.pos = { x: from.x + (to.x - from.x) * e, y: from.y + (to.y - from.y) * e };
        sprite.scale = 1 + 0.22 * Math.sin(Math.PI * t);
        this.draw();
        if (t < 1) {
          requestAnimationFrame(tick);
          return;
        }
        sprite.pos = { ...to };
        sprite.scale = 1;
        index++;
        if (index >= path.length) {
          this.draw();
          resolve();
          return;
        }
        from = { ...to };
        start = now;
        requestAnimationFrame(tick);
      };
      requestAnimationFrame(tick);
    });
  }

  private resize(): void {
    const rect = this.canvas.getBoundingClientRect();
    const size = Math.floor(Math.min(rect.width, rect.height));
    const dpr = Math.min(3, window.devicePixelRatio || 1);
    if (size <= 0 || (size === this.size && dpr === this.dpr)) return;
    this.size = size;
    this.dpr = dpr;
    this.canvas.width = Math.round(size * dpr);
    this.canvas.height = Math.round(size * dpr);
    this.cache = null;
    this.requestDraw();
  }

  private requestDraw(): void {
    if (this.frame) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = 0;
      this.draw();
      // Keep pulsing while the player has pieces to choose from, or a turn highlight to show.
      if (this.legal.size > 0 || (this.state && this.state.phase !== 'Finished')) this.schedulePulse();
    });
  }

  private pulseTimer = 0;
  private schedulePulse(): void {
    if (this.pulseTimer) return;
    this.pulseTimer = window.setTimeout(() => {
      this.pulseTimer = 0;
      if (this.animating === 0) this.requestDraw();
    }, this.legal.size > 0 ? 33 : 100);
  }

  private pulse(): number {
    return this.reduceMotion ? 0.6 : (Math.sin(performance.now() / 260) + 1) / 2;
  }

  private margin(): number {
    return 7;
  }

  private cell(): number {
    return (this.size - this.margin() * 2) / GRID_SIZE;
  }

  private toScreen(p: GridPoint): { x: number; y: number } {
    const c = this.cell();
    return { x: this.margin() + (p.x + 0.5) * c, y: this.margin() + (p.y + 0.5) * c };
  }

  private activeColors(): Set<PlayerColor> {
    return new Set(this.state ? this.state.players.map((p) => p.color) : ALL_COLORS);
  }

  private draw(): void {
    if (this.size <= 0) return;
    const ctx = this.ctx;
    ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    ctx.clearRect(0, 0, this.size, this.size);
    if (!this.cache) this.cache = this.renderStatic();
    ctx.drawImage(this.cache, 0, 0, this.size, this.size);
    this.drawTurnHighlight();
    this.drawPieces();
  }

  /** Everything that doesn't move, drawn once per size into an offscreen canvas. */
  private renderStatic(): HTMLCanvasElement {
    const off = document.createElement('canvas');
    off.width = this.canvas.width;
    off.height = this.canvas.height;
    const ctx = off.getContext('2d')!;
    ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    const c = this.cell();
    const m = this.margin();
    const boardSize = c * GRID_SIZE;

    // Frame
    ctx.save();
    ctx.shadowColor = 'rgba(0,0,0,0.45)';
    ctx.shadowBlur = 14;
    ctx.shadowOffsetY = 4;
    ctx.fillStyle = Palette.goldDeep;
    roundRect(ctx, m - 4, m - 4, boardSize + 8, boardSize + 8, 18);
    ctx.fill();
    ctx.restore();
    ctx.fillStyle = Palette.gold;
    roundRect(ctx, m - 1.5, m - 1.5, boardSize + 3, boardSize + 3, 16);
    ctx.fill();
    ctx.fillStyle = Palette.ivory;
    roundRect(ctx, m, m, boardSize, boardSize, 14);
    ctx.fill();

    const active = this.activeColors();
    for (const color of ALL_COLORS) this.drawYard(ctx, color, active.has(color), c);
    this.drawTrack(ctx, c);
    this.drawCentre(ctx, c, active);
    return off;
  }

  private drawYard(ctx: CanvasRenderingContext2D, color: PlayerColor, active: boolean, c: number): void {
    const o = yardOrigin(color);
    const m = this.margin();
    const x = m + o.x * c, y = m + o.y * c, s = 6 * c;
    ctx.fillStyle = active ? of(color) : muted(color);
    roundRect(ctx, x + c * 0.08, y + c * 0.08, s - c * 0.16, s - c * 0.16, c * 0.5);
    ctx.fill();

    const inset = c * 0.85;
    ctx.fillStyle = Palette.ivory;
    roundRect(ctx, x + inset, y + inset, s - inset * 2, s - inset * 2, c * 0.45);
    ctx.fill();
    ctx.strokeStyle = dark(color, 0.35);
    ctx.lineWidth = Math.max(1, c * 0.05);
    ctx.stroke();

    for (let i = 0; i < 4; i++) {
      const p = this.toScreen(yardSlot(color, i));
      ctx.beginPath();
      ctx.arc(p.x, p.y, c * 0.52, 0, Math.PI * 2);
      ctx.fillStyle = light(color, active ? 0.55 : 0.3);
      ctx.fill();
      ctx.strokeStyle = of(color, active ? 0.8 : 0.3);
      ctx.stroke();
    }
  }

  private drawTrack(ctx: CanvasRenderingContext2D, c: number): void {
    ctx.strokeStyle = Palette.line;
    ctx.lineWidth = Math.max(1, c * 0.04);
    for (let i = 0; i < TRACK_LENGTH; i++) {
      const cell = this.cellRect(trackCell(i), c);
      const startOf = START_SQUARES.has(i) ? (Math.floor(i / QUARTER_LENGTH) as PlayerColor) : null;
      ctx.fillStyle = startOf !== null ? of(startOf) : Palette.ivory;
      ctx.fillRect(cell.x, cell.y, c, c);
      ctx.strokeRect(cell.x, cell.y, c, c);
      if (startOf !== null) this.drawArrow(ctx, cell, startOf, c);
      if (STAR_SQUARES.has(i)) drawStar(ctx, cell.x + c / 2, cell.y + c / 2, c * 0.34, Palette.goldDeep, Palette.gold);
    }
    for (const color of ALL_COLORS) {
      for (let step = 0; step < 5; step++) {
        const cell = this.cellRect(homeColumnCell(color, step), c);
        ctx.fillStyle = homeStep(color, step);
        ctx.fillRect(cell.x, cell.y, c, c);
        ctx.strokeRect(cell.x, cell.y, c, c);
      }
    }
  }

  private drawArrow(ctx: CanvasRenderingContext2D, cell: { x: number; y: number }, color: PlayerColor, c: number): void {
    const cx = cell.x + c / 2, cy = cell.y + c / 2;
    const next = this.toScreen(trackCell((startIndex(color) + 1) % TRACK_LENGTH));
    let dx = next.x - cx, dy = next.y - cy;
    const len = Math.hypot(dx, dy);
    if (len < 0.01) return;
    dx /= len;
    dy /= len;
    const s = c * 0.22;
    ctx.beginPath();
    ctx.moveTo(cx + dx * s, cy + dy * s);
    ctx.lineTo(cx - dx * s - dy * s, cy - dy * s + dx * s);
    ctx.lineTo(cx - dx * s + dy * s, cy - dy * s - dx * s);
    ctx.closePath();
    ctx.fillStyle = 'rgba(251,243,228,0.9)';
    ctx.fill();
  }

  private drawCentre(ctx: CanvasRenderingContext2D, c: number, active: Set<PlayerColor>): void {
    const m = this.margin();
    const left = m + 6 * c, top = m + 6 * c;
    const cx = left + 1.5 * c, cy = top + 1.5 * c;
    const corners = [
      { x: left, y: top + 3 * c },
      { x: left, y: top },
      { x: left + 3 * c, y: top },
      { x: left + 3 * c, y: top + 3 * c },
    ];
    for (let i = 0; i < 4; i++) {
      const a = corners[i], b = corners[(i + 3) % 4];
      ctx.beginPath();
      ctx.moveTo(a.x, a.y);
      ctx.lineTo(b.x, b.y);
      ctx.lineTo(cx, cy);
      ctx.closePath();
      ctx.fillStyle = active.has(i) ? of(i) : muted(i, 0.6);
      ctx.fill();
    }
    ctx.beginPath();
    ctx.arc(cx, cy, c * 0.78, 0, Math.PI * 2);
    ctx.fillStyle = Palette.night;
    ctx.fill();
    ctx.strokeStyle = Palette.gold;
    ctx.lineWidth = c * 0.08;
    ctx.stroke();
    drawCrown(ctx, cx, cy, c * 0.95);
  }

  private drawTurnHighlight(): void {
    const s = this.state;
    if (!s || s.phase === 'Finished') return;
    const color = s.players[s.currentPlayerIndex].color;
    const o = yardOrigin(color);
    const c = this.cell();
    const m = this.margin();
    const ctx = this.ctx;
    ctx.strokeStyle = `rgba(233,185,73,${0.6 + 0.4 * this.pulse()})`;
    ctx.lineWidth = c * 0.16;
    roundRect(ctx, m + o.x * c + c * 0.12, m + o.y * c + c * 0.12, 6 * c - c * 0.24, 6 * c - c * 0.24, c * 0.5);
    ctx.stroke();
  }

  private drawPieces(): void {
    const s = this.state;
    const c = this.cell();
    const currentSeat = s && s.phase !== 'Finished' ? s.players[s.currentPlayerIndex].seat : -1;
    const groups = new Map<string, Sprite[]>();
    for (const sp of this.sprites) {
      const key = `${Math.round(sp.pos.x * 4)}:${Math.round(sp.pos.y * 4)}`;
      const g = groups.get(key);
      if (g) g.push(sp);
      else groups.set(key, [sp]);
    }
    const pulse = this.pulse();
    // Draw movable pieces last so they sit on top of a stack.
    const ordered = [...groups.values()];
    for (const list of ordered) {
      list.sort((a, b) => Number(a.seat === currentSeat && this.legal.has(a.piece)) - Number(b.seat === currentSeat && this.legal.has(b.piece)));
      list.forEach((sp, k) => {
        const off = list.length === 1 ? { x: 0, y: 0 } : stackOffset(k, list.length, c);
        const p = this.toScreen(sp.pos);
        const r = c * (list.length === 1 ? 0.38 : 0.3) * sp.scale;
        this.drawPiece(p.x + off.x, p.y + off.y, r, sp.color, sp.seat === currentSeat && this.legal.has(sp.piece), pulse);
      });
    }
  }

  private drawPiece(x: number, y: number, r: number, color: PlayerColor, movable: boolean, pulse: number): void {
    const ctx = this.ctx;
    if (movable) {
      ctx.beginPath();
      ctx.arc(x, y, r * (1.45 + 0.15 * pulse), 0, Math.PI * 2);
      ctx.fillStyle = `rgba(233,185,73,${0.25 + 0.35 * pulse})`;
      ctx.fill();
    }
    ctx.save();
    ctx.shadowColor = 'rgba(0,0,0,0.43)';
    ctx.shadowBlur = r * 0.35;
    ctx.shadowOffsetY = r * 0.18;
    ctx.beginPath();
    ctx.arc(x, y, r, 0, Math.PI * 2);
    ctx.fillStyle = dark(color);
    ctx.fill();
    ctx.restore();

    const g = ctx.createRadialGradient(x - r * 0.3, y - r * 0.35, r * 0.05, x, y, r * 0.95);
    g.addColorStop(0, light(color));
    g.addColorStop(1, of(color));
    ctx.beginPath();
    ctx.arc(x, y, r * 0.86, 0, Math.PI * 2);
    ctx.fillStyle = g;
    ctx.fill();
    ctx.strokeStyle = Palette.ivory;
    ctx.lineWidth = Math.max(1.2, r * 0.14);
    ctx.stroke();

    drawCrown(ctx, x, y + r * 0.04, r * 0.82);

    if (movable) {
      ctx.beginPath();
      ctx.arc(x, y, r * 1.08, 0, Math.PI * 2);
      ctx.strokeStyle = Palette.gold;
      ctx.lineWidth = Math.max(2, r * 0.16);
      ctx.stroke();
    }
  }

  private cellRect(g: GridPoint, c: number): { x: number; y: number } {
    return { x: this.margin() + g.x * c, y: this.margin() + g.y * c };
  }
}

function stackOffset(k: number, n: number, c: number): { x: number; y: number } {
  const angle = (2 * Math.PI * k) / n - Math.PI / 4;
  const d = c * 0.17;
  return { x: d * Math.cos(angle), y: d * Math.sin(angle) };
}

function roundRect(ctx: CanvasRenderingContext2D, x: number, y: number, w: number, h: number, r: number): void {
  const rr = Math.min(r, w / 2, h / 2);
  ctx.beginPath();
  ctx.moveTo(x + rr, y);
  ctx.arcTo(x + w, y, x + w, y + h, rr);
  ctx.arcTo(x + w, y + h, x, y + h, rr);
  ctx.arcTo(x, y + h, x, y, rr);
  ctx.arcTo(x, y, x + w, y, rr);
  ctx.closePath();
}

export function drawCrown(ctx: CanvasRenderingContext2D, cx: number, cy: number, size: number): void {
  const w = size, h = size * 0.7;
  const x = cx - w / 2, y = cy - h / 2;
  ctx.beginPath();
  ctx.moveTo(x + w * 0.08, y + h);
  ctx.lineTo(x, y + h * 0.18);
  ctx.lineTo(x + w * 0.3, y + h * 0.5);
  ctx.lineTo(x + w * 0.5, y);
  ctx.lineTo(x + w * 0.7, y + h * 0.5);
  ctx.lineTo(x + w, y + h * 0.18);
  ctx.lineTo(x + w * 0.92, y + h);
  ctx.closePath();
  ctx.fillStyle = Palette.gold;
  ctx.fill();
  ctx.strokeStyle = Palette.goldDeep;
  ctx.lineWidth = Math.max(1, size * 0.05);
  ctx.stroke();
  ctx.beginPath();
  ctx.arc(cx, y + h * 0.66, size * 0.07, 0, Math.PI * 2);
  ctx.fillStyle = '#D7263D';
  ctx.fill();
}

function drawStar(ctx: CanvasRenderingContext2D, cx: number, cy: number, radius: number, stroke: string, fill: string): void {
  ctx.beginPath();
  for (let i = 0; i < 10; i++) {
    const r = i % 2 === 0 ? radius : radius * 0.45;
    const a = -Math.PI / 2 + (i * Math.PI) / 5;
    const px = cx + r * Math.cos(a), py = cy + r * Math.sin(a);
    if (i === 0) ctx.moveTo(px, py);
    else ctx.lineTo(px, py);
  }
  ctx.closePath();
  ctx.fillStyle = fill;
  ctx.fill();
  ctx.strokeStyle = stroke;
  ctx.lineWidth = Math.max(1, radius * 0.12);
  ctx.stroke();
}
