using LudoCrown.Core.Engine;
using LudoCrown.Core.Protocol;

namespace LudoCrown.Server.Tests;

public class MatchmakingTests
{
    [Fact]
    public async Task Two_players_queuing_for_two_player_games_are_matched()
    {
        var h = new ServerHarness();
        var a = await h.NewUserAsync();
        var b = await h.NewUserAsync();
        var (s1, _) = await h.Matchmaking.EnqueueAsync(a, 2);
        Assert.Equal(MatchmakingState.Searching, s1!.State);
        var (s2, _) = await h.Matchmaking.EnqueueAsync(b, 2);
        Assert.Equal(MatchmakingState.Matched, s2!.State);
        Assert.NotNull(h.Matches.ActiveMatchFor(a));
        Assert.Equal(h.Matches.ActiveMatchFor(a), h.Matches.ActiveMatchFor(b));
        Assert.Contains(h.Notifier.Matchmaking, m => m.User == a && m.Status.State == MatchmakingState.Matched);
    }

    [Fact]
    public async Task Queues_are_separate_per_player_count()
    {
        var h = new ServerHarness();
        await h.Matchmaking.EnqueueAsync(await h.NewUserAsync(), 2);
        var (s, _) = await h.Matchmaking.EnqueueAsync(await h.NewUserAsync(), 4);
        Assert.Equal(MatchmakingState.Searching, s!.State);
        Assert.Equal(2, h.Matchmaking.QueuedCount);
    }

    [Fact]
    public async Task Invalid_player_counts_are_rejected()
    {
        var h = new ServerHarness();
        var (s, e) = await h.Matchmaking.EnqueueAsync(await h.NewUserAsync(), 3);
        Assert.Null(s);
        Assert.Equal(ErrorCode.InvalidRequest, e!.Code);
    }

    [Fact]
    public async Task Players_who_blocked_each_other_are_not_matched()
    {
        var h = new ServerHarness();
        var a = await h.NewUserAsync();
        var b = await h.NewUserAsync();
        await h.Store.BlockAsync(a, b, h.Time.GetUtcNow());
        await h.Matchmaking.EnqueueAsync(a, 2);
        var (s, _) = await h.Matchmaking.EnqueueAsync(b, 2);
        Assert.Equal(MatchmakingState.Searching, s!.State);
        Assert.Null(h.Matches.ActiveMatchFor(a));
    }

    [Fact]
    public async Task Empty_seats_are_filled_with_bots_after_the_wait_limit()
    {
        var h = new ServerHarness();
        var a = await h.NewUserAsync();
        await h.Matchmaking.EnqueueAsync(a, 4);
        h.Time.Advance(TimeSpan.FromSeconds(10));
        await h.Matchmaking.TickAsync();
        Assert.Null(h.Matches.ActiveMatchFor(a));
        h.Time.Advance(TimeSpan.FromSeconds(21));
        await h.Matchmaking.TickAsync();
        var id = h.Matches.ActiveMatchFor(a);
        Assert.NotNull(id);
        var room = h.Matches.GetRoom(id!.Value)!;
        Assert.Equal(3, room.Seats.Count(s => s.IsBot));
    }

    [Fact]
    public async Task Cancel_removes_the_player_from_the_queue()
    {
        var h = new ServerHarness();
        var a = await h.NewUserAsync();
        await h.Matchmaking.EnqueueAsync(a, 2);
        await h.Matchmaking.CancelAsync(a);
        Assert.Equal(0, h.Matchmaking.QueuedCount);
        Assert.Contains(h.Notifier.Matchmaking, m => m.User == a && m.Status.State == MatchmakingState.Cancelled);
    }

    [Fact]
    public async Task A_player_already_in_a_match_is_sent_back_to_it()
    {
        var h = new ServerHarness();
        var (a, _, id) = await h.StartTwoPlayerAsync();
        var (s, _) = await h.Matchmaking.EnqueueAsync(a, 2);
        Assert.Equal(MatchmakingState.Matched, s!.State);
        Assert.Equal(id.ToString("N"), s.MatchId);
        Assert.Equal(0, h.Matchmaking.QueuedCount);
    }
}

public class PrivateRoomTests
{
    [Fact]
    public async Task Host_creates_friend_joins_with_code_and_host_starts()
    {
        var h = new ServerHarness();
        var host = await h.NewUserAsync("Host");
        var friend = await h.NewUserAsync("Friend");
        var (room, _) = await h.Rooms.CreateAsync(host, 2);
        Assert.True(room!.IsHost);
        Assert.Equal(6, room.Code!.Length);

        var (joined, err) = await h.Rooms.JoinAsync(friend, room.Code.ToLowerInvariant());
        Assert.Null(err);
        Assert.Equal(2, joined!.Seats.Count);
        Assert.False(joined.IsHost);
        Assert.Contains(h.Notifier.Rooms, r => r.User == host && r.Room.Seats.Count == 2);

        var (_, notHost) = await h.Rooms.StartAsync(friend, room.RoomId);
        Assert.Equal(ErrorCode.InvalidRequest, notHost!.Code);

        var (matchId, startErr) = await h.Rooms.StartAsync(host, room.RoomId);
        Assert.Null(startErr);
        Assert.Equal(matchId, h.Matches.ActiveMatchFor(friend)!.Value.ToString("N"));
        Assert.Contains(h.Notifier.Rooms, r => r.User == friend && r.Room.Started && r.Room.MatchId == matchId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12")]
    [InlineData("OOOOOO")]
    public async Task Malformed_codes_are_rejected(string code)
    {
        var h = new ServerHarness();
        var (_, err) = await h.Rooms.JoinAsync(await h.NewUserAsync(), code);
        Assert.Equal(ErrorCode.InvalidRoomCode, err!.Code);
    }

    [Fact]
    public async Task Unknown_codes_and_full_rooms_are_rejected()
    {
        var h = new ServerHarness();
        var (_, notFound) = await h.Rooms.JoinAsync(await h.NewUserAsync(), "ABCDEF");
        Assert.Equal(ErrorCode.RoomNotFound, notFound!.Code);

        var (room, _) = await h.Rooms.CreateAsync(await h.NewUserAsync(), 2);
        await h.Rooms.JoinAsync(await h.NewUserAsync(), room!.Code);
        var (_, full) = await h.Rooms.JoinAsync(await h.NewUserAsync(), room.Code);
        Assert.Equal(ErrorCode.RoomFull, full!.Code);
    }

    [Fact]
    public async Task Host_cannot_start_alone_and_four_player_rooms_fill_with_bots()
    {
        var h = new ServerHarness();
        var host = await h.NewUserAsync();
        var (room, _) = await h.Rooms.CreateAsync(host, 4);
        var (_, alone) = await h.Rooms.StartAsync(host, room!.RoomId);
        Assert.NotNull(alone);

        await h.Rooms.JoinAsync(await h.NewUserAsync(), room.Code);
        var (matchId, _) = await h.Rooms.StartAsync(host, room.RoomId);
        var live = h.Matches.GetRoom(Guid.Parse(matchId!))!;
        Assert.Equal(2, live.Seats.Count(s => s.IsBot));
    }

    [Fact]
    public async Task Blocked_players_cannot_join_and_see_a_generic_error()
    {
        var h = new ServerHarness();
        var host = await h.NewUserAsync();
        var blocked = await h.NewUserAsync();
        await h.Store.BlockAsync(host, blocked, h.Time.GetUtcNow());
        var (room, _) = await h.Rooms.CreateAsync(host, 2);
        var (_, err) = await h.Rooms.JoinAsync(blocked, room!.Code);
        Assert.Equal(ErrorCode.RoomNotFound, err!.Code);
    }

    [Fact]
    public async Task Host_leaving_passes_host_to_the_next_player()
    {
        var h = new ServerHarness();
        var host = await h.NewUserAsync();
        var friend = await h.NewUserAsync();
        var (room, _) = await h.Rooms.CreateAsync(host, 4);
        await h.Rooms.JoinAsync(friend, room!.Code);
        await h.Rooms.LeaveAsync(host, room.RoomId);
        Assert.True(h.Notifier.Rooms.Last(r => r.User == friend).Room.IsHost);
    }

    [Fact]
    public async Task Rematch_reopens_a_table_with_the_same_players()
    {
        var h = new ServerHarness();
        var host = await h.NewUserAsync();
        var friend = await h.NewUserAsync();
        var (room, _) = await h.Rooms.CreateAsync(host, 2);
        await h.Rooms.JoinAsync(friend, room!.Code);
        var (matchId, _) = await h.Rooms.StartAsync(host, room.RoomId);
        await h.Matches.LeaveAsync(host, matchId!);
        await h.Matches.LeaveAsync(friend, matchId!);

        var (rematch, err) = await h.Rooms.RematchAsync(friend, matchId);
        Assert.Null(err);
        Assert.True(rematch!.IsHost);
        Assert.Equal(2, rematch.Seats.Count);
        Assert.NotEqual(room.Code, rematch.Code);

        var (again, err2) = await h.Rooms.RematchAsync(host, matchId);
        Assert.Null(err2);
        Assert.Equal(rematch.RoomId, again!.RoomId);
    }
}
