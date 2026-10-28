// Replays traces recorded from the C# engine (tools/LudoCrown.TraceGen) and checks that the TypeScript port produces
// exactly the same results, errors, events, states, legal moves and bot scores at every step.
import { readFileSync } from 'node:fs';
import { gunzipSync } from 'node:zlib';
import { describe, expect, it } from 'vitest';
import { HardWeights, HeuristicBot, NormalWeights } from '../src/engine/bots';
import { type EngineResult, applyReroll, applyRoll, forfeitTurn, getLegalMoves, move, pass, useShield } from '../src/engine/engine';
import { type GameMode, type GameState, newGame, rulesForMode, standings } from '../src/engine/model';

interface Step {
  a: { t: string; seat: number; v?: number; hold?: boolean; p?: number };
  ok: boolean;
  err: string;
  ev: [string, number, number, number, number, number][];
  s: {
    cur: number; phase: string; pending: number | null; last: number | null; six: number; sixBefore: number;
    turn: number; ver: number; win: number | null;
    players: { pieces: number[]; rr: number; bu: number; sh: boolean }[];
  };
  legal?: { piece: number; from: number; to: number; captures: boolean; finishes: boolean; enters: boolean; normal: number; hard: number }[];
}
interface Game { players: number; mode: GameMode; maxBoosts: number; standings: number[]; steps: Step[] }

const traces = JSON.parse(gunzipSync(readFileSync(new URL('./fixtures/engine-traces.json.gz', import.meta.url))).toString('utf8')) as { games: Game[] };

const snapshot = (s: GameState): Step['s'] => ({
  cur: s.currentPlayerIndex, phase: s.phase, pending: s.pendingRoll, last: s.lastRoll, six: s.consecutiveSixes,
  sixBefore: s.sixesBeforePendingRoll, turn: s.turnNumber, ver: s.version, win: s.winnerSeat,
  players: s.players.map((p) => ({ pieces: p.pieces, rr: p.rerollsUsed, bu: p.boostsUsed, sh: p.shieldActive })),
});

const normal = new HeuristicBot({ ...NormalWeights, noise: 0 });
const hard = new HeuristicBot({ ...HardWeights, noise: 0 });

describe('TypeScript engine matches the C# engine', () => {
  it('has a meaningful number of recorded steps', () => {
    expect(traces.games.length).toBeGreaterThanOrEqual(10);
    expect(traces.games.reduce((n, g) => n + g.steps.length, 0)).toBeGreaterThan(5000);
  });

  traces.games.forEach((game, gi) => {
    it(`game ${gi}: ${game.players} players, ${game.mode}${game.maxBoosts ? ', shields' : ''}`, () => {
      let state = newGame(`trace-${gi}`, Array.from({ length: game.players }, (_, i) => ({ name: `P${i}`, kind: 'bot' as const })),
        { ...rulesForMode(game.mode), maxBoosts: game.maxBoosts });

      game.steps.forEach((step, si) => {
        const where = `game ${gi} step ${si} (${JSON.stringify(step.a)})`;
        if (step.legal) {
          const legal = getLegalMoves(state);
          expect(legal, where).toEqual(step.legal.map(({ normal: _n, hard: _h, ...m }) => m));
          legal.forEach((m, i) => {
            expect(normal.score(state, m), `${where} normal score`).toBeCloseTo(step.legal![i].normal, 9);
            expect(hard.score(state, m), `${where} hard score`).toBeCloseTo(step.legal![i].hard, 9);
          });
        }
        const a = step.a;
        let r: EngineResult;
        switch (a.t) {
          case 'roll': r = applyRoll(state, a.seat, a.v!, a.hold); break;
          case 'reroll': r = applyReroll(state, a.seat, a.v!, a.hold); break;
          case 'move': r = move(state, a.seat, a.p!); break;
          case 'pass': r = pass(state, a.seat); break;
          case 'forfeit': r = forfeitTurn(state, a.seat); break;
          case 'shield': r = useShield(state, a.seat); break;
          default: throw new Error(`unknown action ${a.t}`);
        }
        expect(r.ok, where).toBe(step.ok);
        expect(r.error, where).toBe(step.err);
        expect(r.events.map((e) => [e.type, e.seat, e.piece, e.from, e.to, e.value]), where).toEqual(step.ev);
        expect(snapshot(r.state), where).toEqual(step.s);
        state = r.state;
      });

      expect(standings(state)).toEqual(game.standings);
    });
  });
});
