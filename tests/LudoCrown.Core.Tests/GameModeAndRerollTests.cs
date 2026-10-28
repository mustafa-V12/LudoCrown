using LudoCrown.Core.AI;
using LudoCrown.Core.Board;
using LudoCrown.Core.Dice;
using LudoCrown.Core.Engine;
using LudoCrown.Core.Model;
using LudoCrown.Core.Serialization;

namespace LudoCrown.Core.Tests;

public class GameModeTests
{
    private static GameState New(GameMode mode, int players = 2) => GameSetup.NewGame("m", Enumerable.Range(0, players)
        .Select(i => new SeatSetup($"p{i}", $"P{i}", PlayerKind.Human)).ToList(), RuleSet.ForMode(mode));

    [Fact]
    public void Registry_lists_only_implemented_modes_within_the_five_mode_limit()
    {
        Assert.Equal(new[] { GameMode.Classic, GameMode.Quick }, GameModes.All.Select(m => m.Mode));
        Assert.True(GameModes.All.Count <= GameModes.MaxModes);
        Assert.All(GameModes.All, m => Assert.Equal(m.Mode, m.Rules.Mode));
    }

    [Fact]
    public void Mode_rules_match_the_specification()
    {
        Assert.Equal(4, RuleSet.ForMode(GameMode.Classic).PiecesToWin);
        Assert.Equal(7, RuleSet.ForMode(GameMode.Classic).MaxRerolls);
        Assert.Equal(1, RuleSet.ForMode(GameMode.Quick).PiecesToWin);
        Assert.Equal(5, RuleSet.ForMode(GameMode.Quick).MaxRerolls);
        Assert.Equal(1, GameModes.RerollGemCost);
    }

    [Theory]
    [InlineData("classic", GameMode.Classic)]
    [InlineData("Quick", GameMode.Quick)]
    [InlineData("1", GameMode.Quick)]
    [InlineData(" QUICK ", GameMode.Quick)]
    public void Mode_ids_parse(string input, GameMode expected) => Assert.Equal(expected, GameModes.Parse(input));

    [Theory]
    [InlineData("turbo")]
    [InlineData("4")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_modes_are_rejected(string? input) => Assert.Null(GameModes.Parse(input));

    [Fact]
    public void Quick_mode_ends_when_the_first_piece_reaches_the_crown()
    {
        var s = New(GameMode.Quick).WithPieces(0, 54, 10, -1, -1);
        s = GameEngine.ApplyRoll(s, 0, 2).State;
        var r = GameEngine.Move(s, 0, 0);
        Assert.Equal(GamePhase.Finished, r.State.Phase);
        Assert.Equal(0, r.State.WinnerSeat);
        Assert.Contains(r.Events, e => e.Type == GameEventType.GameWon);
        Assert.Equal(0, r.State.Standings()[0]);
    }

    [Fact]
    public void Classic_mode_does_not_end_on_the_first_piece()
    {
        var s = New(GameMode.Classic).WithPieces(0, 54, 10, -1, -1);
        s = GameEngine.ApplyRoll(s, 0, 2).State;
        var r = GameEngine.Move(s, 0, 0);
        Assert.NotEqual(GamePhase.Finished, r.State.Phase);
        Assert.Null(r.State.WinnerSeat);
        Assert.Contains(r.Events, e => e.Type == GameEventType.PieceFinished);
        Assert.Contains(r.Events, e => e.Type == GameEventType.ExtraTurn);
    }

    [Fact]
    public void Classic_mode_requires_all_four_pieces()
    {
        var s = New(GameMode.Classic).WithPieces(0, 56, 56, 56, 53);
        s = GameEngine.ApplyRoll(s, 0, 3).State;
        var r = GameEngine.Move(s, 0, 3);
        Assert.Equal(GamePhase.Finished, r.State.Phase);
        Assert.Equal(0, r.State.WinnerSeat);
    }

    [Theory]
    [InlineData(GameMode.Classic)]
    [InlineData(GameMode.Quick)]
    public void Legal_and_illegal_moves_follow_the_same_rules_in_both_modes(GameMode mode)
    {
        var s = New(mode).WithPieces(0, 54, 10, -1, -1);
        s = GameEngine.ApplyRoll(s, 0, 3).State;
        // Piece 0 would overshoot the crown (needs exactly 2); yard pieces need a 6.
        Assert.Equal(new[] { 1 }, GameEngine.GetLegalMoves(s).Select(m => m.Piece));
        Assert.Equal(ErrorCode.IllegalMove, GameEngine.Move(s, 0, 0).Error);
        Assert.Equal(ErrorCode.IllegalMove, GameEngine.Move(s, 0, 2).Error);
        Assert.Equal(ErrorCode.NotYourTurn, GameEngine.Move(s, 1, 1).Error);
        Assert.True(GameEngine.Move(s, 0, 1).Ok);
    }

    [Theory]
    [InlineData(GameMode.Classic)]
    [InlineData(GameMode.Quick)]
    public void Full_bot_games_finish_with_a_winner_that_meets_the_mode_condition(GameMode mode)
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var dice = new SeededDiceRoller(seed);
            var bot = BotFactory.Create(BotDifficulty.Normal, new Random(seed));
            var s = New(mode, seed % 2 == 0 ? 4 : 2);
            var guard = 0;
            while (s.Phase != GamePhase.Finished && guard++ < 20000)
            {
                var seat = s.CurrentPlayer.Seat;
                s = (s.Phase == GamePhase.AwaitingRoll ? GameEngine.Roll(s, seat, dice) : GameEngine.Move(s, seat, bot.ChooseMove(s)!.Value)).State;
            }
            var winner = s.PlayerBySeat(s.WinnerSeat!.Value)!;
            Assert.True(s.HasWon(winner));
            if (mode == GameMode.Quick)
            {
                Assert.Equal(1, winner.FinishedCount); // ends immediately at the first crowned piece
                Assert.All(s.Players.Where(p => p.Seat != winner.Seat), p => Assert.Equal(0, p.FinishedCount));
            }
            else Assert.Equal(4, winner.FinishedCount);
        }
    }

    [Fact]
    public void Quick_games_are_shorter_than_classic_games_on_average()
    {
        int Turns(GameMode mode)
        {
            var total = 0;
            for (var seed = 0; seed < 20; seed++)
            {
                var dice = new SeededDiceRoller(seed);
                var bot = BotFactory.Create(BotDifficulty.Normal, new Random(seed));
                var s = New(mode);
                while (s.Phase != GamePhase.Finished)
                {
                    var seat = s.CurrentPlayer.Seat;
                    s = (s.Phase == GamePhase.AwaitingRoll ? GameEngine.Roll(s, seat, dice) : GameEngine.Move(s, seat, bot.ChooseMove(s)!.Value)).State;
                }
                total += s.TurnNumber;
            }
            return total;
        }
        Assert.True(Turns(GameMode.Quick) < Turns(GameMode.Classic));
    }

    [Fact]
    public void Mode_and_rules_survive_save_and_load()
    {
        var s = New(GameMode.Quick);
        s.Players[0].RerollsUsed = 3;
        var back = GameStateSerializer.Deserialize(GameStateSerializer.Serialize(s));
        Assert.Equal(GameMode.Quick, back.Mode);
        Assert.Equal(1, back.Rules.PiecesToWin);
        Assert.Equal(5, back.Rules.MaxRerolls);
        Assert.Equal(3, back.Players[0].RerollsUsed);
    }

    [Fact]
    public void Saves_from_before_modes_existed_load_as_classic()
    {
        var json = GameStateSerializer.Serialize(New(GameMode.Classic))
            .Replace("\"mode\":\"Classic\",", "").Replace("\"piecesToWin\":4,", "").Replace("\"maxRerolls\":7,", "").Replace("\"rerollsUsed\":0,", "");
        var back = GameStateSerializer.Deserialize(json);
        Assert.Equal(GameMode.Classic, back.Mode);
        Assert.Equal(4, back.Rules.PiecesToWin);
    }

    [Fact]
    public void Tampered_mode_data_is_rejected()
    {
        var s = New(GameMode.Quick);
        s.Players[0].RerollsUsed = 6; // above Quick's limit of 5
        Assert.Throws<System.Text.Json.JsonException>(() => GameStateSerializer.Deserialize(GameStateSerializer.Serialize(s)));
    }
}

public class RerollTests
{
    private static GameState New(GameMode mode) => GameSetup.NewGame("r", new[]
    {
        new SeatSetup("a", "A", PlayerKind.Human), new SeatSetup("b", "B", PlayerKind.Human),
    }, RuleSet.ForMode(mode));

    [Fact]
    public void Reroll_replaces_the_pending_roll_and_counts_one_use()
    {
        var s = New(GameMode.Classic).WithPieces(0, 10);
        s = GameEngine.ApplyRoll(s, 0, 2).State;
        var r = GameEngine.ApplyReroll(s, 0, 5);
        Assert.True(r.Ok);
        Assert.Equal(5, r.State.PendingRoll);
        Assert.Equal(1, r.State.Players[0].RerollsUsed);
        Assert.Contains(r.Events, e => e.Type == GameEventType.DiceRerolled && e.From == 2 && e.Value == 5);
        Assert.Equal(15, GameEngine.Move(r.State, 0, 0).State.Players[0].Pieces[0]);
    }

    [Theory]
    [InlineData(GameMode.Classic, 7)]
    [InlineData(GameMode.Quick, 5)]
    public void Reroll_limit_per_match_depends_on_the_mode(GameMode mode, int limit)
    {
        var s = New(mode).WithPieces(0, 10);
        s = GameEngine.ApplyRoll(s, 0, 2).State;
        for (var i = 0; i < limit; i++)
        {
            var r = GameEngine.ApplyReroll(s, 0, 2);
            Assert.True(r.Ok, $"reroll {i + 1} should be allowed");
            s = r.State;
        }
        Assert.Equal(limit, s.Players[0].RerollsUsed);
        Assert.Equal(0, s.RerollsRemaining(s.Players[0]));
        var blocked = GameEngine.ApplyReroll(s, 0, 4);
        Assert.False(blocked.Ok);
        Assert.Equal(ErrorCode.NoRerollsLeft, blocked.Error);
        Assert.Same(s, blocked.State);
    }

    [Fact]
    public void Reroll_is_not_allowed_before_rolling_after_moving_or_out_of_turn()
    {
        var s = New(GameMode.Classic).WithPieces(0, 10).WithPieces(1, 20);
        Assert.Equal(ErrorCode.RerollNotAllowed, GameEngine.ApplyReroll(s, 0, 3).Error); // nothing to replace
        Assert.Equal(ErrorCode.NotYourTurn, GameEngine.ApplyReroll(s, 1, 3).Error);

        s = GameEngine.ApplyRoll(s, 0, 2).State;
        Assert.Equal(ErrorCode.NotYourTurn, GameEngine.ApplyReroll(s, 1, 3).Error);
        s = GameEngine.Move(s, 0, 0).State; // move committed, turn passed
        Assert.Equal(ErrorCode.NotYourTurn, GameEngine.ApplyReroll(s, 0, 3).Error);
    }

    [Fact]
    public void Reroll_after_a_committed_move_with_extra_turn_is_not_allowed_until_the_next_roll()
    {
        var s = New(GameMode.Classic).WithPieces(0, 10);
        s = GameEngine.ApplyRoll(s, 0, 6).State;
        s = GameEngine.Move(s, 0, 0).State; // six → extra roll, but this roll is used up
        Assert.Equal(GamePhase.AwaitingRoll, s.Phase);
        Assert.Equal(ErrorCode.RerollNotAllowed, GameEngine.ApplyReroll(s, 0, 3).Error);
    }

    [Fact]
    public void Rerolling_away_a_six_does_not_count_toward_three_sixes()
    {
        var s = New(GameMode.Classic).WithPieces(0, 0);
        s = GameEngine.Move(GameEngine.ApplyRoll(s, 0, 6).State, 0, 0).State; // first six used
        s = GameEngine.ApplyRoll(s, 0, 6).State;                                // second six pending
        Assert.Equal(2, s.ConsecutiveSixes);
        s = GameEngine.ApplyReroll(s, 0, 3).State;                              // replaced by a 3
        Assert.Equal(0, s.ConsecutiveSixes);
        Assert.Equal(3, s.PendingRoll);
    }

    [Fact]
    public void Rerolling_into_a_third_six_forfeits_the_turn()
    {
        var s = New(GameMode.Classic).WithPieces(0, 0);
        s = GameEngine.Move(GameEngine.ApplyRoll(s, 0, 6).State, 0, 0).State;
        s = GameEngine.Move(GameEngine.ApplyRoll(s, 0, 6).State, 0, 0).State;
        s = GameEngine.ApplyRoll(s, 0, 2).State;
        var r = GameEngine.ApplyReroll(s, 0, 6);
        Assert.Contains(r.Events, e => e.Type == GameEventType.ThreeSixesForfeit);
        Assert.Equal(1, r.State.CurrentPlayer.Seat);
    }

    [Fact]
    public void A_roll_with_no_moves_can_be_held_for_a_reroll_or_passed()
    {
        var s = New(GameMode.Quick); // everyone in the yard
        var held = GameEngine.ApplyRoll(s, 0, 3, holdWhenNoMoves: true).State;
        Assert.Equal(GamePhase.AwaitingMove, held.Phase);
        Assert.Equal(0, held.CurrentPlayer.Seat);
        Assert.True(GameEngine.IsHeldWithoutMoves(held));
        Assert.Empty(GameEngine.GetLegalMoves(held));

        // Reroll into a 6 → now a piece can enter.
        var rerolled = GameEngine.ApplyReroll(held, 0, 6, holdWhenNoMoves: true).State;
        Assert.Equal(4, GameEngine.GetLegalMoves(rerolled).Count);

        // Or give up: pass ends the turn.
        var passed = GameEngine.Pass(held, 0);
        Assert.True(passed.Ok);
        Assert.Equal(1, passed.State.CurrentPlayer.Seat);
        Assert.Contains(passed.Events, e => e.Type == GameEventType.NoLegalMoves);
    }

    [Fact]
    public void Without_hold_a_no_move_roll_still_passes_automatically()
    {
        var r = GameEngine.ApplyRoll(New(GameMode.Classic), 0, 3);
        Assert.Equal(1, r.State.CurrentPlayer.Seat);
    }

    [Fact]
    public void Pass_is_rejected_when_a_move_is_available()
    {
        var s = GameEngine.ApplyRoll(New(GameMode.Classic).WithPieces(0, 10), 0, 3).State;
        Assert.Equal(ErrorCode.IllegalMove, GameEngine.Pass(s, 0).Error);
        Assert.Equal(ErrorCode.WrongPhase, GameEngine.Pass(New(GameMode.Classic), 0).Error);
    }

    [Fact]
    public void Reroll_uses_injected_dice_and_never_a_client_value()
    {
        var s = GameEngine.ApplyRoll(New(GameMode.Classic).WithPieces(0, 10), 0, 1).State;
        var dice = new ScriptedDiceRoller(4);
        var r = GameEngine.Reroll(s, 0, dice);
        Assert.Equal(4, r.State.PendingRoll);
        Assert.Equal(0, dice.Remaining);
    }

    [Fact]
    public void Reroll_counts_are_per_player()
    {
        var s = New(GameMode.Quick).WithPieces(0, 10).WithPieces(1, 20);
        s = GameEngine.ApplyReroll(GameEngine.ApplyRoll(s, 0, 2).State, 0, 3).State;
        s = GameEngine.Move(s, 0, 0).State;
        Assert.Equal(1, s.Players[0].RerollsUsed);
        Assert.Equal(0, s.Players[1].RerollsUsed);
        Assert.Equal(5, s.RerollsRemaining(s.Players[1]));
    }
}
