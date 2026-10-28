// Wires the lobby, mode picker and game screen together.
import { PlayerColor } from '../engine/model';
import { GameController, type SeatConfig, clearSave, loadSave } from '../game/controller';
import { BOT_AVATARS } from './avatars';
import { GameScreen } from './game';
import { renderLobby } from './home';
import { openGameMenu, openModePicker, openRules, openSettings } from './modals';
import { colorName } from './palette';
import { type Prefs, loadPrefs, loadStats, recordResult, savePrefs } from './prefs';
import { Sound } from './sound';

const BOT_NAMES = ['Duchess', 'Baron', 'Squire'];
const LOCAL_AVATARS = ['king', 'queen', 'knight', 'jester'];

function colorsFor(players: number): PlayerColor[] {
  return players === 2 ? [PlayerColor.Ruby, PlayerColor.Topaz]
    : players === 3 ? [PlayerColor.Ruby, PlayerColor.Emerald, PlayerColor.Topaz]
    : [PlayerColor.Ruby, PlayerColor.Emerald, PlayerColor.Topaz, PlayerColor.Sapphire];
}

export class App {
  private readonly sound: Sound;
  private controller: GameController | null = null;
  private screen: GameScreen | null = null;
  private prefs: Prefs = loadPrefs();

  constructor(private readonly root: HTMLElement, base: string) {
    this.sound = new Sound(base);
    document.addEventListener('pointerdown', () => this.sound.unlock(), { capture: true });
  }

  start(): void {
    this.showLobby();
  }

  private showLobby(): void {
    this.controller?.stop();
    this.controller = null;
    this.screen = null;
    const save = loadSave();
    this.mount(renderLobby(this.prefs, loadStats(), save, {
      onComputer: (players) => this.pickMode(players, 'computer'),
      onLocal: () => this.pickMode(2, 'local'),
      onResume: () => {
        if (save) this.startGame(new GameController(save.state, save.seats, this.newScreen(), { autoMoveSingle: this.prefs.autoMove }));
      },
      onDiscard: () => { clearSave(); this.showLobby(); },
      onRules: () => openRules(),
      onSettings: () => openSettings(this.prefs, this.sound, (p) => { this.prefs = p; savePrefs(p); }, () => this.showLobby()),
    }));
  }

  private pickMode(players: number, kind: 'computer' | 'local'): void {
    const title = kind === 'local' ? 'Pass & Play' : `${players} Players`;
    openModePicker(title, { mode: this.prefs.mode, difficulty: this.prefs.difficulty, players }, kind, (choice) => {
      this.prefs = { ...this.prefs, mode: choice.mode, difficulty: choice.difficulty };
      savePrefs(this.prefs);
      const colors = colorsFor(kind === 'local' ? choice.players : players);
      const seats: SeatConfig[] = kind === 'local'
        ? colors.map((c, i) => ({ name: colorName(c), bot: false, difficulty: choice.difficulty, avatar: LOCAL_AVATARS[i] }))
        : colors.map((_, i) => (i === 0
          ? { name: this.prefs.name, bot: false, difficulty: choice.difficulty, avatar: this.prefs.avatar }
          : { name: BOT_NAMES[i - 1], bot: true, difficulty: choice.difficulty, avatar: BOT_AVATARS[i - 1] }));
      clearSave();
      this.startGame(GameController.create(seats, choice.mode, this.newScreen(), { autoMoveSingle: this.prefs.autoMove }));
    });
  }

  private newScreen(): GameScreen {
    const screen = new GameScreen(this.sound, {
      onMenu: () => openGameMenu({ onResume: () => undefined, onRules: () => openRules(), onHome: () => this.showLobby() }),
      onRules: () => openRules(),
      onRoll: () => void this.controller?.humanRoll(),
      onPiece: (p) => void this.controller?.humanMove(p),
      onAgain: () => {
        const prev = this.controller;
        if (!prev) return this.showLobby();
        this.startGame(GameController.create(prev.seats, prev.current.rules.mode, this.newScreen(), { autoMoveSingle: this.prefs.autoMove }));
      },
      onHome: () => this.showLobby(),
      onEnded: (won) => { if (won !== null) recordResult(won); },
    });
    this.screen = screen;
    return screen;
  }

  private startGame(controller: GameController): void {
    this.controller?.stop();
    this.controller = controller;
    const screen = this.screen!;
    screen.attach(controller);
    this.mount(screen.element);
    controller.start();
  }

  private mount(el: HTMLElement): void {
    this.root.replaceChildren(el);
    window.scrollTo(0, 0);
  }
}
