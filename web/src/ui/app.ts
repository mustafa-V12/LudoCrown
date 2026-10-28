// Screens of the web version: Home (set up a game), Game (board, players, die), Results and Rules.
import type { BotDifficulty } from '../engine/bots';
import type { EngineResult, LegalMove } from '../engine/engine';
import { type GameMode, type GameState, AllModes, GameModes, PlayerColor, finishedCount, standings } from '../engine/model';
import { GameController, type GameView, type SeatConfig, clearSave, loadSave } from '../game/controller';
import { BoardRenderer, drawCrown } from './board';
import { colorName, of } from './palette';
import { Sound } from './sound';

type Opponents = 'computer' | 'local';

interface Setup {
  mode: GameMode;
  players: number;
  opponents: Opponents;
  difficulty: BotDifficulty;
  name: string;
  autoMove: boolean;
}

const SETUP_KEY = 'ludocrown.setup.v1';
const BOT_NAMES = ['Duchess', 'Baron', 'Squire'];
const DEFAULT_SETUP: Setup = { mode: 'Classic', players: 2, opponents: 'computer', difficulty: 'Normal', name: 'You', autoMove: true };

// ---------- tiny DOM helper ----------
type Child = Node | string | null | undefined | false;
function h<K extends keyof HTMLElementTagNameMap>(
  tag: K,
  props: Partial<Record<string, string | boolean | ((e: Event) => void)>> = {},
  ...children: Child[]
): HTMLElementTagNameMap[K] {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (v === undefined || v === false) continue;
    if (typeof v === 'function') el.addEventListener(k.replace(/^on/, '').toLowerCase(), v as EventListener);
    else if (k === 'class') el.className = String(v);
    else if (v === true) el.setAttribute(k, '');
    else el.setAttribute(k, String(v));
  }
  for (const c of children) if (c !== null && c !== undefined && c !== false) el.append(c);
  return el;
}

function segmented<T extends string | number>(label: string, options: { value: T; title: string; sub?: string }[], current: T, onPick: (v: T) => void): HTMLElement {
  const group = h('div', { class: 'segmented', role: 'radiogroup', 'aria-label': label });
  for (const o of options) {
    const btn = h('button', { class: 'seg' + (o.value === current ? ' on' : ''), role: 'radio', 'aria-checked': String(o.value === current), type: 'button' },
      h('span', { class: 'seg-title' }, o.title), o.sub ? h('span', { class: 'seg-sub' }, o.sub) : null);
    btn.addEventListener('click', () => onPick(o.value));
    group.append(btn);
  }
  return group;
}

function crownIcon(size: number): HTMLCanvasElement {
  const c = h('canvas', { class: 'crown-icon', 'aria-hidden': 'true' });
  const dpr = Math.min(3, window.devicePixelRatio || 1);
  c.width = size * dpr;
  c.height = size * dpr;
  c.style.width = `${size}px`;
  c.style.height = `${size}px`;
  const ctx = c.getContext('2d')!;
  ctx.scale(dpr, dpr);
  drawCrown(ctx, size / 2, size / 2, size * 0.9);
  return c;
}

function loadSetup(): Setup {
  try {
    const raw = localStorage.getItem(SETUP_KEY);
    if (raw) {
      const s = { ...DEFAULT_SETUP, ...(JSON.parse(raw) as Partial<Setup>) };
      if (!AllModes.some((m) => m.mode === s.mode)) s.mode = 'Classic';
      if (![2, 3, 4].includes(s.players)) s.players = 2;
      if (!['Easy', 'Normal', 'Hard'].includes(s.difficulty)) s.difficulty = 'Normal';
      if (s.opponents !== 'local') s.opponents = 'computer';
      s.name = String(s.name || 'You').slice(0, 16);
      return s;
    }
  } catch {
    /* ignore */
  }
  return { ...DEFAULT_SETUP };
}

function saveSetup(s: Setup): void {
  try {
    localStorage.setItem(SETUP_KEY, JSON.stringify(s));
  } catch {
    /* ignore */
  }
}

function seatsFor(s: Setup): SeatConfig[] {
  const colors = s.players === 2 ? [PlayerColor.Ruby, PlayerColor.Topaz] : s.players === 3
    ? [PlayerColor.Ruby, PlayerColor.Emerald, PlayerColor.Topaz]
    : [PlayerColor.Ruby, PlayerColor.Emerald, PlayerColor.Topaz, PlayerColor.Sapphire];
  if (s.opponents === 'local') return colors.map((c) => ({ name: colorName(c), bot: false, difficulty: s.difficulty }));
  return colors.map((_, i) => (i === 0
    ? { name: s.name.trim() || 'You', bot: false, difficulty: s.difficulty }
    : { name: BOT_NAMES[i - 1], bot: true, difficulty: s.difficulty }));
}

// ---------- the app ----------
export class App {
  private readonly sound: Sound;
  private controller: GameController | null = null;
  private setup = loadSetup();

  constructor(private readonly root: HTMLElement, base: string) {
    this.sound = new Sound(base);
    document.addEventListener('pointerdown', () => this.sound.unlock(), { capture: true });
  }

  start(): void {
    this.showHome();
  }

  // ----- Home -----
  private showHome(): void {
    this.controller?.stop();
    this.controller = null;
    const s = this.setup;
    const save = loadSave();
    const modeInfo = GameModes[s.mode];
    const rerender = () => {
      saveSetup(this.setup);
      this.showHome();
    };

    const isIos = /iPhone|iPad|iPod/.test(navigator.userAgent);
    const standalone = (navigator as unknown as { standalone?: boolean }).standalone === true || matchMedia('(display-mode: standalone)').matches;

    const resume = save
      ? h('section', { class: 'card resume' },
          h('h2', {}, 'Continue your game'),
          h('p', { class: 'muted' }, `${GameModes[save.state.rules.mode].name} · ${save.seats.map((x) => x.name).join(' vs ')}`),
          h('div', { class: 'row' },
            h('button', { class: 'btn gold', type: 'button', onclick: () => this.startGame(new GameController(save.state, save.seats, this.view(), { autoMoveSingle: this.setup.autoMove })) }, 'Continue'),
            h('button', { class: 'btn ghost', type: 'button', onclick: () => { clearSave(); this.showHome(); } }, 'Discard')))
      : null;

    const screen = h('main', { class: 'screen home' },
      h('header', { class: 'hero' },
        crownIcon(64),
        h('h1', {}, 'Ludo Crown'),
        h('p', { class: 'muted' }, 'Web version · play on this device against the computer or with friends')),
      resume,
      h('section', { class: 'card' },
        h('h2', {}, 'Game mode'),
        segmented('Game mode', AllModes.map((m) => ({ value: m.mode, title: m.name, sub: m.tagline })), s.mode, (v) => { this.setup = { ...s, mode: v }; rerender(); }),
        h('p', { class: 'rule' }, h('strong', {}, `${modeInfo.name}: `), modeInfo.winCondition),
        h('p', { class: 'muted small' }, modeInfo.description)),
      h('section', { class: 'card' },
        h('h2', {}, 'Players'),
        segmented('Number of players', [2, 3, 4].map((n) => ({ value: n, title: `${n} players` })), s.players, (v) => { this.setup = { ...s, players: v }; rerender(); }),
        h('h2', { class: 'mt' }, 'Opponents'),
        segmented('Opponents', [
          { value: 'computer' as Opponents, title: 'Computer', sub: 'You vs bots' },
          { value: 'local' as Opponents, title: 'Pass & play', sub: 'Share this phone' },
        ], s.opponents, (v) => { this.setup = { ...s, opponents: v }; rerender(); }),
        s.opponents === 'computer'
          ? h('div', {},
              h('h2', { class: 'mt' }, 'Difficulty'),
              segmented('Difficulty', [
                { value: 'Easy' as BotDifficulty, title: 'Easy' },
                { value: 'Normal' as BotDifficulty, title: 'Normal' },
                { value: 'Hard' as BotDifficulty, title: 'Hard' },
              ], s.difficulty, (v) => { this.setup = { ...s, difficulty: v }; rerender(); }),
              h('label', { class: 'field mt' }, h('span', {}, 'Your name'), this.nameInput()))
          : h('p', { class: 'muted small mt' }, 'Everyone plays on this phone. Pass it to the player whose colour is glowing.')),
      h('label', { class: 'toggle' },
        this.checkbox(s.autoMove, (on) => { this.setup = { ...this.setup, autoMove: on }; saveSetup(this.setup); }),
        h('span', {}, 'Move automatically when only one move is possible')),
      h('div', { class: 'sticky-play' }, h('button', { class: 'btn gold big', type: 'button', onclick: () => this.newGame() }, save ? 'Start a new game' : 'Play')),
      h('button', { class: 'btn link', type: 'button', onclick: () => this.showRules() }, 'How to play'),
      isIos && !standalone
        ? h('p', { class: 'tip' }, 'Tip: tap ', h('strong', {}, 'Share'), ' → ', h('strong', {}, 'Add to Home Screen'), ' to play full-screen, like an app.')
        : null,
      h('p', { class: 'muted small center' }, 'Online matches, gem rerolls, Shield bottles, the store and leagues are part of the Ludo Crown app.'));

    this.mount(screen);
  }

  private nameInput(): HTMLInputElement {
    const input = h('input', { type: 'text', maxlength: '16', autocomplete: 'nickname', enterkeyhint: 'done', value: this.setup.name });
    input.addEventListener('input', () => {
      this.setup = { ...this.setup, name: input.value.slice(0, 16) };
      saveSetup(this.setup);
    });
    return input;
  }

  private checkbox(checked: boolean, onChange: (on: boolean) => void): HTMLInputElement {
    const input = h('input', { type: 'checkbox', checked });
    input.addEventListener('change', () => onChange(input.checked));
    return input;
  }

  private newGame(): void {
    clearSave();
    saveSetup(this.setup);
    this.startGame(GameController.create(seatsFor(this.setup), this.setup.mode, this.view(), { autoMoveSingle: this.setup.autoMove }));
  }

  // ----- Game -----
  private gameView: GameScreen | null = null;

  private view(): GameView {
    const screen = new GameScreen(this.sound, {
      onHome: () => this.showHome(),
      onRules: () => this.showRules(),
      onRoll: () => void this.controller?.humanRoll(),
      onPiece: (p) => void this.controller?.humanMove(p),
      onAgain: () => {
        const prev = this.controller;
        if (!prev) return this.showHome();
        const mode = prev.current.rules.mode;
        this.startGame(GameController.create(prev.seats, mode, this.view(), { autoMoveSingle: this.setup.autoMove }));
      },
    });
    this.gameView = screen;
    return screen;
  }

  private startGame(controller: GameController): void {
    this.controller?.stop();
    this.controller = controller;
    const screen = this.gameView!;
    screen.attach(controller);
    this.mount(screen.element);
    controller.start();
  }

  // ----- Rules -----
  private showRules(): void {
    const close = () => overlay.remove();
    const overlay = h('div', { class: 'overlay', role: 'dialog', 'aria-modal': 'true', 'aria-label': 'How to play' },
      h('div', { class: 'sheet' },
        h('div', { class: 'sheet-head' }, h('h2', {}, 'How to play'), h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Close', onclick: close }, '✕')),
        h('div', { class: 'rules' },
          h('h3', {}, 'Your turn'),
          h('p', {}, 'Tap the die, then tap one of your glowing pieces to move it that many squares. If no piece can move, the turn passes — even after a 6.'),
          h('h3', {}, 'Leaving the yard'),
          h('p', {}, 'A piece enters the board on a 6, onto your coloured start square.'),
          h('h3', {}, 'Moving'),
          h('p', {}, 'Pieces travel clockwise around the board, then up their own coloured home column to the crown. You need the exact number to reach the crown.'),
          h('h3', {}, 'Capturing'),
          h('p', {}, 'Land exactly on an opponent to send their piece back to its yard. Stars and start squares are safe: nobody can be captured there. Your own pieces may share a square.'),
          h('h3', {}, 'Extra rolls'),
          h('p', {}, 'Rolling a 6, capturing, or crowning a piece gives you another roll. Three 6s in a row forfeit the turn.'),
          h('h3', {}, 'Winning'),
          h('p', {}, h('strong', {}, 'Classic: '), GameModes.Classic.winCondition, ' ', h('strong', {}, 'Quick: '), GameModes.Quick.winCondition),
          h('p', { class: 'muted small' }, 'Your game is saved on this device after every move, so you can close the page and continue later.'))));
    overlay.addEventListener('click', (e) => { if (e.target === overlay) close(); });
    document.body.append(overlay);
    (overlay.querySelector('.icon-btn') as HTMLButtonElement).focus();
  }

  private mount(el: HTMLElement): void {
    this.root.replaceChildren(el);
    window.scrollTo(0, 0);
  }
}

// ---------- game screen ----------
interface GameCallbacks {
  onHome(): void;
  onRules(): void;
  onRoll(): void;
  onPiece(piece: number): void;
  onAgain(): void;
}

class GameScreen implements GameView {
  readonly element: HTMLElement;
  private readonly board: BoardRenderer;
  private readonly canvas: HTMLCanvasElement;
  private readonly topRow: HTMLElement;
  private readonly bottomRow: HTMLElement;
  private readonly status: HTMLElement;
  private readonly die: HTMLButtonElement;
  private readonly dieLabel: HTMLElement;
  private readonly toasts: HTMLElement;
  private readonly modeChip: HTMLElement;
  private readonly soundBtn: HTMLButtonElement;
  private controller: GameController | null = null;
  private lastFace = 0;
  private canRoll = false;

  constructor(private readonly sound: Sound, private readonly cb: GameCallbacks) {
    this.canvas = h('canvas', { class: 'board', role: 'img', 'aria-label': 'Ludo board' });
    this.topRow = h('div', { class: 'badges' });
    this.bottomRow = h('div', { class: 'badges' });
    this.status = h('p', { class: 'status', 'aria-live': 'polite' });
    this.dieLabel = h('span', { class: 'die-label' });
    this.die = h('button', { class: 'die', type: 'button', 'aria-label': 'Roll the die', onclick: () => { if (this.canRoll) this.cb.onRoll(); } });
    this.toasts = h('div', { class: 'toasts', 'aria-live': 'polite' });
    this.modeChip = h('span', { class: 'chip' });
    this.soundBtn = h('button', { class: 'icon-btn', type: 'button', onclick: () => this.toggleSound() });
    this.updateSoundBtn();

    this.element = h('main', { class: 'screen game' },
      h('div', { class: 'topbar' },
        h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Home', onclick: () => this.cb.onHome() }, '‹'),
        this.modeChip,
        h('div', { class: 'row tight' },
          h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'How to play', onclick: () => this.cb.onRules() }, '?'),
          this.soundBtn)),
      this.topRow,
      h('div', { class: 'board-wrap' }, this.canvas, this.toasts),
      this.bottomRow,
      this.status,
      h('div', { class: 'die-area' }, this.die, this.dieLabel));

    this.board = new BoardRenderer(this.canvas);
    this.canvas.addEventListener('click', (e) => {
      const piece = this.board.hitTest(e.clientX, e.clientY);
      if (piece !== null) {
        this.sound.play('tap');
        this.cb.onPiece(piece);
      }
    });
    this.setFace(0);
  }

  attach(controller: GameController): void {
    this.controller = controller;
  }

  render(state: GameState, legal: readonly LegalMove[], canRoll: boolean): void {
    const mode = GameModes[state.rules.mode];
    this.modeChip.textContent = `${mode.name} · ${mode.tagline}`;
    this.modeChip.title = mode.winCondition;
    this.board.setState(state, legal.map((m) => m.piece));
    this.renderBadges(state);
    this.canRoll = canRoll;
    this.die.classList.toggle('ready', canRoll);
    this.die.disabled = !canRoll;
    if (state.lastRoll && this.lastFace === 0) this.setFace(state.lastRoll);

    const cur = state.players[state.currentPlayerIndex];
    const seats = this.controller?.seats ?? [];
    const humans = seats.filter((x) => !x.bot).length;
    const isBot = seats[cur.seat]?.bot;
    const who = humans <= 1 && !isBot ? 'Your' : `${cur.name}'s`;
    let text: string;
    if (state.phase === 'Finished') text = '';
    else if (isBot) text = state.phase === 'AwaitingRoll' ? `${cur.name} is rolling…` : `${cur.name} is thinking…`;
    else if (canRoll) text = `${who} turn — tap the die`;
    else if (legal.length > 1) text = `${humans <= 1 ? 'Choose' : `${cur.name}, choose`} a piece to move ${state.pendingRoll}`;
    else if (legal.length === 1) text = `Tap the glowing piece to move ${state.pendingRoll}`;
    else text = `${who} turn`;
    this.status.textContent = text;
    this.status.style.setProperty('--turn', of(cur.color));
    this.dieLabel.textContent = canRoll ? 'Tap to roll' : '';
  }

  async animate(prev: GameState, result: EngineResult): Promise<void> {
    const rolled = result.events.find((e) => e.type === 'DiceRolled');
    if (rolled) {
      this.canRoll = false;
      this.die.disabled = true;
      this.die.classList.remove('ready');
      this.sound.play('dice');
      await this.rollAnimation(rolled.value);
      const noMove = result.events.some((e) => e.type === 'NoLegalMoves' || e.type === 'ThreeSixesForfeit');
      if (noMove) await sleep(650);
    }
    const moved = result.events.some((e) => e.type === 'PieceMoved' || e.type === 'PieceEntered');
    if (moved) this.sound.play(result.events.some((e) => e.type === 'PieceEntered') ? 'enter' : 'move');
    const boardDone = this.board.animate(prev, result.state, result.events);
    if (result.events.some((e) => e.type === 'PieceCaptured')) setTimeout(() => this.sound.play('capture'), 250);
    await boardDone;
    if (result.events.some((e) => e.type === 'PieceFinished') && result.state.phase !== 'Finished') this.sound.play('finish');
    this.renderBadges(result.state);
    const passed = result.events.find((e) => e.type === 'TurnPassed');
    if (passed && this.controller && !this.controller.seats[passed.value]?.bot) this.sound.play('turn');
  }

  notice(text: string): void {
    const t = h('div', { class: 'toast' }, text);
    this.toasts.append(t);
    while (this.toasts.children.length > 2) this.toasts.firstElementChild?.remove();
    setTimeout(() => t.classList.add('out'), 2200);
    setTimeout(() => t.remove(), 2600);
  }

  ended(state: GameState, seats: readonly SeatConfig[]): void {
    const order = standings(state);
    const humans = seats.map((s, i) => (s.bot ? -1 : i)).filter((i) => i >= 0);
    const winner = state.players[state.winnerSeat ?? order[0]];
    const single = humans.length === 1 ? humans[0] : null;
    const youWon = single !== null && winner.seat === single;
    this.sound.play(single === null || youWon ? 'win' : 'lose');
    const title = youWon ? 'You win the crown!' : `${winner.name} wins the crown!`;
    const sub = single !== null && !youWon ? `You finished ${ordinal(order.indexOf(single) + 1)}.` : GameModes[state.rules.mode].winCondition;

    const overlay = h('div', { class: 'overlay', role: 'dialog', 'aria-modal': 'true', 'aria-label': 'Results' },
      h('div', { class: 'sheet results' },
        crownIcon(72),
        h('h2', {}, title),
        h('p', { class: 'muted' }, sub),
        h('ol', { class: 'standings' },
          ...order.map((seat, i) => {
            const p = state.players[seat];
            return h('li', {},
              h('span', { class: 'place' }, ordinal(i + 1)),
              h('span', { class: 'dot', style: `background:${of(p.color)}` }),
              h('span', { class: 'who' }, p.name),
              h('span', { class: 'muted small' }, `${finishedCount(p)}/${state.rules.piecesToWin} crowned`));
          })),
        h('button', { class: 'btn gold big', type: 'button', onclick: () => { overlay.remove(); this.cb.onAgain(); } }, 'Play again'),
        h('button', { class: 'btn ghost', type: 'button', onclick: () => { overlay.remove(); this.cb.onHome(); } }, 'Home')));
    setTimeout(() => {
      if (this.element.isConnected) document.body.append(overlay);
    }, 700);
  }

  private renderBadges(state: GameState): void {
    const seats = this.controller?.seats ?? [];
    const humans = seats.filter((x) => !x.bot).length;
    const badge = (color: PlayerColor) => {
      const p = state.players.find((x) => x.color === color);
      if (!p) return h('div', { class: 'badge empty' });
      const seat = seats[p.seat];
      const active = state.phase !== 'Finished' && state.players[state.currentPlayerIndex].seat === p.seat;
      const kind = seat?.bot ? `${seat.difficulty} bot` : humans <= 1 ? 'You' : 'Player';
      return h('div', { class: 'badge' + (active ? ' active' : ''), style: `--c:${of(color)}` },
        h('span', { class: 'gem' }),
        h('span', { class: 'meta' },
          h('span', { class: 'name' }, p.name),
          h('span', { class: 'kind' }, `${kind} · ${finishedCount(p)}/${state.rules.piecesToWin} 👑`)));
    };
    this.topRow.replaceChildren(badge(PlayerColor.Emerald), badge(PlayerColor.Topaz));
    this.bottomRow.replaceChildren(badge(PlayerColor.Ruby), badge(PlayerColor.Sapphire));
  }

  private async rollAnimation(value: number): Promise<void> {
    if (matchMedia('(prefers-reduced-motion: reduce)').matches) {
      this.setFace(value);
      return;
    }
    this.die.classList.add('rolling');
    const start = performance.now();
    while (performance.now() - start < 520) {
      this.setFace(1 + Math.floor(Math.random() * 6));
      await sleep(70);
    }
    this.die.classList.remove('rolling');
    this.setFace(value);
    await sleep(160);
  }

  private setFace(value: number): void {
    this.lastFace = value;
    const pips: Record<number, number[]> = { 0: [], 1: [4], 2: [0, 8], 3: [0, 4, 8], 4: [0, 2, 6, 8], 5: [0, 2, 4, 6, 8], 6: [0, 2, 3, 5, 6, 8] };
    const face = h('span', { class: 'face' });
    for (let i = 0; i < 9; i++) face.append(h('span', { class: 'pip' + (pips[value].includes(i) ? ' on' : '') }));
    this.die.replaceChildren(face);
    this.die.setAttribute('aria-label', value ? `Die showing ${value}. Roll the die` : 'Roll the die');
  }

  private toggleSound(): void {
    this.sound.setEnabled(!this.sound.enabled);
    this.updateSoundBtn();
  }

  private updateSoundBtn(): void {
    this.soundBtn.textContent = this.sound.enabled ? '🔊' : '🔇';
    this.soundBtn.setAttribute('aria-label', this.sound.enabled ? 'Sound on' : 'Sound off');
  }
}

const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));
const ordinal = (n: number) => (n === 1 ? '1st' : n === 2 ? '2nd' : n === 3 ? '3rd' : `${n}th`);
