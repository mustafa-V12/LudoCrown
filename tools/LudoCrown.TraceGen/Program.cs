using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using LudoCrown.Core.AI;
using LudoCrown.Core.Engine;
using LudoCrown.Core.Model;

// Plays reproducible games with the real engine, choosing actions (including rerolls, held rolls, passes, shields and
// deliberately invalid commands) from a seeded random source, and records everything the TypeScript port must match.
var output = args.Length > 0 ? args[0] : "engine-traces.json.gz";
var games = new JsonArray();
var normal = BotWeights.Normal with { Noise = 0 };
var hard = BotWeights.Hard with { Noise = 0 };

for (var g = 0; g < 18; g++)
{
    var rng = new Random(1000 + g);
    var players = 2 + g % 3;
    var mode = g % 2 == 0 ? GameMode.Classic : GameMode.Quick;
    var boosts = g % 3 == 0 ? 1 : 0;
    var rules = RuleSet.ForMode(mode) with { MaxBoosts = boosts };
    var seats = Enumerable.Range(0, players).Select(i => new SeatSetup($"p{i}", $"P{i}", PlayerKind.Bot)).ToList();
    var state = GameSetup.NewGame($"trace-{g}", seats, rules);
    var steps = new JsonArray();

    for (var n = 0; n < 4000 && state.Phase != GamePhase.Finished; n++)
    {
        var seat = state.CurrentPlayer.Seat;
        JsonObject action;
        EngineResult result;
        JsonArray? legal = null;

        if (state.Phase == GamePhase.AwaitingMove)
        {
            var moves = GameEngine.GetLegalMoves(state);
            legal = new JsonArray(moves.Select(m => (JsonNode)new JsonObject
            {
                ["piece"] = m.Piece, ["from"] = m.From, ["to"] = m.To,
                ["captures"] = m.Captures, ["finishes"] = m.Finishes, ["enters"] = m.Enters,
                ["normal"] = new HeuristicBot(normal, new Random(0)).Score(state, m),
                ["hard"] = new HeuristicBot(hard, new Random(0)).Score(state, m),
            }).ToArray());
        }

        var roll = rng.NextDouble();
        if (roll < 0.04)
        {
            // Invalid command on purpose: wrong seat, or a random piece that may be illegal.
            var wrongSeat = (seat + 1) % players;
            action = new JsonObject { ["t"] = "move", ["seat"] = wrongSeat, ["p"] = rng.Next(4) };
            result = GameEngine.Move(state, wrongSeat, (int)action["p"]!);
        }
        else if (state.Phase == GamePhase.AwaitingRoll)
        {
            if (boosts > 0 && rng.NextDouble() < 0.15)
            {
                action = new JsonObject { ["t"] = "shield", ["seat"] = seat };
                result = GameEngine.UseShield(state, seat);
            }
            else if (rng.NextDouble() < 0.03)
            {
                action = new JsonObject { ["t"] = "reroll", ["seat"] = seat, ["v"] = 3, ["hold"] = false };
                result = GameEngine.ApplyReroll(state, seat, 3);
            }
            else
            {
                var v = rng.Next(1, 7);
                var hold = rng.NextDouble() < 0.3;
                action = new JsonObject { ["t"] = "roll", ["seat"] = seat, ["v"] = v, ["hold"] = hold };
                result = GameEngine.ApplyRoll(state, seat, v, hold);
            }
        }
        else
        {
            var moves = GameEngine.GetLegalMoves(state);
            if (rng.NextDouble() < 0.12)
            {
                var v = rng.Next(1, 7);
                var hold = rng.NextDouble() < 0.5;
                action = new JsonObject { ["t"] = "reroll", ["seat"] = seat, ["v"] = v, ["hold"] = hold };
                result = GameEngine.ApplyReroll(state, seat, v, hold);
            }
            else if (moves.Count == 0)
            {
                action = new JsonObject { ["t"] = "pass", ["seat"] = seat };
                result = GameEngine.Pass(state, seat);
            }
            else if (rng.NextDouble() < 0.05)
            {
                action = new JsonObject { ["t"] = "pass", ["seat"] = seat };
                result = GameEngine.Pass(state, seat); // must fail: moves exist
            }
            else if (rng.NextDouble() < 0.03)
            {
                action = new JsonObject { ["t"] = "forfeit", ["seat"] = seat };
                result = GameEngine.ForfeitTurn(state, seat);
            }
            else
            {
                var p = rng.NextDouble() < 0.1 ? rng.Next(4) : moves[rng.Next(moves.Count)].Piece;
                action = new JsonObject { ["t"] = "move", ["seat"] = seat, ["p"] = p };
                result = GameEngine.Move(state, seat, p);
            }
        }

        var step = new JsonObject
        {
            ["a"] = action,
            ["ok"] = result.Ok,
            ["err"] = result.Error.ToString(),
            ["ev"] = new JsonArray(result.Events.Select(e => (JsonNode)new JsonArray(e.Type.ToString(), e.Seat, e.Piece, e.From, e.To, e.Value)).ToArray()),
            ["s"] = Snapshot(result.State),
        };
        if (legal is not null) step["legal"] = legal;
        steps.Add(step);
        state = result.State;
    }

    games.Add(new JsonObject
    {
        ["players"] = players,
        ["mode"] = mode.ToString(),
        ["maxBoosts"] = boosts,
        ["standings"] = new JsonArray(state.Standings().Select(s => (JsonNode)s).ToArray()),
        ["steps"] = steps,
    });
}

var json = new JsonObject { ["generatedBy"] = "tools/LudoCrown.TraceGen", ["games"] = games }.ToJsonString();
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
await using (var file = File.Create(output))
await using (var gz = new GZipStream(file, CompressionLevel.SmallestSize))
await using (var w = new StreamWriter(gz)) await w.WriteAsync(json);
Console.WriteLine($"Wrote {games.Count} games, {games.Sum(x => x!["steps"]!.AsArray().Count)} steps, {json.Length / 1024} KB json -> {output}");

static JsonObject Snapshot(GameState s) => new()
{
    ["cur"] = s.CurrentPlayerIndex,
    ["phase"] = s.Phase.ToString(),
    ["pending"] = s.PendingRoll,
    ["last"] = s.LastRoll,
    ["six"] = s.ConsecutiveSixes,
    ["sixBefore"] = s.SixesBeforePendingRoll,
    ["turn"] = s.TurnNumber,
    ["ver"] = s.Version,
    ["win"] = s.WinnerSeat,
    ["players"] = new JsonArray(s.Players.Select(p => (JsonNode)new JsonObject
    {
        ["pieces"] = new JsonArray(p.Pieces.Select(x => (JsonNode)x).ToArray()),
        ["rr"] = p.RerollsUsed,
        ["bu"] = p.BoostsUsed,
        ["sh"] = p.ShieldActive,
    }).ToArray()),
};
