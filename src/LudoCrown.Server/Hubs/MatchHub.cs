using System.Collections.Concurrent;
using LudoCrown.Core.Engine;
using LudoCrown.Core.Protocol;
using LudoCrown.Server.Auth;
using LudoCrown.Server.Matches;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LudoCrown.Server.Hubs;

/// <summary>
/// Real-time endpoint. Thin by design: authenticates the caller, then delegates to the services.
/// Clients can only express intent; none of these methods accept dice values, positions or results.
/// </summary>
[Authorize]
public sealed class MatchHub(MatchService matches, MatchmakingService matchmaking, PrivateRoomService rooms) : Hub
{
    private Guid UserId => Context.User!.UserId();

    public override async Task OnConnectedAsync()
    {
        await matches.OnConnectedAsync(UserId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await matches.OnDisconnectedAsync(UserId);
        if (!matches.IsConnected(UserId))
        {
            await matchmaking.CancelAsync(UserId);
            await rooms.OnDisconnectedAsync(UserId);
        }
        await base.OnDisconnectedAsync(exception);
    }

    [HubMethodName(HubNames.FindMatch)]
    public async Task<MatchmakingStatus> FindMatch(int playerCount)
    {
        var (status, error) = await matchmaking.EnqueueAsync(UserId, playerCount);
        return status ?? new MatchmakingStatus(MatchmakingState.Failed, playerCount, 0, null, error?.Message);
    }

    [HubMethodName(HubNames.CancelMatchmaking)]
    public Task CancelMatchmaking() => matchmaking.CancelAsync(UserId);

    [HubMethodName(HubNames.CreatePrivateRoom)]
    public async Task<HubResult<RoomInfo>> CreatePrivateRoom(int playerCount)
    {
        var (room, error) = await rooms.CreateAsync(UserId, playerCount);
        return new HubResult<RoomInfo>(room, error);
    }

    [HubMethodName(HubNames.JoinPrivateRoom)]
    public async Task<HubResult<RoomInfo>> JoinPrivateRoom(string code)
    {
        var (room, error) = await rooms.JoinAsync(UserId, code);
        return new HubResult<RoomInfo>(room, error);
    }

    [HubMethodName(HubNames.StartPrivateRoom)]
    public async Task<HubResult<string>> StartPrivateRoom(string roomId)
    {
        var (matchId, error) = await rooms.StartAsync(UserId, roomId);
        return new HubResult<string>(matchId, error);
    }

    [HubMethodName(HubNames.LeaveRoom)]
    public async Task<ServerError?> LeaveRoom(string id)
    {
        await rooms.LeaveAsync(UserId, id);
        return matches.ActiveMatchFor(UserId)?.ToString("N") == id ? await matches.LeaveAsync(UserId, id) : null;
    }

    [HubMethodName(HubNames.Roll)]
    public Task<ServerError?> Roll(string matchId, long expectedVersion) => matches.RollAsync(UserId, matchId, expectedVersion);

    [HubMethodName(HubNames.Move)]
    public Task<ServerError?> Move(string matchId, int pieceIndex, long expectedVersion) => matches.MoveAsync(UserId, matchId, pieceIndex, expectedVersion);

    [HubMethodName(HubNames.Resync)]
    public Task<MatchSnapshot?> Resync(string? matchId) => matches.ResyncAsync(UserId, matchId);

    [HubMethodName(HubNames.SendReaction)]
    public Task<ServerError?> SendReaction(string matchId, string reactionId) => matches.ReactAsync(UserId, matchId, reactionId);

    [HubMethodName(HubNames.RequestRematch)]
    public async Task<HubResult<RoomInfo>> RequestRematch(string matchId)
    {
        var (room, error) = await rooms.RematchAsync(UserId, matchId);
        return new HubResult<RoomInfo>(room, error);
    }
}

public sealed record HubResult<T>(T? Value, ServerError? Error);

/// <summary>Delivers server pushes to every connection of a user (SignalR maps users by the "sub" claim).</summary>
public sealed class HubMatchNotifier(IHubContext<MatchHub> hub) : IMatchNotifier
{
    private IClientProxy U(Guid id) => hub.Clients.User(id.ToString());
    public Task SendSnapshotAsync(Guid userId, MatchSnapshot snapshot) => U(userId).SendAsync(HubNames.OnMatchState, snapshot);
    public Task SendRoomUpdatedAsync(Guid userId, RoomInfo room) => U(userId).SendAsync(HubNames.OnRoomUpdated, room);
    public Task SendMatchmakingStatusAsync(Guid userId, MatchmakingStatus status) => U(userId).SendAsync(HubNames.OnMatchmakingStatus, status);
    public Task SendReactionAsync(Guid userId, ReactionInfo reaction) => U(userId).SendAsync(HubNames.OnReaction, reaction);
    public Task SendMatchEndedAsync(Guid userId, MatchEndedInfo info) => U(userId).SendAsync(HubNames.OnMatchEnded, info);
}

/// <summary>Per-connection token bucket: at most ~10 hub calls per second, bursts of 20.</summary>
public sealed class HubRateLimitFilter : IHubFilter
{
    private sealed class Bucket { public double Tokens = 20; public DateTimeOffset Last = DateTimeOffset.UtcNow; }
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var bucket = _buckets.GetOrAdd(context.Context.ConnectionId, _ => new Bucket());
        lock (bucket)
        {
            var now = DateTimeOffset.UtcNow;
            bucket.Tokens = Math.Min(20, bucket.Tokens + (now - bucket.Last).TotalSeconds * 10);
            bucket.Last = now;
            if (bucket.Tokens < 1) throw new HubException(ErrorCode.RateLimited.ToString());
            bucket.Tokens -= 1;
        }
        return await next(context);
    }

    public Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        _buckets.TryRemove(context.Context.ConnectionId, out _);
        return next(context, exception);
    }
}

/// <summary>Drives turn timers, bot moves, matchmaking bot-fill and room expiry.</summary>
public sealed class GameLoopService(MatchService matches, MatchmakingService matchmaking, PrivateRoomService rooms, ILogger<GameLoopService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await matches.RecoverAsync(stoppingToken); }
        catch (Exception ex) { logger.LogError(ex, "Match recovery failed"); }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await matches.TickAsync();
                await matchmaking.TickAsync();
                rooms.Tick();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Game loop tick failed");
            }
        }
    }
}
