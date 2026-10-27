using LudoCrown.Core.AI;
using LudoCrown.Core.Dice;
using LudoCrown.Core.Engine;
using LudoCrown.Core.Model;

namespace LudoCrown.Core.Tests;

public class BotTests
{
    public static IEnumerable<object[]> Difficulties() => Enum.GetValues<BotDifficulty>().Select(d => new object[] { d });

    [Theory]
    [MemberData(nameof(Difficulties))]
    public void Bots_only_ever_choose_legal_moves(BotDifficulty difficulty)
    {
        for (var seed = 0; seed < 15; seed++)
        {
            var dice = new SeededDiceRoller(1000 + seed);
            var bot = BotFactory.Create(difficulty, new Random(seed));
            var s = TestBoards.FourPlayer();
            var guard = 0;
            while (s.Phase != GamePhase.Finished && guard++ < 20000)
            {
                var seat = s.CurrentPlayer.Seat;
                if (s.Phase == GamePhase.AwaitingRoll)
                {
                    s = GameEngine.Roll(s, seat, dice).State;
                    continue;
                }
                var choice = bot.ChooseMove(s);
                Assert.NotNull(choice);
                Assert.True(GameEngine.IsLegal(s, seat, choice!.Value), $"{difficulty} chose illegal piece {choice}");
                s = GameEngine.Move(s, seat, choice.Value).State;
            }
            Assert.Equal(GamePhase.Finished, s.Phase);
        }
    }

    [Theory]
    [MemberData(nameof(Difficulties))]
    public void Bots_return_null_when_there_is_no_move(BotDifficulty difficulty)
    {
        var s = TestBoards.TwoPlayer(); // awaiting roll: no legal moves
        Assert.Null(BotFactory.Create(difficulty, new Random(1)).ChooseMove(s));
    }

    [Fact]
    public void Normal_bot_prefers_capturing()
    {
        // Piece 0 at 10 can capture Topaz at abs 12 with a 2; piece 1 at 30 would just advance.
        var s = TestBoards.TwoPlayer().WithPieces(0, 10, 30).WithPieces(1, 38);
        s = GameEngine.ApplyRoll(s, 0, 2).State;
        Assert.Equal(0, new HeuristicBot(BotWeights.Normal with { Noise = 0 }, new Random(1)).ChooseMove(s));
    }

    [Fact]
    public void Normal_bot_prefers_finishing()
    {
        var s = TestBoards.TwoPlayer().WithPieces(0, 20, 53);
        s = GameEngine.ApplyRoll(s, 0, 3).State;
        Assert.Equal(1, new HeuristicBot(BotWeights.Normal with { Noise = 0 }, new Random(1)).ChooseMove(s));
    }

    [Fact]
    public void Hard_bot_escapes_a_threatened_piece()
    {
        // Ruby piece 0 at 20 (abs 20) is 2 ahead of a Topaz piece at abs 18 (Topaz progress 44).
        // Ruby piece 1 at 30 is unthreatened. Roll 1: moving piece 0 to abs 21 (a star) escapes.
        var s = TestBoards.TwoPlayer().WithPieces(0, 20, 30).WithPieces(1, 44);
        s = GameEngine.ApplyRoll(s, 0, 1).State;
        var hard = new HeuristicBot(BotWeights.Hard with { Noise = 0 }, new Random(1));
        Assert.Equal(0, hard.ChooseMove(s));
    }

    [Fact]
    public void Hard_bot_avoids_landing_right_in_front_of_an_opponent()
    {
        // Topaz piece at abs 14 (progress 40). Ruby piece 0 at 9 rolling 4 lands on abs 13 (safe start) — fine.
        // Ruby piece 1 at 12 rolling 4 lands on abs 16, two squares ahead of the opponent: dangerous.
        var s = TestBoards.TwoPlayer().WithPieces(0, 9, 12).WithPieces(1, 40);
        s = GameEngine.ApplyRoll(s, 0, 4).State;
        var hard = new HeuristicBot(BotWeights.Hard with { Noise = 0 }, new Random(1));
        Assert.Equal(0, hard.ChooseMove(s));
    }

    [Fact]
    public void Threat_counting_ignores_safe_squares_and_home_column()
    {
        var s = TestBoards.TwoPlayer().WithPieces(1, 44); // Topaz at abs 18
        Assert.Equal(1, HeuristicBot.ThreatsAt(s, PlayerColor.Ruby, 0, 20));
        Assert.Equal(0, HeuristicBot.ThreatsAt(s, PlayerColor.Ruby, 0, 21)); // star
        Assert.Equal(0, HeuristicBot.ThreatsAt(s, PlayerColor.Ruby, 0, 25)); // 7 away
        Assert.Equal(0, HeuristicBot.ThreatsAt(s, PlayerColor.Ruby, 0, 52)); // home column
    }

    [Fact]
    public void Hard_bot_beats_easy_bot_more_often_than_not()
    {
        var hardWins = 0;
        const int games = 60;
        for (var seed = 0; seed < games; seed++)
        {
            var dice = new SeededDiceRoller(seed);
            // Alternate who goes first to remove first-move advantage.
            var hardSeat = seed % 2;
            var bots = new IBotStrategy[2];
            bots[hardSeat] = BotFactory.Create(BotDifficulty.Hard, new Random(seed));
            bots[1 - hardSeat] = BotFactory.Create(BotDifficulty.Easy, new Random(seed + 7));
            var s = TestBoards.TwoPlayer();
            while (s.Phase != GamePhase.Finished)
            {
                var seat = s.CurrentPlayer.Seat;
                s = s.Phase == GamePhase.AwaitingRoll
                    ? GameEngine.Roll(s, seat, dice).State
                    : GameEngine.Move(s, seat, bots[seat].ChooseMove(s)!.Value).State;
            }
            if (s.WinnerSeat == hardSeat) hardWins++;
        }
        Assert.True(hardWins > games / 2, $"Hard won only {hardWins}/{games}");
    }
}
