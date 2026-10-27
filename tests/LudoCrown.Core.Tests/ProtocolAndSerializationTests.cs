using System.Text.Json;
using LudoCrown.Core.Engine;
using LudoCrown.Core.Model;
using LudoCrown.Core.Protocol;
using LudoCrown.Core.Serialization;

namespace LudoCrown.Core.Tests;

public class ProtocolAndSerializationTests
{
    [Fact]
    public void Saved_game_round_trips_exactly()
    {
        var s = TestBoards.FourPlayer().WithPieces(0, 10, 56, -1, 52).WithPieces(2, 3);
        s = GameEngine.ApplyRoll(s, 0, 4).State;
        var json = GameStateSerializer.Serialize(s);
        var back = GameStateSerializer.Deserialize(json);
        Assert.Equal(json, GameStateSerializer.Serialize(back));
        Assert.Equal(GamePhase.AwaitingMove, back.Phase);
        Assert.Equal(4, back.PendingRoll);
    }

    [Fact]
    public void Tampered_saved_games_are_rejected()
    {
        var s = TestBoards.TwoPlayer();
        s.Players[0].Pieces[0] = 99;
        Assert.Throws<JsonException>(() => GameStateSerializer.Deserialize(GameStateSerializer.Serialize(s)));

        var s2 = TestBoards.TwoPlayer();
        s2.Phase = GamePhase.AwaitingMove; // without a pending roll
        Assert.Throws<JsonException>(() => GameStateSerializer.Deserialize(GameStateSerializer.Serialize(s2)));
    }

    [Fact]
    public void Stale_versions_are_rejected()
    {
        var s = TestBoards.TwoPlayer();
        Assert.Equal(ErrorCode.None, CommandValidator.ValidateVersion(s, s.Version));
        Assert.Equal(ErrorCode.StaleVersion, CommandValidator.ValidateVersion(s, s.Version - 1));
        Assert.Equal(ErrorCode.StaleVersion, CommandValidator.ValidateVersion(s, s.Version + 1));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(3, false)]
    [InlineData(0, false)]
    [InlineData(10, false)]
    public void Only_two_or_four_player_online_games(int count, bool ok) =>
        Assert.Equal(ok, CommandValidator.ValidatePlayerCount(count) == ErrorCode.None);

    [Theory]
    [InlineData("abc-123", true)]
    [InlineData("", false)]
    [InlineData("bad id", false)]
    [InlineData("x'; drop table", false)]
    public void Match_ids_are_validated(string id, bool ok) =>
        Assert.Equal(ok, CommandValidator.ValidateMatchId(id) == ErrorCode.None);

    [Fact]
    public void Generated_room_codes_are_valid()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = RoomCodes.Generate();
            Assert.Equal(code, RoomCodes.Normalize(code));
        }
    }

    [Theory]
    [InlineData(" abc-def ", "ABCDEF")]
    [InlineData("k7m 2pq", "K7M2PQ")]
    [InlineData("ABC10O", null)]   // contains ambiguous 1/0/O
    [InlineData("ABCDE", null)]
    [InlineData("ABCDEFG", null)]
    [InlineData(null, null)]
    public void Room_code_input_is_normalised(string? input, string? expected) =>
        Assert.Equal(expected, RoomCodes.Normalize(input));

    [Theory]
    [InlineData("Mustafa", true)]
    [InlineData("Åsa Öberg", true)]
    [InlineData("ab", false)]
    [InlineData("this name is far too long", false)]
    [InlineData("<script>", false)]
    [InlineData("a  b", false)]
    public void Display_names_are_validated(string name, bool ok) =>
        Assert.Equal(ok, DisplayNames.Validate(name) is null);

    [Fact]
    public void Only_preset_reactions_are_allowed()
    {
        Assert.True(Reactions.IsValid("gg"));
        Assert.False(Reactions.IsValid("free text"));
        Assert.False(Reactions.IsValid(null));
    }
}
