using LudoCrown.App.Game;
using LudoCrown.Core.Board;
using LudoCrown.Core.Dice;
using LudoCrown.Core.Engine;
using LudoCrown.Core.Model;
using LudoCrown.Mobile.Controls;

// Play a few seeded turns so pieces are spread across the board, then render one frame.
var state = GameSetup.NewGame("preview", new[]
{
    new SeatSetup("a", "Ruby", PlayerKind.Human), new SeatSetup("b", "Emerald", PlayerKind.Bot),
    new SeatSetup("c", "Topaz", PlayerKind.Bot), new SeatSetup("d", "Sapphire", PlayerKind.Bot),
});
var dice = new SeededDiceRoller(11);
var bot = LudoCrown.Core.AI.BotFactory.Create(LudoCrown.Core.AI.BotDifficulty.Normal, new Random(11));
for (var i = 0; i < 260 && state.Phase != GamePhase.Finished; i++)
{
    var seat = state.CurrentPlayer.Seat;
    state = state.Phase == GamePhase.AwaitingRoll ? GameEngine.Roll(state, seat, dice).State : GameEngine.Move(state, seat, bot.ChooseMove(state)!.Value).State;
}
// Stop on a Ruby "choose a piece" moment so highlights show.
while (!(state.CurrentPlayer.Seat == 0 && state.Phase == GamePhase.AwaitingMove) && state.Phase != GamePhase.Finished)
{
    var seat = state.CurrentPlayer.Seat;
    state = state.Phase == GamePhase.AwaitingRoll ? GameEngine.Roll(state, seat, dice).State : GameEngine.Move(state, seat, bot.ChooseMove(state)!.Value).State;
}

var seats = state.Players.Select(p => new SeatView(p.Seat, p.Color, p.DisplayName, "crown", p.Seat != 0, true, p.Seat == 0, p.FinishedCount)).ToList();
var board = new BoardState(state.MatchId, state.Version, state.Phase, state.CurrentPlayer.Seat, state.PendingRoll, state.LastRoll,
    state.Players.Select(p => p.Pieces).ToArray(), seats, Array.Empty<GameEvent>(), null,
    GameEngine.GetLegalMoves(state).Select(m => m.Piece).ToList(), null, false);

var drawable = new BoardDrawable { State = board, Pulse = 0.8f };
foreach (var p in state.Players)
    for (var i = 0; i < 4; i++)
        drawable.Sprites.Add(new PieceSprite { Seat = p.Seat, Index = i, Color = p.Color, Progress = p.Pieces[i], Position = BoardGeometry.PiecePosition(p.Color, i, p.Pieces[i]) });

var canvas = new Microsoft.Maui.Graphics.SvgCanvas();
drawable.Draw(canvas, new Microsoft.Maui.Graphics.RectF(0, 0, 720, 720));
File.WriteAllText(args.Length > 0 ? args[0] : "board.svg", canvas.ToSvg(720, 720));
Console.WriteLine($"Rendered. Ruby rolled {state.PendingRoll}; movable: {string.Join(",", board.LegalPieces)}; pieces: {string.Join(" | ", state.Players.Select(p => $"{p.Color}:{string.Join(",", p.Pieces)}"))}");
