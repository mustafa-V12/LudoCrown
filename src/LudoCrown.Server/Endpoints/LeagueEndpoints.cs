using LudoCrown.Core.Model;
using LudoCrown.Server.Auth;
using LudoCrown.Server.Data;
using LudoCrown.Server.League;

namespace LudoCrown.Server.Endpoints;

public sealed record DivisionDto(int Index, string Name, string Crest);
public sealed record SeasonDto(int Number, DateTimeOffset StartsAt, DateTimeOffset EndsAt, long SecondsRemaining, string Status);
public sealed record MyStandingDto(bool Joined, int Division, string DivisionName, int Points, int? GroupRank, int GroupSize, int MatchesPlayed, int Wins,
    bool InPromotionZone, bool InDemotionZone, int MatchesNeededForPromotion);
public sealed record GroupRowDto(int Rank, string PublicId, string DisplayName, string AvatarId, string? FrameId, int Points, int MatchesPlayed, int Wins, bool IsYou);
public sealed record RewardCellDto(int Coins, int Gems, int Shields);
public sealed record RewardRowDto(int Division, string DivisionName, IReadOnlyList<RewardCellDto> Bands);
public sealed record RewardTableDto(IReadOnlyList<string> Bands, IReadOnlyList<RewardRowDto> Rows, IReadOnlyList<string> Rules);
public sealed record LastSeasonDto(int Number, int? FinalRank, string? Outcome, RewardCellDto? Reward, bool RewardGranted);
public sealed record LeagueResponse(bool Enabled, string Environment, SeasonDto? Season, IReadOnlyList<DivisionDto> Divisions, MyStandingDto? Me,
    IReadOnlyList<GroupRowDto> Group, RewardTableDto Rewards, LastSeasonDto? LastSeason);

public sealed record ModeStatsDto(string Mode, string ModeName, int GamesPlayed, int GamesWon, int GamesLost, int Abandons, int WinRatePercent, int CurrentWinStreak, int BestWinStreak);
public sealed record PublicLeagueDto(int Season, int Division, string DivisionName, int Points, int? GroupRank);
public sealed record PublicProfileDto(string PublicId, string DisplayName, string AvatarId, string? FrameId, int Level, bool IsYou, bool IsFriend,
    bool StatsVisible, ModeStatsDto? Overall, IReadOnlyList<ModeStatsDto> Modes, bool LeagueVisible, PublicLeagueDto? League);
public sealed record PrivacyRequest(bool? ShowStats, bool? ShowLeague, bool? ChatEnabled);

/// <summary>League tables, public profiles and privacy settings. Everything shown comes from server-recorded results.</summary>
public static class LeagueEndpoints
{
    public static RewardTableDto RewardTable() => new(
        LeagueRules.Bands.Select(b => b.Label).ToList(),
        LeagueRules.Divisions.Select(d => new RewardRowDto(d.Index, d.Name,
            Enumerable.Range(0, LeagueRules.Bands.Count).Select(i => LeagueRules.RewardCell(d.Index, i)).Select(r => new RewardCellDto(r.Coins, r.Gems, r.Shields)).ToList())).ToList(),
        new[]
        {
            "Only ranked matchmaking matches with at least 2 human players count. Casual matches, private tables and offline games never do.",
            "Points per ranked match: 2 players — win +20, loss −10. 4 players — 1st +30, 2nd +10, 3rd −5, 4th −15. Leaving a match −25. Points never go below 0.",
            $"You are placed in a group of up to {LeagueRules.GroupSize} players of your division when you finish your first ranked match of the season.",
            $"At season end the top {LeagueRules.PromoteTop} of each group who played at least {LeagueRules.MinMatchesForPromotion} ranked matches move up a division.",
            $"In groups of {LeagueRules.MinGroupSizeForDemotion} or more, the bottom {LeagueRules.DemoteBottom} move down a division. Squire is the lowest, Monarch the highest.",
            "Ties are broken by wins, then by who reached their points first.",
            "The top 10 of every group who played at least once receive the rewards in the table — fixed amounts of gold, gems and Shield bottles. No real-world prizes.",
            "Gem rerolls are allowed in ranked matches (Classic 7, Quick 5). Boost bottles are not.",
        });

    private static ModeStatsDto ToDto(ModeStatsRecord m) => new(m.Mode.ToString().ToLowerInvariant(), GameModes.Get(m.Mode).Name, m.GamesPlayed, m.GamesWon, m.GamesLost, m.Abandons,
        m.WinRatePercent, m.CurrentWinStreak, m.BestWinStreak);

    public static void MapLeagueEndpoints(this WebApplication app)
    {
        app.MapGet("/league", async (HttpContext http, LeagueService league, ILeagueStore store, IWebHostEnvironment env, TimeProvider time, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var divisions = LeagueRules.Divisions.Select(d => new DivisionDto(d.Index, d.Name, d.Crest)).ToList();
            if (!league.Enabled) return Results.Ok(new LeagueResponse(false, env.EnvironmentName, null, divisions, null, Array.Empty<GroupRowDto>(), RewardTable(), null));

            var season = await store.GetCurrentSeasonAsync(ct) ?? await league.EnsureSeasonAsync(ct);
            var now = time.GetUtcNow();
            SeasonDto? seasonDto = season is null ? null
                : new SeasonDto(season.Number, season.StartsAt, season.EndsAt, Math.Max(0, (long)(season.EndsAt - now).TotalSeconds), season.Status.ToString().ToLowerInvariant());

            MyStandingDto? me = null;
            var group = new List<GroupRowDto>();
            if (season is not null)
            {
                var standing = await store.GetStandingAsync(season.Id, userId, ct);
                if (standing is null)
                {
                    var division = await store.GetDivisionAsync(userId, ct) ?? 0;
                    me = new MyStandingDto(false, division, LeagueRules.DivisionAt(division).Name, 0, null, 0, 0, 0, false, false, LeagueRules.MinMatchesForPromotion);
                }
                else
                {
                    var rows = await store.GetGroupAsync(standing.GroupId, LeagueRules.GroupSize, ct);
                    group = rows.Select((r, i) => new GroupRowDto(i + 1, r.PublicId, r.DisplayName, r.AvatarId, r.FrameId, r.Points, r.MatchesPlayed, r.Wins, r.UserId == userId)).ToList();
                    var rank = group.FindIndex(g => g.IsYou) + 1;
                    var n = group.Count;
                    me = new MyStandingDto(true, standing.Division, LeagueRules.DivisionAt(standing.Division).Name, standing.Points, rank == 0 ? null : rank, n,
                        standing.MatchesPlayed, standing.Wins,
                        standing.Division < LeagueRules.TopDivision && rank is > 0 and <= LeagueRules.PromoteTop,
                        standing.Division > 0 && n >= LeagueRules.MinGroupSizeForDemotion && rank > n - LeagueRules.DemoteBottom,
                        Math.Max(0, LeagueRules.MinMatchesForPromotion - standing.MatchesPlayed));
                }
            }

            LastSeasonDto? last = null;
            var latest = await store.GetLatestSeasonAsync(ct);
            var previousNumber = (season?.Number ?? ((latest?.Number ?? 0) + 1)) - 1;
            if (previousNumber > 0 && await store.GetSeasonByNumberAsync(previousNumber, ct) is SeasonRecord prev)
            {
                var prevStanding = await store.GetStandingAsync(prev.Id, userId, ct);
                var reward = await store.GetRewardAsync(prev.Id, userId, ct);
                if (prevStanding is not null)
                    last = new LastSeasonDto(previousNumber, prevStanding.FinalRank, prevStanding.Outcome?.ToString().ToLowerInvariant(),
                        reward is null ? null : new RewardCellDto(reward.Reward.Coins, reward.Reward.Gems, reward.Reward.Shields), reward?.GrantedAt is not null);
            }
            return Results.Ok(new LeagueResponse(true, env.EnvironmentName, seasonDto, divisions, me, group, RewardTable(), last));
        }).RequireAuthorization().RequireRateLimiting("api");

        app.MapGet("/league/rewards", () => Results.Ok(RewardTable())).RequireAuthorization().RequireRateLimiting("api");

        // Public profile by public id (friend code). Shows only public fields, respecting the player's privacy settings.
        app.MapGet("/players/{publicId}", async (string publicId, HttpContext http, IProfileStore profiles, ISocialStore social, ILeagueStore store, LeagueService league, CancellationToken ct) =>
        {
            var viewer = http.User.UserId();
            var code = publicId.Trim().ToUpperInvariant();
            var p = code.Length == 8 ? await profiles.GetProfileByFriendCodeAsync(code, ct) : null;
            if (p is null || await social.IsBlockedEitherWayAsync(viewer, p.UserId, ct)) return Results.NotFound();
            var isYou = p.UserId == viewer;
            var privacy = await profiles.GetPrivacyAsync(p.UserId, ct);
            var statsVisible = isYou || privacy.ShowStats;
            var leagueVisible = isYou || privacy.ShowLeague;
            var friends = await social.GetFriendsAsync(viewer, ct);

            ModeStatsDto? overall = null;
            var modes = new List<ModeStatsDto>();
            if (statsVisible)
            {
                var s = await profiles.GetStatsAsync(p.UserId, ct);
                overall = new ModeStatsDto("all", "All modes", s.GamesPlayed, s.GamesWon, Math.Max(0, s.GamesPlayed - s.GamesWon - s.Abandons), s.Abandons,
                    s.GamesPlayed == 0 ? 0 : (int)Math.Round(100.0 * s.GamesWon / s.GamesPlayed), s.CurrentWinStreak, s.BestWinStreak);
                modes = (await profiles.GetModeStatsAsync(p.UserId, ct)).Select(ToDto).ToList();
            }

            PublicLeagueDto? leagueDto = null;
            if (leagueVisible && league.Enabled && await store.GetCurrentSeasonAsync(ct) is SeasonRecord season)
            {
                var standing = await store.GetStandingAsync(season.Id, p.UserId, ct);
                if (standing is not null)
                {
                    var rows = await store.GetGroupAsync(standing.GroupId, LeagueRules.GroupSize, ct);
                    var rank = rows.ToList().FindIndex(r => r.UserId == p.UserId) + 1;
                    leagueDto = new PublicLeagueDto(season.Number, standing.Division, LeagueRules.DivisionAt(standing.Division).Name, standing.Points, rank == 0 ? null : rank);
                }
                else if (await store.GetDivisionAsync(p.UserId, ct) is int d)
                    leagueDto = new PublicLeagueDto(season.Number, d, LeagueRules.DivisionAt(d).Name, 0, null);
            }

            return Results.Ok(new PublicProfileDto(p.FriendCode, p.DisplayName, p.AvatarId, p.FrameId, p.Level, isYou,
                friends.Any(f => f.UserId == p.UserId && f.Status == FriendshipStatus.Accepted), statsVisible, overall, modes, leagueVisible, leagueDto));
        }).RequireAuthorization().RequireRateLimiting("api");

        var me = app.MapGroup("/me").RequireAuthorization().RequireRateLimiting("api");
        me.MapGet("/privacy", async (HttpContext http, IProfileStore profiles, CancellationToken ct) => Results.Ok(await profiles.GetPrivacyAsync(http.User.UserId(), ct)));
        me.MapPatch("/privacy", async (PrivacyRequest req, HttpContext http, IProfileStore profiles, Matches.MatchService matches, CancellationToken ct) =>
        {
            var id = http.User.UserId();
            var updated = await profiles.UpdatePrivacyAsync(id, req.ShowStats, req.ShowLeague, req.ChatEnabled, ct);
            if (req.ChatEnabled is bool chat) await matches.OnChatSettingChangedAsync(id, chat);
            return Results.Ok(updated);
        });
        me.MapGet("/stats/modes", async (HttpContext http, IProfileStore profiles, CancellationToken ct) =>
            Results.Ok((await profiles.GetModeStatsAsync(http.User.UserId(), ct)).Select(ToDto)));
    }

    /// <summary>DEVELOPMENT ONLY: end the current season now to test finalisation and rewards locally.</summary>
    public static void MapDevLeagueEndpoints(this WebApplication app)
    {
        app.MapPost("/dev/league/end-season", async (LeagueService league, CancellationToken ct) =>
        {
            var next = await league.EndCurrentSeasonNowAsync(ct);
            return Results.Ok(new { newSeason = next?.Number });
        }).RequireAuthorization().RequireRateLimiting("api");
    }
}
