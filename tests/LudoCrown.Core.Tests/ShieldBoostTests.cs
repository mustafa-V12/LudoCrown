using LudoCrown.Core.AI;
using LudoCrown.Core.Board;
using LudoCrown.Core.Dice;
using LudoCrown.Core.Engine;
using LudoCrown.Core.Model;
using LudoCrown.Core.Serialization;

namespace LudoCrown.Core.Tests;

public class ShieldBoostTests
{
    private static GameState Casual(GameMode mode = GameMode.Classic) => GameSetup.NewGame("m", new[]
    {
        new SeatSetup("a", "A", PlayerKind.Human), new SeatSetup("b", "B", PlayerKind.Human),
    }, RuleSet.ForMode(mode) with { MaxBoosts = 1 });

    /// <summary>
    /// A Ruby (seat 0) progress on an unsafe square, and the Topaz (seat 1) progress 3 squares behind it, so Topaz rolling 3 lands on it.
    /// </summary>
    private static (int Ruby, int TopazFrom) CaptureSetup()
    {
        for (var r = 1; r < 20; r++)
        {
            if (BoardGeometry.IsSafeSquare(BoardGeometry.AbsoluteIndex(PlayerColor.Ruby, r))) continue;
            var topazTarget = (r + BoardGeometry.TrackLength - BoardGeometry.StartIndex(PlayerColor.Topaz)) % BoardGeometry.TrackLength;
            var from = topazTarget - 3;
            if (from < 1 || from > 47) continue;
            if (BoardGeometry.IsSafeSquare(BoardGeometry.AbsoluteIndex(PlayerColor.Topaz, from))) continue;
            return (r, from);
        }
        throw new InvalidOperationException("No capture square found");
    }

    [Fact]
    public void Boosts_are_off_by_default_and_in_the_standard_mode_rules()
    {
        Assert.Equal(0, RuleSet.ForMode(GameMode.Classic).MaxBoosts);
        Assert.Equal(0, RuleSet.ForMode(GameMode.Quick).MaxBoosts);
        var ranked = GameSetup.NewGame("m", new[] { new SeatSetup("a", "A", PlayerKind.Human), new SeatSetup("b", "B", PlayerKind.Human) }, RuleSet.ForMode(GameMode.Classic));
        Assert.Equal(ErrorCode.BoostNotAllowed, GameEngine.UseShield(ranked, 0).Error);
    }

    [Fact]
    public void Shield_is_used_before_rolling_once_per_match_and_changes_nothing_else()
    {
        var s = Casual();
        var r = GameEngine.UseShield(s, 0);
        Assert.True(r.Ok);
        Assert.True(r.State.Players[0].ShieldActive);
        Assert.Equal(1, r.State.Players[0].BoostsUsed);
        Assert.Equal(GamePhase.AwaitingRoll, r.State.Phase);
        Assert.Equal(0, r.State.CurrentPlayer.Seat);
        Assert.Equal(s.Version + 1, r.State.Version);
        Assert.Contains(r.Events, e => e.Type == GameEventType.ShieldActivated && e.Seat == 0);
        Assert.Equal(ErrorCode.NoBoostsLeft, GameEngine.UseShield(r.State, 0).Error); // limit reached (and still active)
    }

    [Fact]
    public void Shield_is_rejected_out_of_turn_after_rolling_and_after_the_limit()
    {
        var s = Casual();
        Assert.Equal(ErrorCode.NotYourTurn, GameEngine.UseShield(s, 1).Error);
        var rolled = GameEngine.ApplyRoll(s, 0, 6).State;
        Assert.Equal(ErrorCode.BoostNotAllowed, GameEngine.UseShield(rolled, 0).Error);

        var used = s.Clone();
        used.Players[0].BoostsUsed = 1;
        Assert.Equal(ErrorCode.NoBoostsLeft, GameEngine.UseShield(used, 0).Error);

        var over = s.Clone();
        over.Phase = GamePhase.Finished;
        Assert.Equal(ErrorCode.GameOver, GameEngine.UseShield(over, 0).Error);
    }

    [Fact]
    public void Shielded_piece_is_not_captured_and_shares_the_square()
    {
        var (ruby, topazFrom) = CaptureSetup();
        var s = Casual().WithPieces(0, ruby).WithPieces(1, topazFrom);
        s = GameEngine.UseShield(s, 0).State;
        s = GameEngine.ForfeitTurn(s, 0).State; // Ruby's turn ends; the shield stays until Ruby's next turn
        Assert.Equal(1, s.CurrentPlayer.Seat);
        Assert.True(s.Players[0].ShieldActive);

        s = GameEngine.ApplyRoll(s, 1, 3).State;
        var legal = GameEngine.GetLegalMoves(s).Single(m => m.Piece == 0);
        Assert.False(legal.Captures); // bots and the UI see that no capture is possible
        var r = GameEngine.Move(s, 1, 0);
        Assert.True(r.Ok);
        Assert.Equal(ruby, r.State.Players[0].Pieces[0]); // not sent home
        Assert.Equal(topazFrom + 3, r.State.Players[1].Pieces[0]);
        Assert.Equal(BoardGeometry.AbsoluteIndex(PlayerColor.Ruby, ruby), BoardGeometry.AbsoluteIndex(PlayerColor.Topaz, topazFrom + 3)); // same square
        Assert.DoesNotContain(r.Events, e => e.Type == GameEventType.PieceCaptured);
        Assert.Contains(r.Events, e => e.Type == GameEventType.CaptureBlockedByShield && e.Seat == 0 && e.Value == 1);
        Assert.DoesNotContain(r.Events, e => e.Type == GameEventType.ExtraTurn); // no capture bonus
    }

    [Fact]
    public void Unshielded_piece_is_still_captured()
    {
        var (ruby, topazFrom) = CaptureSetup();
        var s = Casual().WithPieces(0, ruby).WithPieces(1, topazFrom).WithTurn(1);
        s = GameEngine.ApplyRoll(s, 1, 3).State;
        var r = GameEngine.Move(s, 1, 0);
        Assert.Equal(BoardGeometry.Yard, r.State.Players[0].Pieces[0]);
        Assert.Contains(r.Events, e => e.Type == GameEventType.PieceCaptured);
    }

    [Fact]
    public void Shield_lasts_through_extra_turns_and_expires_when_the_owner_s_next_turn_starts()
    {
        var s = Casual().WithPieces(0, 5).WithPieces(1, 30);
        s = GameEngine.UseShield(s, 0).State;
        s = GameEngine.ApplyRoll(s, 0, 6).State;      // six: extra turn
        s = GameEngine.Move(s, 0, 0).State;
        Assert.Equal(0, s.CurrentPlayer.Seat);
        Assert.True(s.Players[0].ShieldActive);       // still my turn
        s = GameEngine.ApplyRoll(s, 0, 2).State;
        s = GameEngine.Move(s, 0, 0).State;           // turn passes to seat 1
        Assert.Equal(1, s.CurrentPlayer.Seat);
        Assert.True(s.Players[0].ShieldActive);       // protects during the opponent's turn
        s = GameEngine.ApplyRoll(s, 1, 2).State;
        var r = GameEngine.Move(s, 1, 0);             // back to seat 0: shield ends
        Assert.Equal(0, r.State.CurrentPlayer.Seat);
        Assert.False(r.State.Players[0].ShieldActive);
        Assert.Contains(r.Events, e => e.Type == GameEventType.ShieldExpired && e.Seat == 0);
        Assert.Equal(ErrorCode.NoBoostsLeft, GameEngine.UseShield(r.State, 0).Error); // one per match
    }

    [Fact]
    public void Shield_expires_when_the_turn_passes_by_forfeit_or_three_sixes()
    {
        var s = Casual().WithPieces(0, 5).WithPieces(1, 30);
        s = GameEngine.UseShield(s, 0).State;
        s = GameEngine.ForfeitTurn(s, 0).State; // timeout
        s = GameEngine.ForfeitTurn(s, 1).State;
        Assert.False(s.Players[0].ShieldActive);
    }

    [Fact]
    public void Shield_does_not_change_dice_or_win_conditions_in_quick_mode()
    {
        var s = Casual(GameMode.Quick).WithPieces(0, 54);
        s = GameEngine.UseShield(s, 0).State;
        s = GameEngine.ApplyRoll(s, 0, 2).State;
        Assert.Equal(2, s.PendingRoll);
        var r = GameEngine.Move(s, 0, 0);
        Assert.Equal(GamePhase.Finished, r.State.Phase);
        Assert.Equal(0, r.State.WinnerSeat);
    }

    [Fact]
    public void Shield_state_survives_save_and_load_and_tampering_is_rejected()
    {
        var s = GameEngine.UseShield(Casual(), 0).State;
        var loaded = GameStateSerializer.Deserialize(GameStateSerializer.Serialize(s));
        Assert.True(loaded.Players[0].ShieldActive);
        Assert.Equal(1, loaded.Rules.MaxBoosts);

        var tampered = s.Clone();
        tampered.Players[1].ShieldActive = true; // shield without using a boost
        Assert.Throws<System.Text.Json.JsonException>(() => GameStateSerializer.Validate(tampered));
        var tooMany = s.Clone();
        tooMany.Players[0].BoostsUsed = 2;
        Assert.Throws<System.Text.Json.JsonException>(() => GameStateSerializer.Validate(tooMany));
    }

    [Fact]
    public void Bot_games_with_shields_still_finish()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var s = Casual();
            var dice = new SeededDiceRoller(seed);
            var bot = BotFactory.Create(BotDifficulty.Normal, new Random(seed));
            var steps = 0;
            while (s.Phase != GamePhase.Finished && steps++ < 5000)
            {
                var seat = s.CurrentPlayer.Seat;
                if (s.Phase == GamePhase.AwaitingRoll)
                {
                    if (GameEngine.ValidateShield(s, seat) == ErrorCode.None && seed % 2 == 0) s = GameEngine.UseShield(s, seat).State;
                    s = GameEngine.Roll(s, seat, dice).State;
                }
                else
                {
                    var c = bot.ChooseMove(s);
                    s = (c is int p ? GameEngine.Move(s, seat, p) : GameEngine.ForfeitTurn(s, seat)).State;
                }
            }
            Assert.Equal(GamePhase.Finished, s.Phase);
        }
    }
}
