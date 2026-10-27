using LudoCrown.Core.Engine;
using LudoCrown.Core.Protocol;
using LudoCrown.Server.Data;
using LudoCrown.Server.Options;
using Microsoft.Extensions.Options;

namespace LudoCrown.Server.Matches;

/// <summary>
/// Simple first-come matchmaking with one queue per player count (2 or 4). When a queue has enough
/// players a match starts. If someone waits longer than MatchmakingBotFillSeconds, the empty seats are
/// filled with bots (shown as bots in the UI) so players are never stuck waiting.
/// Players who have blocked each other are never placed in the same match.
/// Single-instance design: for multiple server instances move queues to Redis (see docs/BACKEND.md).
/// </summary>
public sealed class MatchmakingService(MatchService matches, ISocialStore social, IMatchNotifier notifier, IOptions<MatchOptions> options, TimeProvider time)
{
    private sealed record Ticket(Guid UserId, int PlayerCount, DateTimeOffset EnqueuedAt);

    private readonly object _gate = new();
    private readonly List<Ticket> _tickets = new();

    public int QueuedCount { get { lock (_gate) return _tickets.Count; } }

    public async Task<(MatchmakingStatus? Status, ServerError? Error)> EnqueueAsync(Guid userId, int playerCount)
    {
        if (CommandValidator.ValidatePlayerCount(playerCount) != ErrorCode.None)
            return (null, new ServerError(ErrorCode.InvalidRequest, "Choose a 2 or 4 player game."));

        if (matches.ActiveMatchFor(userId) is Guid existing)
            return (new MatchmakingStatus(MatchmakingState.Matched, playerCount, playerCount, existing.ToString("N"), "You're already in a match."), null);

        int found;
        lock (_gate)
        {
            _tickets.RemoveAll(t => t.UserId == userId);
            _tickets.Add(new Ticket(userId, playerCount, time.GetUtcNow()));
            found = _tickets.Count(t => t.PlayerCount == playerCount);
        }

        await TryFormAsync(playerCount, fillWithBots: false);
        lock (_gate)
        {
            if (!_tickets.Any(t => t.UserId == userId))
                return (new MatchmakingStatus(MatchmakingState.Matched, playerCount, playerCount, matches.ActiveMatchFor(userId)?.ToString("N"), null), null);
            found = _tickets.Count(t => t.PlayerCount == playerCount);
        }
        var status = new MatchmakingStatus(MatchmakingState.Searching, playerCount, Math.Min(found, playerCount), null, null);
        await NotifyQueueAsync(playerCount);
        return (status, null);
    }

    public async Task CancelAsync(Guid userId)
    {
        int? count = null;
        lock (_gate)
        {
            var t = _tickets.FirstOrDefault(x => x.UserId == userId);
            if (t is not null) { _tickets.Remove(t); count = t.PlayerCount; }
        }
        if (count is int c)
        {
            await notifier.SendMatchmakingStatusAsync(userId, new MatchmakingStatus(MatchmakingState.Cancelled, c, 0, null, null));
            await NotifyQueueAsync(c);
        }
    }

    public async Task TickAsync()
    {
        if (options.Value.MatchmakingBotFillSeconds <= 0) { await TryFormAsync(2, false); await TryFormAsync(4, false); return; }
        foreach (var count in new[] { 2, 4 })
        {
            bool overdue;
            lock (_gate)
            {
                var oldest = _tickets.Where(t => t.PlayerCount == count).OrderBy(t => t.EnqueuedAt).FirstOrDefault();
                overdue = oldest is not null && time.GetUtcNow() - oldest.EnqueuedAt >= TimeSpan.FromSeconds(options.Value.MatchmakingBotFillSeconds);
            }
            await TryFormAsync(count, overdue);
        }
    }

    private async Task TryFormAsync(int playerCount, bool fillWithBots)
    {
        while (true)
        {
            List<Ticket> candidates;
            lock (_gate) candidates = _tickets.Where(t => t.PlayerCount == playerCount).OrderBy(t => t.EnqueuedAt).ToList();
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
            var matchId = await matches.StartMatchAsync(MatchMode.Matchmaking, seats, null);
            foreach (var g in group)
                await notifier.SendMatchmakingStatusAsync(g.UserId, new MatchmakingStatus(MatchmakingState.Matched, playerCount, group.Count, matchId.ToString("N"), null));
            fillWithBots = false; // only one bot-filled match per tick
        }
    }

    private async Task NotifyQueueAsync(int playerCount)
    {
        List<Guid> waiting;
        lock (_gate) waiting = _tickets.Where(t => t.PlayerCount == playerCount).Select(t => t.UserId).ToList();
        foreach (var u in waiting)
            await notifier.SendMatchmakingStatusAsync(u, new MatchmakingStatus(MatchmakingState.Searching, playerCount, Math.Min(waiting.Count, playerCount), null, null));
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
