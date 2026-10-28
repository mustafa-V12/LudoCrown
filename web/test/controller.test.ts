import { beforeEach, describe, expect, it } from 'vitest';
import { getLegalMoves } from '../src/engine/engine';
import { type GameState, currentPlayer, finishedCount } from '../src/engine/model';
import { GameController, type GameView, type SeatConfig, clearSave, describe as describeEvent, loadSave } from '../src/game/controller';

class MemoryStorage {
  private data = new Map<string, string>();
  getItem(k: string) { return this.data.get(k) ?? null; }
  setItem(k: string, v: string) { this.data.set(k, String(v)); }
  removeItem(k: string) { this.data.delete(k); }
  clear() { this.data.clear(); }
}

class FakeView implements GameView {
  renders = 0;
  animations = 0;
  notices: string[] = [];
  endedState: GameState | null = null;
  lastLegal: number[] = [];
  lastCanRoll = false;
  render(_s: GameState, legal: readonly { piece: number }[], canRoll: boolean) { this.renders++; this.lastLegal = legal.map((m) => m.piece); this.lastCanRoll = canRoll; }
  async animate() { this.animations++; }
  notice(t: string) { this.notices.push(t); }
  ended(s: GameState) { this.endedState = s; }
}

const bots = (n: number, difficulty: 'Easy' | 'Normal' | 'Hard' = 'Normal'): SeatConfig[] =>
  Array.from({ length: n }, (_, i) => ({ name: `Bot${i}`, bot: true, difficulty }));

async function until(cond: () => boolean, ms = 20000) {
  const start = Date.now();
  while (!cond()) {
    if (Date.now() - start > ms) throw new Error('timeout');
    await new Promise((r) => setTimeout(r, 0));
  }
}

beforeEach(() => {
  (globalThis as unknown as { localStorage: MemoryStorage }).localStorage = new MemoryStorage();
});

describe('GameController', () => {
  for (const mode of ['Classic', 'Quick'] as const) {
    for (const n of [2, 3, 4]) {
      it(`plays a full ${mode} game with ${n} computer players to a valid finish`, async () => {
        const view = new FakeView();
        const c = GameController.create(bots(n, n === 3 ? 'Hard' : n === 4 ? 'Easy' : 'Normal'), mode, view, { autoMoveSingle: true, delays: false });
        c.start();
        await until(() => view.endedState !== null);
        const s = view.endedState!;
        expect(s.phase).toBe('Finished');
        const winner = s.players[s.winnerSeat!];
        expect(finishedCount(winner)).toBe(mode === 'Classic' ? 4 : 1);
        // Nobody else reached the win condition.
        for (const p of s.players) if (p.seat !== winner.seat) expect(finishedCount(p)).toBeLessThan(mode === 'Classic' ? 4 : 1);
        expect(loadSave()).toBeNull(); // finished games are not kept
      });
    }
  }

  it('lets a human roll and move only when it is their turn, ignoring illegal taps', async () => {
    const view = new FakeView();
    const seats: SeatConfig[] = [{ name: 'You', bot: false, difficulty: 'Normal' }, { name: 'Bot', bot: true, difficulty: 'Normal' }];
    const c = GameController.create(seats, 'Classic', view, { autoMoveSingle: false, delays: false });
    c.start();
    expect(view.lastCanRoll).toBe(true);

    for (let turns = 0; turns < 400 && c.current.phase !== 'Finished'; turns++) {
      await until(() => c.isLocalHumanTurn() || c.current.phase === 'Finished');
      if ((c.current.phase as string) === 'Finished') break;
      const s = c.current;
      if (s.phase === 'AwaitingRoll') {
        await c.humanRoll();
      } else {
        const legal = getLegalMoves(s).map((m) => m.piece);
        const illegal = [0, 1, 2, 3].find((p) => !legal.includes(p));
        if (illegal !== undefined) {
          const before = c.current.version;
          await c.humanMove(illegal);
          expect(c.current.version).toBe(before);
        }
        await c.humanMove(legal[0]);
      }
    }
    await until(() => view.endedState !== null, 60000);
    expect(view.animations).toBeGreaterThan(10);
  });

  it('moves automatically when a human has exactly one legal move', async () => {
    const view = new FakeView();
    const c = GameController.create([{ name: 'A', bot: false, difficulty: 'Normal' }, { name: 'B', bot: false, difficulty: 'Normal' }], 'Quick', view, { autoMoveSingle: true, delays: false });
    c.start();
    for (let i = 0; i < 300; i++) {
      const s = c.current;
      if (s.phase === 'Finished') break;
      if (s.phase === 'AwaitingRoll') await c.humanRoll();
      // After a roll the controller never leaves a single legal move waiting for a tap.
      if (c.current.phase === 'AwaitingMove') {
        expect(getLegalMoves(c.current).length).toBeGreaterThan(1);
        await c.humanMove(getLegalMoves(c.current)[0].piece);
      }
    }
  });

  it('saves after every change and resumes the same game', async () => {
    const view = new FakeView();
    const seats: SeatConfig[] = [{ name: 'You', bot: false, difficulty: 'Hard' }, { name: 'Bot', bot: true, difficulty: 'Hard' }];
    const c = GameController.create(seats, 'Quick', view, { autoMoveSingle: true, delays: false });
    c.start();
    await c.humanRoll();
    await until(() => c.isLocalHumanTurn() || c.current.phase === 'Finished');
    c.stop();
    const save = loadSave();
    expect(save).not.toBeNull();
    expect(save!.state.version).toBe(c.current.version);
    expect(save!.state.rules.mode).toBe('Quick');
    expect(save!.state.rules.piecesToWin).toBe(1);
    expect(save!.seats).toEqual(seats);

    const resumed = new GameController(save!.state, save!.seats, new FakeView(), { autoMoveSingle: true, delays: false });
    expect(currentPlayer(resumed.current).seat).toBe(currentPlayer(c.current).seat);
    clearSave();
    expect(loadSave()).toBeNull();
  });

  it('discards tampered or corrupt saves and never trusts stored rules', () => {
    localStorage.setItem('ludocrown.offline.v1', '{not json');
    expect(loadSave()).toBeNull();
    expect(localStorage.getItem('ludocrown.offline.v1')).toBeNull();

    const view = new FakeView();
    const c = GameController.create(bots(2), 'Classic', view, { autoMoveSingle: true, delays: false });
    const good = { v: 1, state: c.current, seats: bots(2), savedAt: '' };
    const bad = (mutate: (x: typeof good) => void) => {
      const copy = JSON.parse(JSON.stringify(good));
      mutate(copy);
      localStorage.setItem('ludocrown.offline.v1', JSON.stringify(copy));
      return loadSave();
    };
    expect(bad((x) => { x.state.players[0].pieces[0] = 99; })).toBeNull();
    expect(bad((x) => { x.state.currentPlayerIndex = 5; })).toBeNull();
    expect(bad((x) => { (x.state.rules as { mode: string }).mode = 'Turbo'; })).toBeNull();
    expect(bad((x) => { x.seats.pop(); })).toBeNull();
    // Stored rules are replaced by the mode's real rules.
    const fixed = bad((x) => { (x.state.rules as { piecesToWin: number }).piecesToWin = 0; });
    expect(fixed!.state.rules.piecesToWin).toBe(4);
  });

  it('describes events like the app does', () => {
    const view = new FakeView();
    const c = GameController.create(bots(2), 'Classic', view, { delays: false, autoMoveSingle: true });
    const s = c.current;
    expect(describeEvent({ type: 'NoLegalMoves', seat: 0, piece: -1, from: 0, to: 0, value: 4 }, s)).toBe('Bot0 rolled 4 — no moves.');
    expect(describeEvent({ type: 'PieceCaptured', seat: 1, piece: 0, from: 5, to: -1, value: 0 }, s)).toBe('Bot0 captured Bot1!');
    expect(describeEvent({ type: 'ThreeSixesForfeit', seat: 1, piece: -1, from: 0, to: 0, value: 6 }, s)).toBe("Three sixes! Bot1's turn is forfeited.");
  });
});
