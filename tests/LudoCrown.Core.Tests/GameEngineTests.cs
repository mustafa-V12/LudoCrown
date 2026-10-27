using LudoCrown.Core.Board;
using LudoCrown.Core.Dice;
using LudoCrown.Core.Engine;
using LudoCrown.Core.Model;

namespace LudoCrown.Core.Tests;

public class GameEngineTests
{
    // Seat 0 = Ruby (start abs 0); in two-player games seat 1 = Topaz (start abs 26).

    [Fact]
    public void New_game_has_all_pieces_in_yard_and_first_seat_to_roll()
    {
        var s = TestBoards.FourPlayer();
        Assert.All(s.Players, p => Assert.All(p.Pieces, x => Assert.Equal(BoardGeometry.Yard, x)));
        Assert.Equal(0, s.CurrentPlayer.Seat);
        Assert.Equal(GamePhase.AwaitingRoll, s.Phase);
        Assert.Equal(new[] { PlayerColor.Ruby, PlayerColor.Emerald, PlayerColor.Topaz, PlayerColor.Sapphire }, s.Players.Select(p => p.Color));
    }

    [Fact]
    public void Two_player_game_seats_opposite_colours()
    {
        var s = TestBoards.TwoPlayer();
        Assert.Equal(new[] { PlayerColor.Ruby, PlayerColor.Topaz }, s.Players.Select(p => p.Color));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(7)]
    public void Invalid_player_counts_are_rejected(int count)
    {
        var seats = Enumerable.Range(0, count).Select(i => new SeatSetup($"p{i}", $"P{i}", PlayerKind.Human)).ToList();
        Assert.Throws<ArgumentException>(() => GameSetup.NewGame("x", seats));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Without_a_six_no_piece_can_leave_the_yard_and_turn_passes(int roll)
    {
        var s = TestBoards.TwoPlayer();
        var r = GameEngine.ApplyRoll(s, 0, roll);
        Assert.True(r.Ok);
        Assert.Contains(r.Events, e => e.Type == GameEventType.NoLegalMoves);
        Assert.Equal(1, r.State.CurrentPlayer.Seat);
        Assert.Equal(GamePhase.AwaitingRoll, r.State.Phase);
    }

    [Fact]
    public void Rolling_a_six_lets_a_piece_enter_onto_the_start_square_and_grants_an_extra_roll()
    {
        var s = GameEngine.ApplyRoll(TestBoards.TwoPlayer(), 0, 6).State;
        Assert.Equal(GamePhase.AwaitingMove, s.Phase);
        Assert.Equal(4, GameEngine.GetLegalMoves(s).Count);

        var r = GameEngine.Move(s, 0, 2);
        Assert.True(r.Ok);
        Assert.Equal(BoardGeometry.StartProgress, r.State.Players[0].Pieces[2]);
        Assert.Contains(r.Events, e => e.Type == GameEventType.PieceEntered && e.Piece == 2);
        Assert.Contains(r.Events, e => e.Type == GameEventType.ExtraTurn);
        Assert.Equal(0, r.State.CurrentPlayer.Seat);
        Assert.Equal(GamePhase.AwaitingRoll, r.State.Phase);
    }

    [Fact]
    public void Custom_rules_can_allow_entry_on_one()
    {
        var rules = new RuleSet { EntryRolls = new[] { 1, 6 } };
        var s = GameSetup.NewGame("x", new[] { new SeatSetup("a", "A", PlayerKind.Human), new SeatSetup("b", "B", PlayerKind.Human) }, rules);
        var r = GameEngine.ApplyRoll(s, 0, 1);
        Assert.Equal(4, GameEngine.GetLegalMoves(r.State).Count);
    }

    [Fact]
    public void A_normal_move_advances_by_the_roll_and_passes_the_turn()
    {
        var s = TestBoards.TwoPlayer().WithPieces(0, 10);
        s = GameEngine.ApplyRoll(s, 0, 4).State;
        var r = GameEngine.Move(s, 0, 0);
        Assert.True(r.Ok);
        Assert.Equal(14, r.State.Players[0].Pieces[0]);
        Assert.Equal(1, r.State.CurrentPlayer.Seat);
        Assert.Contains(r.Events, e => e.Type == GameEventType.TurnPassed);
    }

    [Fact]
    public void Landing_on_an_opponent_captures_it_and_grants_an_extra_roll()
    {
        // Ruby piece at abs 10; Topaz piece at progress 38 = abs (26+38)%52 = 12. Ruby rolls 2.
        var s = TestBoards.TwoPlayer().WithPieces(0, 10).WithPieces(1, 38);
        s = GameEngine.ApplyRoll(s, 0, 2).State;
        var move = Assert.Single(GameEngine.GetLegalMoves(s));
        Assert.True(move.Captures);

        var r = GameEngine.Move(s, 0, 0);
        Assert.Equal(BoardGeometry.Yard, r.State.Players[1].Pieces[0]);
        Assert.Contains(r.Events, e => e.Type == GameEventType.PieceCaptured && e.Seat == 1 && e.Piece == 0);
        Assert.Equal(0, r.State.CurrentPlayer.Seat);
        Assert.Equal(0, r.State.ConsecutiveSixes);
    }

    [Fact]
    public void Capture_extra_turn_can_be_disabled()
    {
        var rules = new RuleSet { CaptureGrantsExtraTurn = false };
        var s = TestBoards.TwoPlayer();
        s.Rules = rules;
        s = s.WithPieces(0, 10).WithPieces(1, 38);
        s = GameEngine.ApplyRoll(s, 0, 2).State;
        var r = GameEngine.Move(s, 0, 0);
        Assert.Equal(1, r.State.CurrentPlayer.Seat);
    }

    [Fact]
    public void Pieces_on_star_squares_are_safe_from_capture()
    {
        // Abs 8 is a star. Topaz progress 34 = abs (26+34)%52 = 8.
        var s = TestBoards.TwoPlayer().WithPieces(0, 5).WithPieces(1, 34);
        s = GameEngine.ApplyRoll(s, 0, 3).State;
        var move = Assert.Single(GameEngine.GetLegalMoves(s));
        Assert.False(move.Captures);
        var r = GameEngine.Move(s, 0, 0);
        Assert.Equal(34, r.State.Players[1].Pieces[0]);
        Assert.Equal(8, r.State.Players[0].Pieces[0]);
        Assert.DoesNotContain(r.Events, e => e.Type == GameEventType.PieceCaptured);
    }

    [Fact]
    public void Pieces_on_start_squares_are_safe_from_capture()
    {
        // Topaz start = abs 26 = Ruby progress 26.
        var s = TestBoards.TwoPlayer().WithPieces(0, 22).WithPieces(1, 0);
        s = GameEngine.ApplyRoll(s, 0, 4).State;
        var r = GameEngine.Move(s, 0, 0);
        Assert.Equal(0, r.State.Players[1].Pieces[0]);
    }

    [Fact]
    public void Entering_onto_start_square_does_not_capture_an_opponent_there()
    {
        // Topaz piece sitting on Ruby's start square (abs 0 = Topaz progress 26).
        var s = TestBoards.TwoPlayer().WithPieces(1, 26);
        s = GameEngine.ApplyRoll(s, 0, 6).State;
        var r = GameEngine.Move(s, 0, 0);
        Assert.Equal(26, r.State.Players[1].Pieces[0]);
    }

    [Fact]
    public void Landing_on_multiple_opponent_pieces_captures_all_of_them()
    {
        var s = TestBoards.FourPlayer()
            .WithPieces(0, 15)
            // Emerald progress 5 => abs 18; Topaz progress 44 => abs (26+44)%52 = 18.
            .WithPieces(1, 5)
            .WithPieces(2, 44);
        s = GameEngine.ApplyRoll(s, 0, 3).State;
        var r = GameEngine.Move(s, 0, 0);
        Assert.Equal(BoardGeometry.Yard, r.State.Players[1].Pieces[0]);
        Assert.Equal(BoardGeometry.Yard, r.State.Players[2].Pieces[0]);
        Assert.Equal(2, r.Events.Count(e => e.Type == GameEventType.PieceCaptured));
    }

    [Fact]
    public void Own_pieces_can_share_a_square()
    {
        var s = TestBoards.TwoPlayer().WithPieces(0, 10, 12);
        s = GameEngine.ApplyRoll(s, 0, 2).State;
        var r = GameEngine.Move(s, 0, 0);
        Assert.True(r.Ok);
        Assert.Equal(new[] { 12, 12, -1, -1 }, r.State.Players[0].Pieces);
    }

    [Fact]
    public void Piece_moves_from_the_main_track_into_the_home_column()
    {
        var s = TestBoards.TwoPlayer().WithPieces(0, 48);
        s = GameEngine.ApplyRoll(s, 0, 5).State;
        var r = GameEngine.Move(s, 0, 0);
        Assert.Equal(53, r.State.Players[0].Pieces[0]);
    }

    [Fact]
    public void Pieces_in_the_home_column_cannot_be_captured()
    {
        // Topaz piece at 50 rolling 2 would reach 52 (home column), never Ruby's abs squares.
        var s = TestBoards.TwoPlayer().WithPieces(0, 53).WithPieces(1, 50).WithTurn(1);
        s = GameEngine.ApplyRoll(s, 1, 2).State;
        var r = GameEngine.Move(s, 1, 0);
        Assert.Equal(53, r.State.Players[0].Pieces[0]);
        Assert.Equal(52, r.State.Players[1].Pieces[0]);
    }

    [Fact]
    public void Finishing_requires_an_exact_roll()
    {
        var s = TestBoards.TwoPlayer().WithPieces(0, 54);
        var r = GameEngine.ApplyRoll(s, 0, 3);
        Assert.Contains(r.Events, e => e.Type == GameEventType.NoLegalMoves);

        s = GameEngine.ApplyRoll(TestBoards.TwoPlayer().WithPieces(0, 54), 0, 2).State;
        var move = Assert.Single(GameEngine.GetLegalMoves(s));
        Assert.True(move.Finishes);
    }

    [Fact]
    public void Finishing_a_piece_grants_an_extra_roll()
    {
        var s = TestBoards.TwoPlayer().WithPieces(0, 54, 10);
        s = GameEngine.ApplyRoll(s, 0, 2).State;
        var r = GameEngine.Move(s, 0, 0);
        Assert.Equal(56, r.State.Players[0].Pieces[0]);
        Assert.Contains(r.Events, e => e.Type == GameEventType.PieceFinished);
        Assert.Equal(0, r.State.CurrentPlayer.Seat);
    }

    [Fact]
    public void Finished_pieces_cannot_move()
    {
        var s = TestBoards.TwoPlayer().WithPieces(0, 56, 10);
        s = GameEngine.ApplyRoll(s, 0, 1).State;
        Assert.Equal(new[] { 1 }, GameEngine.GetLegalMoves(s).Select(m => m.Piece));
        Assert.Equal(ErrorCode.IllegalMove, GameEngine.Move(s, 0, 0).Error);
    }

    [Fact]
    public void Bringing_the_last_piece_home_wins_the_game()
    {
        var s = TestBoards.TwoPlayer().WithPieces(0, 56, 56, 56, 51);
        s = GameEngine.ApplyRoll(s, 0, 5).State;
        var r = GameEngine.Move(s, 0, 3);
        Assert.Equal(GamePhase.Finished, r.State.Phase);
        Assert.Equal(0, r.State.WinnerSeat);
        Assert.Contains(r.Events, e => e.Type == GameEventType.GameWon);
        Assert.Equal(0, r.State.Standings()[0]);

        Assert.Equal(ErrorCode.GameOver, GameEngine.ApplyRoll(r.State, 1, 3).Error);
        Assert.Equal(ErrorCode.GameOver, GameEngine.Move(r.State, 0, 0).Error);
    }

    [Fact]
    public void Three_sixes_in_a_row_forfeit_the_turn()
    {
        var s = TestBoards.TwoPlayer().WithPieces(0, 0);
        s = GameEngine.Move(GameEngine.ApplyRoll(s, 0, 6).State, 0, 0).State;  // 0 -> 6
        s = GameEngine.Move(GameEngine.ApplyRoll(s, 0, 6).State, 0, 0).State;  // 6 -> 12
        Assert.Equal(0, s.CurrentPlayer.Seat);
        Assert.Equal(2, s.ConsecutiveSixes);

        var r = GameEngine.ApplyRoll(s, 0, 6);
        Assert.Contains(r.Events, e => e.Type == GameEventType.ThreeSixesForfeit);
        Assert.Equal(1, r.State.CurrentPlayer.Seat);
        Assert.Equal(12, r.State.Players[0].Pieces[0]);
        Assert.Equal(0, r.State.ConsecutiveSixes);
    }

    [Fact]
    public void Extra_turn_from_capture_resets_the_six_counter()
    {
        var s = TestBoards.TwoPlayer().WithPieces(0, 10).WithPieces(1, 38);
        s.ConsecutiveSixes = 0;
        s = GameEngine.Move(GameEngine.ApplyRoll(s, 0, 2).State, 0, 0).State;
        Assert.Equal(0, s.ConsecutiveSixes);
    }

    [Fact]
    public void Acting_out_of_turn_or_in_the_wrong_phase_is_rejected_without_changing_state()
    {
        var s = TestBoards.TwoPlayer();
        var wrongSeat = GameEngine.ApplyRoll(s, 1, 6);
        Assert.False(wrongSeat.Ok);
        Assert.Equal(ErrorCode.NotYourTurn, wrongSeat.Error);
        Assert.Same(s, wrongSeat.State);

        Assert.Equal(ErrorCode.WrongPhase, GameEngine.Move(s, 0, 0).Error);

        var awaitingMove = GameEngine.ApplyRoll(s, 0, 6).State;
        Assert.Equal(ErrorCode.WrongPhase, GameEngine.ApplyRoll(awaitingMove, 0, 6).Error);
        Assert.Equal(ErrorCode.InvalidPiece, GameEngine.Move(awaitingMove, 0, 4).Error);
        Assert.Equal(ErrorCode.InvalidPiece, GameEngine.Move(awaitingMove, 0, -1).Error);
        Assert.Equal(ErrorCode.NotYourTurn, GameEngine.Move(awaitingMove, 1, 0).Error);
    }

    [Fact]
    public void Illegal_piece_choice_is_rejected()
    {
        var s = TestBoards.TwoPlayer().WithPieces(0, 10);
        s = GameEngine.ApplyRoll(s, 0, 3).State;
        Assert.Equal(ErrorCode.IllegalMove, GameEngine.Move(s, 0, 1).Error); // piece 1 is in the yard
    }

    [Fact]
    public void Engine_never_mutates_its_input_and_bumps_version()
    {
        var s = TestBoards.TwoPlayer().WithPieces(0, 10);
        var before = Serialization.GameStateSerializer.Serialize(s);
        var r1 = GameEngine.ApplyRoll(s, 0, 3);
        var r2 = GameEngine.Move(r1.State, 0, 0);
        Assert.Equal(before, Serialization.GameStateSerializer.Serialize(s));
        Assert.Equal(s.Version + 1, r1.State.Version);
        Assert.Equal(s.Version + 2, r2.State.Version);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void Out_of_range_die_values_throw(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GameEngine.ApplyRoll(TestBoards.TwoPlayer(), 0, value));
    }

    [Fact]
    public void Roll_uses_the_injected_dice()
    {
        var dice = new ScriptedDiceRoller(6);
        var r = GameEngine.Roll(TestBoards.TwoPlayer(), 0, dice);
        Assert.Equal(6, r.State.PendingRoll);
        Assert.Equal(0, dice.Remaining);
    }

    [Fact]
    public void Turn_order_rotates_through_all_four_seats()
    {
        var s = TestBoards.FourPlayer();
        var seen = new List<int>();
        for (var i = 0; i < 5; i++)
        {
            seen.Add(s.CurrentPlayer.Seat);
            s = GameEngine.ApplyRoll(s, s.CurrentPlayer.Seat, 2).State;
        }
        Assert.Equal(new[] { 0, 1, 2, 3, 0 }, seen);
    }

    [Fact]
    public void Forfeit_turn_passes_to_next_player()
    {
        var s = GameEngine.ApplyRoll(TestBoards.TwoPlayer(), 0, 6).State;
        var r = GameEngine.ForfeitTurn(s, 0);
        Assert.True(r.Ok);
        Assert.Equal(1, r.State.CurrentPlayer.Seat);
        Assert.Null(r.State.PendingRoll);
        Assert.Equal(ErrorCode.NotYourTurn, GameEngine.ForfeitTurn(r.State, 0).Error);
    }

    [Fact]
    public void A_full_random_game_always_terminates_with_a_winner()
    {
        for (var seed = 0; seed < 30; seed++)
        {
            var dice = new SeededDiceRoller(seed);
            var bot = AI.BotFactory.Create(AI.BotDifficulty.Normal, new Random(seed));
            var s = seed % 2 == 0 ? TestBoards.FourPlayer() : TestBoards.TwoPlayer();
            var steps = 0;
            while (s.Phase != GamePhase.Finished)
            {
                Assert.True(steps++ < 20000, "game did not terminate");
                var seat = s.CurrentPlayer.Seat;
                var r = s.Phase == GamePhase.AwaitingRoll ? GameEngine.Roll(s, seat, dice) : GameEngine.Move(s, seat, bot.ChooseMove(s)!.Value);
                Assert.True(r.Ok, r.Error.ToString());
                s = r.State;
                Serialization.GameStateSerializer.Validate(s);
            }
            Assert.NotNull(s.WinnerSeat);
            Assert.True(s.PlayerBySeat(s.WinnerSeat!.Value)!.HasFinished);
        }
    }
}
