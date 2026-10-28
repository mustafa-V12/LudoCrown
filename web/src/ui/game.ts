// The game screen: board in the middle, a player (avatar, name, die) in each corner, as in classic Ludo apps.
import confetti from 'canvas-confetti';
import type { EngineResult, LegalMove } from '../engine/engine';
import { type GameState, GameModes, PlayerColor, finishedCount, standings } from '../engine/model';
import type { GameController, GameView, SeatConfig } from '../game/controller';
import { Icons } from './art';
import { avatarSvg } from './avatars';
import { BoardRenderer } from './board';
import { Die3D } from './dice';
import { h, ordinal, reducedMotion, sleep, svg } from './dom';
import { openPanel } from './modals';
import { of } from './palette';
import type { Sound } from './sound';

export interface GameCallbacks {
  onMenu(): void;
  onRules(): void;
  onRoll(): void;
  onPiece(piece: number): void;
  onAgain(): void;
  onHome(): void;
  onEnded(won: boolean | null): void;
}

interface Corner {
  color: PlayerColor;
  root: HTMLElement;
  avatar: HTMLElement;
  name: HTMLElement;
  crowns: HTMLElement;
  dieBox: HTMLButtonElement;
  die: Die3D;
}

const CORNERS: { color: PlayerColor; row: 'top' | 'bottom'; side: 'left' | 'right' }[] = [
  { color: PlayerColor.Emerald, row: 'top', side: 'left' },
  { color: PlayerColor.Topaz, row: 'top', side: 'right' },
  { color: PlayerColor.Ruby, row: 'bottom', side: 'left' },
  { color: PlayerColor.Sapphire, row: 'bottom', side: 'right' },
];

export class GameScreen implements GameView {
  readonly element: HTMLElement;
  private readonly board: BoardRenderer;
  private readonly canvas: HTMLCanvasElement;
  private readonly corners = new Map<PlayerColor, Corner>();
  private readonly toasts: HTMLElement;
  private readonly modeChip: HTMLElement;
  private readonly soundBtn: HTMLButtonElement;
  private controller: GameController | null = null;
  private canRoll = false;

  constructor(private readonly sound: Sound, private readonly cb: GameCallbacks) {
    this.canvas = h('canvas', { class: 'board', role: 'img', 'aria-label': 'Ludo board' });
    this.toasts = h('div', { class: 'toasts', 'aria-live': 'polite' });
    this.modeChip = h('span', { class: 'mode-pill' });
    this.soundBtn = h('button', { class: 'round-btn', type: 'button', onclick: () => this.toggleSound() });
    this.updateSoundBtn();

    const rows = { top: h('div', { class: 'corner-row top' }), bottom: h('div', { class: 'corner-row bottom' }) };
    for (const c of CORNERS) {
      const corner = this.buildCorner(c.color, c.row, c.side);
      this.corners.set(c.color, corner);
      rows[c.row].append(corner.root);
    }

    this.element = h('main', { class: 'screen game' },
      h('header', { class: 'game-top' },
        h('button', { class: 'round-btn', type: 'button', 'aria-label': 'Menu', onclick: () => this.cb.onMenu() }, svg(Icons.menu)),
        h('button', { class: 'pill-btn', type: 'button', onclick: () => this.cb.onRules() }, svg(Icons.info, 'pb-icon'), h('span', {}, 'Rules')),
        this.modeChip,
        h('span', { class: 'spacer' }),
        this.soundBtn),
      h('div', { class: 'table' }, rows.top, h('div', { class: 'board-wrap' }, this.canvas, this.toasts), rows.bottom));

    this.board = new BoardRenderer(this.canvas);
    this.canvas.addEventListener('click', (e) => {
      const piece = this.board.hitTest(e.clientX, e.clientY);
      if (piece !== null) {
        this.sound.play('tap');
        this.cb.onPiece(piece);
      }
    });
  }

  private buildCorner(color: PlayerColor, row: 'top' | 'bottom', side: 'left' | 'right'): Corner {
    const die = new Die3D();
    const dieBox = h('button', { class: 'die-box', type: 'button', 'aria-label': 'Roll the die', onclick: () => { if (this.canRoll) this.cb.onRoll(); } },
      h('span', { class: 'tap-arrow', 'aria-hidden': 'true' }), die.element);
    const avatar = h('div', { class: 'avatar-ring' });
    const name = h('div', { class: 'p-name' });
    const crowns = h('div', { class: 'p-crowns' });
    const who = h('div', { class: 'who' }, row === 'bottom' ? name : null, h('div', { class: 'avatar-wrap' }, avatar, crowns), row === 'top' ? name : null);
    const root = h('div', { class: `corner ${side} ${row}`, style: `--pc:${of(color)}` }, side === 'left' ? who : dieBox, side === 'left' ? dieBox : who);
    return { color, root, avatar, name, crowns, dieBox, die };
  }

  attach(controller: GameController): void {
    this.controller = controller;
  }

  render(state: GameState, legal: readonly LegalMove[], canRoll: boolean): void {
    const mode = GameModes[state.rules.mode];
    this.modeChip.replaceChildren(svg(Icons.crown, 'mp-icon'), h('span', {}, mode.name));
    this.modeChip.title = mode.winCondition;
    this.board.setState(state, legal.map((m) => m.piece));
    this.renderCorners(state, canRoll);
  }

  private renderCorners(state: GameState, canRoll: boolean): void {
    const seats = this.controller?.seats ?? [];
    const humans = seats.filter((s) => !s.bot).length;
    const current = state.phase === 'Finished' ? null : state.players[state.currentPlayerIndex];
    this.canRoll = canRoll;
    for (const corner of this.corners.values()) {
      const p = state.players.find((x) => x.color === corner.color);
      corner.root.classList.toggle('empty', !p);
      if (!p) continue;
      const seat = seats[p.seat];
      const active = current?.seat === p.seat;
      corner.root.classList.toggle('active', active);
      corner.root.classList.toggle('can-roll', active && canRoll);
      const avatarId = seat?.avatar ?? 'king';
      if (corner.avatar.dataset.id !== avatarId) {
        corner.avatar.innerHTML = avatarSvg(avatarId);
        corner.avatar.dataset.id = avatarId;
      }
      corner.name.textContent = p.name;
      corner.root.title = seat?.bot ? `${p.name} · computer (${seat.difficulty})` : humans <= 1 ? 'You' : p.name;
      corner.crowns.replaceChildren(svg(Icons.crown, 'cr-icon'), h('span', {}, `${finishedCount(p)}/${state.rules.piecesToWin}`));
      corner.dieBox.disabled = !(active && canRoll);
      corner.dieBox.setAttribute('aria-label', active && canRoll ? 'Roll the die' : `${p.name}'s die`);
      if (active && corner.die.value === 0 && state.lastRoll) corner.die.show(state.lastRoll, false);
    }
  }

  async animate(prev: GameState, result: EngineResult): Promise<void> {
    const rolled = result.events.find((e) => e.type === 'DiceRolled');
    if (rolled) {
      const color = prev.players[rolled.seat].color;
      const corner = this.corners.get(color)!;
      this.canRoll = false;
      corner.root.classList.remove('can-roll');
      corner.dieBox.disabled = true;
      this.sound.play('dice');
      await corner.die.roll(rolled.value);
      if (result.events.some((e) => e.type === 'NoLegalMoves' || e.type === 'ThreeSixesForfeit')) {
        corner.root.classList.add('no-move');
        await sleep(650);
        corner.root.classList.remove('no-move');
      }
    }
    if (result.events.some((e) => e.type === 'PieceMoved' || e.type === 'PieceEntered')) {
      this.sound.play(result.events.some((e) => e.type === 'PieceEntered') ? 'enter' : 'move');
    }
    if (result.events.some((e) => e.type === 'PieceCaptured')) setTimeout(() => this.sound.play('capture'), 220);
    await this.board.animate(prev, result.state, result.events);
    if (result.events.some((e) => e.type === 'PieceFinished') && result.state.phase !== 'Finished') this.sound.play('finish');
    this.renderCorners(result.state, false);
    const passed = result.events.find((e) => e.type === 'TurnPassed');
    if (passed && this.controller && !this.controller.seats[passed.value]?.bot) this.sound.play('turn');
  }

  notice(text: string): void {
    const t = h('div', { class: 'toast' }, text);
    this.toasts.append(t);
    while (this.toasts.children.length > 2) this.toasts.firstElementChild?.remove();
    setTimeout(() => t.classList.add('out'), 2100);
    setTimeout(() => t.remove(), 2500);
  }

  ended(state: GameState, seats: readonly SeatConfig[]): void {
    const order = standings(state);
    const humans = seats.map((s, i) => (s.bot ? -1 : i)).filter((i) => i >= 0);
    const winner = state.players[state.winnerSeat ?? order[0]];
    const single = humans.length === 1 ? humans[0] : null;
    const youWon = single !== null && winner.seat === single;
    const celebrate = single === null || youWon;
    this.cb.onEnded(single === null ? null : youWon);

    setTimeout(() => {
      if (!this.element.isConnected) return;
      this.sound.play(celebrate ? 'win' : 'lose');
      if (celebrate && !reducedMotion()) {
        const colors = ['#E9B949', '#D7263D', '#17A673', '#2F6FE0', '#F2A71B', '#FBF3E4'];
        confetti({ particleCount: 140, spread: 80, origin: { y: 0.35 }, colors, disableForReducedMotion: true });
        setTimeout(() => confetti({ particleCount: 90, angle: 60, spread: 60, origin: { x: 0, y: 0.6 }, colors }), 300);
        setTimeout(() => confetti({ particleCount: 90, angle: 120, spread: 60, origin: { x: 1, y: 0.6 }, colors }), 450);
      }
      const title = single === null ? `${winner.name} wins!` : youWon ? 'VICTORY!' : `${winner.name} wins`;
      const sub = single !== null && !youWon ? `You finished ${ordinal(order.indexOf(single) + 1)}` : GameModes[state.rules.mode].winCondition;
      let panel: ReturnType<typeof openPanel>;
      const list = h('ol', { class: 'podium' }, ...order.map((seat, i) => {
        const p = state.players[seat];
        return h('li', { class: i === 0 ? 'first' : '', style: `--pc:${of(p.color)}` },
          h('span', { class: 'pod-place' }, i === 0 ? svg(Icons.crown, 'pod-crown') : ordinal(i + 1)),
          svg(avatarSvg(seats[seat]?.avatar ?? 'king'), 'avatar sm ringed'),
          h('span', { class: 'pod-name' }, p.name),
          h('span', { class: 'pod-score' }, `${finishedCount(p)}/${state.rules.piecesToWin}`));
      }));
      panel = openPanel(celebrate ? 'Victory' : 'Game over', [
        h('div', { class: 'result-hero' + (celebrate ? ' win' : '') },
          svg(avatarSvg(seats[winner.seat]?.avatar ?? 'king'), 'avatar hero ringed'),
          h('h2', {}, title), h('p', {}, sub)),
        list,
        h('div', { class: 'result-actions' },
          h('button', { class: 'btn3d violet', type: 'button', onclick: () => { panel.close(); this.cb.onHome(); } }, 'LOBBY'),
          h('button', { class: 'btn3d gold', type: 'button', onclick: () => { panel.close(); this.cb.onAgain(); } }, 'PLAY AGAIN')),
      ], { className: 'result-panel', onClose: () => undefined });
    }, 750);
  }

  private toggleSound(): void {
    this.sound.setEnabled(!this.sound.enabled);
    this.updateSoundBtn();
  }

  private updateSoundBtn(): void {
    this.soundBtn.replaceChildren(svg(this.sound.enabled ? Icons.soundOn : Icons.soundOff));
    this.soundBtn.setAttribute('aria-label', this.sound.enabled ? 'Sound on' : 'Sound off');
  }
}
