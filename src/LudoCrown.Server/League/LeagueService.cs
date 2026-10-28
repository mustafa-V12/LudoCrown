using LudoCrown.Server.Data;
using Microsoft.Extensions.Options;

namespace LudoCrown.Server.League;

public sealed class LeagueOptions
{
    public bool Enabled { get; set; } = true;
    public int SeasonDays { get; set; } = LeagueRules.DefaultSeasonDays;
}

/// <summary>
/// Runs league seasons on the server. Season boundaries, points, promotion/demotion and rewards are computed here from
/// server-recorded results only; clients can read standings but never write them.
/// Finalisation is restartable: every step is idempotent (results are saved once, each reward is an economy operation with
/// a fixed idempotency key, and the season is marked completed last), so a crash halfway simply resumes on the next tick.
/// </summary>
public sealed class LeagueService(ILeagueStore store, IEconomyStore economy, IOptions<LeagueOptions> options, TimeProvider time, ILogger<LeagueService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LeagueOptions O => options.Value;

    public bool Enabled => O.Enabled;

    /// <summary>The season ranked matches currently count toward (creating the first one if needed), or null when disabled or between seasons.</summary>
    public async Task<SeasonRecord?> GetActiveSeasonAsync(CancellationToken ct = default)
    {
        if (!O.Enabled) return null;
        var season = await store.GetCurrentSeasonAsync(ct) ?? await EnsureSeasonAsync(ct);
        return season is { Status: SeasonStatus.Active } && time.GetUtcNow() < season.EndsAt ? season : null;
    }

    /// <summary>Places the player in a group of their division for the season (idempotent).</summary>
    public Task<StandingRecord> EnsureStandingAsync(int seasonId, Guid userId, CancellationToken ct = default) =>
        store.EnsureStandingAsync(seasonId, userId, LeagueRules.GroupSize, ct);

    /// <summary>Makes sure a season is running: finalises an ended season and opens the next one. Called every minute.</summary>
    public async Task<SeasonRecord?> EnsureSeasonAsync(CancellationToken ct = default)
    {
        if (!O.Enabled) return null;
        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            while (await store.GetCurrentSeasonAsync(ct) is SeasonRecord current)
            {
                if (current.Status == SeasonStatus.Active && now < current.EndsAt) return current;
                await FinalizeLockedAsync(current, ct); // ended, or a finalisation interrupted earlier: finish it
            }

            var latest = await store.GetLatestSeasonAsync(ct);
            var start = latest is null || latest.EndsAt < now.AddDays(-1) ? StartOfDay(now) : latest.EndsAt;
            var created = await store.CreateSeasonAsync((latest?.Number ?? 0) + 1, start, start.AddDays(Math.Max(1, O.SeasonDays)), ct);
            logger.LogInformation("League season {Number} runs {Start:u} – {End:u}", created.Number, created.StartsAt, created.EndsAt);
            return created;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Ends the current season now (development tool and tests) and opens the next one.</summary>
    public async Task<SeasonRecord?> EndCurrentSeasonNowAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = await store.GetCurrentSeasonAsync(ct);
            if (current is not null) await FinalizeLockedAsync(current, ct);
        }
        finally { _gate.Release(); }
        return await EnsureSeasonAsync(ct);
    }

    private async Task FinalizeLockedAsync(SeasonRecord season, CancellationToken ct)
    {
        // 1. Freeze: no more points once the season is finalizing.
        if (season.Status == SeasonStatus.Active) await store.SetSeasonStatusAsync(season.Id, SeasonStatus.Finalizing, null, ct);

        // 2. Deterministic results per group (same input → same output, so re-running is safe).
        var standings = await store.GetSeasonStandingsAsync(season.Id, ct);
        var results = standings.GroupBy(s => s.GroupId).SelectMany(g => LeagueRules.ResultsForGroup(g)).ToList();
        await store.SaveSeasonResultsAsync(season.Id, results, ct);

        // 3. Grant rewards once each.
        foreach (var reward in await store.GetUngrantedRewardsAsync(season.Id, ct))
        {
            var op = new EconomyOperation(reward.UserId, $"league:{season.Id}:{reward.UserId:N}", Economy.Reasons.LeagueReward, reward.Reward.Changes(), time.GetUtcNow());
            var result = await economy.ApplyAsync(op, ct);
            if (result.Ok) await store.MarkRewardGrantedAsync(season.Id, reward.UserId, time.GetUtcNow(), ct);
            else logger.LogWarning("League reward for season {Season} not granted to {User}: {Outcome}", season.Number, reward.UserId, result.Outcome);
        }

        // 4. Done.
        await store.SetSeasonStatusAsync(season.Id, SeasonStatus.Completed, time.GetUtcNow(), ct);
        logger.LogInformation("League season {Number} finalised: {Players} players", season.Number, results.Count);
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset t) => new(t.UtcDateTime.Date, TimeSpan.Zero);
}

/// <summary>Ticks the league once a minute (season rollover and reward grants).</summary>
public sealed class LeagueHostedService(LeagueService league, ILogger<LeagueHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { await league.EnsureSeasonAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "League tick failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
