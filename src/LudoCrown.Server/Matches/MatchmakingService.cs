using LudoCrown.Core.Engine;
using LudoCrown.Core.Model;
using LudoCrown.Core.Protocol;
using LudoCrown.Server.Data;
using LudoCrown.Server.Options;
using Microsoft.Extensions.Options;

namespace LudoCrown.Server.Matches;

/// <summary>
/// Simple first-come matchmaking with one queue per player count (2 or 4), game mode (Classic, Quick) and queue (Ranked, Casual). When a queue has enough
/// players a match starts. If someone waits longer than MatchmakingBotFillSeconds, the empty seats are
/// filled with bots (shown as bots in the UI) so players are never stuck waiting.
/// Players who have blocked each other are never placed in the same match.
/// Single-instance design: for multiple server instances move queues to Redis (see docs/BACKEND.md).
/// </summary>
public sealed class MatchmakingService(MatchService matches, ISocialStore social, IMatchNotifier notifier, IOptions<MatchOptions> options, TimeProvider time)
{
    private sealed record Ticket(Guid UserId, int PlayerCount, GameMode Mode, MatchQueue Queue, DateTimeOffset EnqueuedAt);

    private readonly object _gate = new();
    private readonly List<Ticket> _tickets = new();

    public int QueuedCount { get { lock (_gate) return _tickets.Count; } }

    public async Task<(MatchmakingStatus? Status, ServerError? Error)> EnqueueAsync(Guid userId, int playerCount, GameMode mode = GameMode.Classic, MatchQueue queue = MatchQueue.Ranked)
    {
        if (!GameModes.IsAvailable(mode))
            return (null, new ServerError(ErrorCode.InvalidMode, "That game mode isn't available."));
        if (CommandValidator.ValidatePlayerCount(playerCount) != ErrorCode.None)
            return (null, new ServerError(ErrorCode.InvalidRequest, "Choose a 2 or 4 player game."));

        if (matches.ActiveMatchFor(userId) is Guid existing)
            return (new MatchmakingStatus(MatchmakingState.Matched, playerCount, playerCount, existing.ToString("N"), "You're already in a match.", mode), null);

        int found;
        lock (_gate)
        {
            _tickets.RemoveAll(t => t.UserId == userId);
            _tickets.Add(new Ticket(userId, playerCount, mode, queue, time.GetUtcNow()));
        }

        await TryFormAsync(playerCount, mode, queue, fillWithBots: false);
        lock (_gate)
        {
            if (!_tickets.Any(t => t.UserId == userId))
            {
                // Grouped. If another caller (or the background tick) is still starting the match, the id is not known yet:
                // report "starting" and let the Matched push that follows carry the id. Never report Matched without one.
                return matches.ActiveMatchFor(userId) is Guid started
                    ? (new MatchmakingStatus(MatchmakingState.Matched, playerCount, playerCount, started.ToString("N"), null, mode, queue), null)
                    : (new MatchmakingStatus(MatchmakingState.Searching, playerCount, playerCount, null, "Starting match…", mode, queue), null);
            }
            found = _tickets.Count(t => t.PlayerCount == playerCount && t.Mode == mode && t.Queue == queue);
        }
        var status = new MatchmakingStatus(MatchmakingState.Searching, playerCount, Math.Min(found, playerCount), null, null, mode, queue);
        await NotifyQueueAsync(playerCount, mode, queue);
        return (status, null);
    }

    public async Task CancelAsync(Guid userId)
    {
        Ticket? t;
        lock (_gate)
        {
            t = _tickets.FirstOrDefault(x => x.UserId == userId);
            if (t is not null) _tickets.Remove(t);
        }
        if (t is not null)
        {
            await notifier.SendMatchmakingStatusAsync(userId, new MatchmakingStatus(MatchmakingState.Cancelled, t.PlayerCount, 0, null, null, t.Mode, t.Queue));
            await NotifyQueueAsync(t.PlayerCount, t.Mode, t.Queue);
        }
    }

    public async Task TickAsync()
    {
        foreach (var count in new[] { 2, 4 })
        foreach (var mode in GameModes.All.Select(m => m.Mode))
        foreach (var queue in new[] { MatchQueue.Ranked, MatchQueue.Casual })
        {
            bool overdue;
            lock (_gate)
            {
                var oldest = _tickets.Where(t => t.PlayerCount == count && t.Mode == mode && t.Queue == queue).OrderBy(t => t.EnqueuedAt).FirstOrDefault();
                overdue = options.Value.MatchmakingBotFillSeconds > 0 && oldest is not null
                          && time.GetUtcNow() - oldest.EnqueuedAt >= TimeSpan.FromSeconds(options.Value.MatchmakingBotFillSeconds);
            }
            await TryFormAsync(count, mode, queue, overdue);
        }
    }

    private async Task TryFormAsync(int playerCount, GameMode mode, MatchQueue queue, bool fillWithBots)
    {
        while (true)
        {
            List<Ticket> candidates;
            lock (_gate) candidates = _tickets.Where(t => t.PlayerCount == playerCount && t.Mode == mode && t.Queue == queue).OrderBy(t => t.EnqueuedAt).ToList();
            if (candidates.Count == 0) return;

            // Greedily build a group of players who have not blocked each other, oldest first.
            var group = new List<Ticket>();
            foreach (var c in candidates)
            {
                if (group.Count == playerCount) break;
                var compatible = true;
                foreach (var g in group)
                    if (await social.IsBlockedEitherWayAsync(g.UserId, c.UserId)) { compatible = false; break; }
                if (compatible) group.Add(c);
            }

            if (group.Count < playerCount && !fillWithBots) return;
            if (group.Count == 0) return;

            lock (_gate)
            {
                // Someone may have cancelled while we checked blocks.
                if (group.Any(g => !_tickets.Contains(g))) continue;
                foreach (var g in group) _tickets.Remove(g);
            }

            var seats = new List<Guid?>(group.Select(g => (Guid?)g.UserId));
            while (seats.Count < playerCount) seats.Add(null);
            Shuffle(seats);
            var matchId = await matches.StartMatchAsync(queue == MatchQueue.Ranked ? MatchMode.Matchmaking : MatchMode.Casual, seats, null, mode);
            foreach (var g in group)
                await notifier.SendMatchmakingStatusAsync(g.UserId, new MatchmakingStatus(MatchmakingState.Matched, playerCount, group.Count, matchId.ToString("N"), null, mode, queue));
            fillWithBots = false; // only one bot-filled match per tick
        }
    }

    private async Task NotifyQueueAsync(int playerCount, GameMode mode, MatchQueue queue)
    {
        List<Guid> waiting;
        lock (_gate) waiting = _tickets.Where(t => t.PlayerCount == playerCount && t.Mode == mode && t.Queue == queue).Select(t => t.UserId).ToList();
        foreach (var u in waiting)
            await notifier.SendMatchmakingStatusAsync(u, new MatchmakingStatus(MatchmakingState.Searching, playerCount, Math.Min(waiting.Count, playerCount), null, null, mode, queue));
    }

    private static void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
