using LudoCrown.Core.Dice;
using LudoCrown.Core.Protocol;
using LudoCrown.Server.Auth;
using LudoCrown.Server.Data;
using LudoCrown.Server.Matches;
using LudoCrown.Server.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace LudoCrown.Server.Tests;

public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public ManualTimeProvider() : this(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)) { }
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Records everything the server pushes, per user.</summary>
public sealed class RecordingNotifier : IMatchNotifier
{
    private readonly object _gate = new();
    public List<(Guid User, MatchSnapshot Snapshot)> Snapshots { get; } = new();
    public List<(Guid User, RoomInfo Room)> Rooms { get; } = new();
    public List<(Guid User, MatchmakingStatus Status)> Matchmaking { get; } = new();
    public List<(Guid User, ReactionInfo Reaction)> Reactions { get; } = new();
    public List<(Guid User, MatchEndedInfo Info)> Ended { get; } = new();

    public MatchSnapshot? LastSnapshot(Guid user) { lock (_gate) return Snapshots.LastOrDefault(s => s.User == user).Snapshot; }

    public Task SendSnapshotAsync(Guid userId, MatchSnapshot snapshot) { lock (_gate) Snapshots.Add((userId, snapshot)); return Task.CompletedTask; }
    public Task SendRoomUpdatedAsync(Guid userId, RoomInfo room) { lock (_gate) Rooms.Add((userId, room)); return Task.CompletedTask; }
    public Task SendMatchmakingStatusAsync(Guid userId, MatchmakingStatus status) { lock (_gate) Matchmaking.Add((userId, status)); return Task.CompletedTask; }
    public Task SendReactionAsync(Guid userId, ReactionInfo reaction) { lock (_gate) Reactions.Add((userId, reaction)); return Task.CompletedTask; }
    public Task SendMatchEndedAsync(Guid userId, MatchEndedInfo info) { lock (_gate) Ended.Add((userId, info)); return Task.CompletedTask; }
}

/// <summary>Dice that return queued values first, then fall back to a seeded sequence.</summary>
public sealed class QueueDice : IDiceRoller
{
    private readonly Queue<int> _queue = new();
    private readonly Random _fallback = new(42);
    public void Enqueue(params int[] values) { foreach (var v in values) _queue.Enqueue(v); }
    public int Roll() => _queue.Count > 0 ? _queue.Dequeue() : _fallback.Next(1, 7);
}

/// <summary>Wires up the server's game services against the in-memory store with controllable time and dice.</summary>
public sealed class ServerHarness
{
    public ManualTimeProvider Time { get; } = new();
    public InMemoryStore Store { get; }
    public RecordingNotifier Notifier { get; } = new();
    public QueueDice Dice { get; } = new();
    public MatchOptions MatchOptions { get; } = new() { TurnSeconds = 20, DisconnectedTurnSeconds = 6, DisconnectGraceSeconds = 90, MaxMissedTurns = 3, BotDelaySeconds = 1, MatchmakingBotFillSeconds = 30 };
    public JwtOptions Jwt { get; } = new() { SigningKey = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray()) };
    public AuthOptions Auth { get; } = new() { Email = new EmailAuthOptions { Enabled = true, MaxAttempts = 3 } };

    public MatchService Matches { get; }
    public MatchmakingService Matchmaking { get; }
    public PrivateRoomService Rooms { get; }
    public TokenService Tokens { get; }
    public AccountService Accounts { get; }

    public ServerHarness()
    {
        Store = new InMemoryStore(Time);
        var mo = Microsoft.Extensions.Options.Options.Create(MatchOptions);
        Matches = NewMatchService();
        Matchmaking = new MatchmakingService(Matches, Store, Notifier, mo, Time);
        Rooms = new PrivateRoomService(Matches, Store, Store, Notifier, mo, Time);
        Tokens = new TokenService(Microsoft.Extensions.Options.Options.Create(Jwt), Store, Time);
        Accounts = new AccountService(Store, Store, Store, Store, Tokens);
    }

    public MatchService NewMatchService() =>
        new(Store, Store, Store, Notifier, Dice, Microsoft.Extensions.Options.Options.Create(MatchOptions), Time, NullLogger<MatchService>.Instance);

    public async Task<Guid> NewUserAsync(string name = "Player")
    {
        var user = await Store.CreateUserAsync(false, name, "crown");
        return user.Id;
    }

    public async Task<(Guid A, Guid B, Guid MatchId)> StartTwoPlayerAsync(bool connected = true)
    {
        var a = await NewUserAsync("Alice");
        var b = await NewUserAsync("Bob");
        if (connected) { await Matches.OnConnectedAsync(a); await Matches.OnConnectedAsync(b); }
        var id = await Matches.StartMatchAsync(MatchMode.Matchmaking, new Guid?[] { a, b }, null);
        return (a, b, id);
    }

    public async Task TickAsync(TimeSpan advance)
    {
        Time.Advance(advance);
        await Matches.TickAsync();
    }
}
