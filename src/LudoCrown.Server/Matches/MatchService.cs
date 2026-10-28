using System.Collections.Concurrent;
using LudoCrown.Core.AI;
using LudoCrown.Core.Dice;
using LudoCrown.Core.Engine;
using LudoCrown.Core.Model;
using LudoCrown.Core.Protocol;
using LudoCrown.Core.Serialization;
using LudoCrown.Server.Data;
using LudoCrown.Server.Options;
using Microsoft.Extensions.Options;

namespace LudoCrown.Server.Matches;

/// <summary>
/// Owns every live match and is the only code allowed to change match state.
///
/// Anti-cheat model: clients send intents only ("roll", "move piece N", with the state version they saw).
/// The server rolls the dice itself, validates turn/phase/version/legality with the shared rules engine,
/// applies the move, persists the new state, and pushes the authoritative snapshot to every player.
/// Results and rewards are computed here and written by the server only.
/// </summary>
public sealed class MatchService(
    IMatchStore store,
    IProfileStore profiles,
    IWalletStore wallet,
    ISocialStore social,
    IMatchNotifier notifier,
    IDiceRoller dice,
    IOptions<MatchOptions> options,
    TimeProvider time,
    ILogger<MatchService> logger)
{
    private static readonly string[] BotNames = { "Duchess Bot", "Baron Bot", "Squire Bot", "Countess Bot", "Marquis Bot", "Jester Bot" };

    private readonly ConcurrentDictionary<Guid, MatchRoom> _rooms = new();
    private readonly ConcurrentDictionary<Guid, Guid> _activeMatchByUser = new();
    private readonly ConcurrentDictionary<Guid, int> _connections = new();
    private MatchOptions O => options.Value;

    public int LiveMatchCount => _rooms.Count(r => !r.Value.Ended);

    public Guid? ActiveMatchFor(Guid userId) => _activeMatchByUser.TryGetValue(userId, out var id) ? id : null;

    internal MatchRoom? GetRoom(Guid matchId) => _rooms.GetValueOrDefault(matchId);

    public bool IsConnected(Guid userId) => _connections.GetValueOrDefault(userId) > 0;

    /// <summary>Starts a match. A null entry in <paramref name="seatUsers"/> is a bot seat. The game mode is fixed for the whole match.</summary>
    public async Task<Guid> StartMatchAsync(MatchMode mode, IReadOnlyList<Guid?> seatUsers, string? roomCode, GameMode gameMode = GameMode.Classic, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var matchId = Guid.NewGuid();
        var humanIds = seatUsers.Where(u => u is not null).Select(u => u!.Value).ToList();
        var profileMap = await profiles.GetProfilesAsync(humanIds, ct);
        var botIndex = Random.Shared.Next(BotNames.Length);

        var slots = seatUsers.Select((u, i) =>
        {
            if (u is Guid id)
            {
                var p = profileMap.GetValueOrDefault(id);
                return new SeatSlot { Seat = i, UserId = id, DisplayName = p?.DisplayName ?? "Player", AvatarId = p?.AvatarId ?? "crown", Connected = IsConnected(id), DisconnectedAt = IsConnected(id) ? null : now };
            }
            return new SeatSlot { Seat = i, IsBot = true, DisplayName = BotNames[(botIndex + i) % BotNames.Length], AvatarId = Avatars.All[(botIndex + i) % Avatars.All.Count], Connected = true };
        }).ToArray();

        var state = GameSetup.NewGame(matchId.ToString("N"), slots.Select(s => new SeatSetup(s.UserId?.ToString() ?? $"bot-{s.Seat}", s.DisplayName, s.IsBot ? PlayerKind.Bot : PlayerKind.Remote)).ToList(),
            RuleSet.ForMode(gameMode));
        foreach (var slot in slots.Where(s => s.IsHuman)) slot.Gems = await wallet.GetGemsAsync(slot.UserId!.Value, ct);
        var room = new MatchRoom
        {
            Id = matchId,
            Mode = mode,
            RoomCode = roomCode,
            State = state,
            Seats = slots,
            StartedAt = now,
            Bot = BotFactory.Create(BotDifficulty.Normal, new Random()),
        };
        room.Deadline = ComputeDeadline(room, now);

        var record = new MatchRecord(matchId, mode, slots.Length, roomCode, now,
            slots.Select(s => new MatchParticipantRecord(s.Seat, s.UserId, s.IsBot, s.DisplayName, state.Players[s.Seat].Color.ToString())).ToList(), gameMode);
        await store.CreateMatchAsync(record, GameStateSerializer.Serialize(state), ct);

        _rooms[matchId] = room;
        foreach (var id in humanIds) _activeMatchByUser[id] = matchId;
        logger.LogInformation("Match {MatchId} started ({Mode}, {Players} players, {Humans} humans)", matchId, mode, slots.Length, humanIds.Count);

        await room.Gate.WaitAsync(ct);
        try { await BroadcastAsync(room); }
        finally { room.Gate.Release(); }
        return matchId;
    }

    /// <summary>
    /// Rolls for the player. If the roll has no legal move and the player can afford a reroll, the roll is held so they
    /// can choose to reroll (gem) or pass; otherwise the turn passes immediately as in the base rules.
    /// </summary>
    public Task<ServerError?> RollAsync(Guid userId, string matchId, long expectedVersion) =>
        HumanCommandAsync(userId, matchId, expectedVersion, (room, slot) => GameEngine.Roll(room.State, slot.Seat, dice, CanAffordReroll(room, slot, afterSpending: 0)));

    public Task<ServerError?> MoveAsync(Guid userId, string matchId, int pieceIndex, long expectedVersion) =>
        HumanCommandAsync(userId, matchId, expectedVersion, (room, slot) => GameEngine.Move(room.State, slot.Seat, pieceIndex));

    /// <summary>Ends the turn after a held roll that has no legal move.</summary>
    public Task<ServerError?> PassAsync(Guid userId, string matchId, long expectedVersion) =>
        HumanCommandAsync(userId, matchId, expectedVersion, (room, slot) => GameEngine.Pass(room.State, slot.Seat));

    /// <summary>
    /// Gem reroll: replaces the player's pending (unused) roll with a new server-rolled value.
    /// Order of checks: request id → match → seat → idempotency cache → version → rules (turn, phase, limit) → gem spend.
    /// The gem is spent through <see cref="IWalletStore.SpendGemsAsync"/> keyed by match + request id, so a retried or
    /// replayed request (e.g. after reconnecting) never charges twice. Nothing is charged when any check fails.
    /// </summary>
    public async Task<RerollResult> RerollAsync(Guid userId, string matchId, long expectedVersion, string requestId)
    {
        if (CommandValidator.ValidateRequestId(requestId) != ErrorCode.None) return RerollFail(ErrorCode.InvalidRequest, null, null, "Invalid request id.");
        var room = Find(matchId);
        if (room is null) return RerollFail(ErrorCode.UnknownMatch, null);
        await room.Gate.WaitAsync();
        try
        {
            var slot = room.SeatOf(userId);
            if (slot is null || slot.Abandoned) return RerollFail(ErrorCode.NotInMatch, null);
            var key = $"{userId:N}:{requestId}";
            if (room.ProcessedRerolls.TryGetValue(key, out var previous)) return previous; // retried request: same answer, no new charge
            var player = room.State.Players[slot.Seat];

            if (room.Ended) return RerollFail(ErrorCode.GameOver, slot, room);
            var versionError = CommandValidator.ValidateVersion(room.State, expectedVersion);
            if (versionError != ErrorCode.None)
            {
                await notifier.SendSnapshotAsync(userId, room.SnapshotFor(userId));
                return RerollFail(versionError, slot, room);
            }
            var ruleError = GameEngine.ValidateReroll(room.State, slot.Seat);
            if (ruleError != ErrorCode.None) return RerollFail(ruleError, slot, room);
            if (slot.Gems < GameModes.RerollGemCost) return RerollFail(ErrorCode.InsufficientGems, slot, room);

            var spend = await wallet.SpendGemsAsync(userId, room.Id, $"reroll:{room.Id:N}:{requestId}", GameModes.RerollGemCost,
                Economy.Reasons.Reroll, time.GetUtcNow());
            slot.Gems = spend.Balance;
            if (!spend.Charged) return RerollFail(ErrorCode.InsufficientGems, slot, room);

            var result = GameEngine.Reroll(room.State, slot.Seat, dice, CanAffordReroll(room, slot, afterSpending: 0, rerollsAfter: player.RerollsUsed + 1));
            if (!result.Ok)
            {
                // Should be impossible after ValidateReroll; logged loudly because a gem was taken.
                logger.LogError("Reroll rejected after gem spend in match {MatchId}: {Error}", room.Id, result.Error);
                return RerollFail(result.Error, slot, room);
            }
            slot.MissedTurns = 0;
            await CommitAsync(room, result);
            var used = room.State.Players[slot.Seat].RerollsUsed;
            var ok = new RerollResult(null, (int)slot.Gems, used, Math.Max(0, room.State.Rules.MaxRerolls - used), result.State.LastRoll);
            room.ProcessedRerolls[key] = ok;
            return ok;
        }
        finally { room.Gate.Release(); }
    }

    /// <summary>Whether a reroll could be paid for right now (used to decide if a no-move roll is held).</summary>
    private static bool CanAffordReroll(MatchRoom room, SeatSlot slot, long afterSpending, int? rerollsAfter = null)
    {
        if (!slot.IsHuman || slot.ServerControlled || !slot.Connected) return false;
        var used = rerollsAfter ?? room.State.Players[slot.Seat].RerollsUsed;
        return used < room.State.Rules.MaxRerolls && slot.Gems - afterSpending >= GameModes.RerollGemCost;
    }

    private static RerollResult RerollFail(ErrorCode code, SeatSlot? slot, MatchRoom? room = null, string? message = null)
    {
        var used = slot is not null && room is not null ? room.State.Players[slot.Seat].RerollsUsed : 0;
        var remaining = room is not null ? Math.Max(0, room.State.Rules.MaxRerolls - used) : 0;
        return new RerollResult(Error(code, message), slot is null ? null : (int)slot.Gems, used, remaining, null);
    }

    private async Task<ServerError?> HumanCommandAsync(Guid userId, string matchId, long expectedVersion, Func<MatchRoom, SeatSlot, EngineResult> action)
    {
        var room = Find(matchId);
        if (room is null) return Error(ErrorCode.UnknownMatch);
        await room.Gate.WaitAsync();
        try
        {
            if (room.Ended) return Error(ErrorCode.GameOver);
            var slot = room.SeatOf(userId);
            if (slot is null) return Error(ErrorCode.NotInMatch);
            if (slot.Abandoned) return Error(ErrorCode.NotInMatch, "You left this match.");
            var versionError = CommandValidator.ValidateVersion(room.State, expectedVersion);
            if (versionError != ErrorCode.None)
            {
                // The client is behind: send it the current state so it can catch up.
                await notifier.SendSnapshotAsync(userId, room.SnapshotFor(userId));
                return Error(versionError);
            }

            var result = action(room, slot);
            if (!result.Ok) return Error(result.Error);
            slot.MissedTurns = 0;
            await CommitAsync(room, result);
            return null;
        }
        finally { room.Gate.Release(); }
    }

    public async Task<MatchSnapshot?> ResyncAsync(Guid userId, string? matchId)
    {
        var room = string.IsNullOrEmpty(matchId)
            ? (ActiveMatchFor(userId) is Guid id ? _rooms.GetValueOrDefault(id) : null)
            : Find(matchId);
        if (room is null || room.SeatOf(userId) is null) return null;
        await room.Gate.WaitAsync();
        try { return room.SnapshotFor(userId); }
        finally { room.Gate.Release(); }
    }

    /// <summary>The player leaves voluntarily. A bot takes over their seat; they get no rewards for the match.</summary>
    public async Task<ServerError?> LeaveAsync(Guid userId, string matchId)
    {
        var room = Find(matchId);
        if (room is null) return Error(ErrorCode.UnknownMatch);
        await room.Gate.WaitAsync();
        try
        {
            var slot = room.SeatOf(userId);
            if (slot is null) return Error(ErrorCode.NotInMatch);
            if (room.Ended || slot.Abandoned) { _activeMatchByUser.TryRemove(userId, out _); return null; }
            await AbandonAsync(room, slot, "left");
            return null;
        }
        finally { room.Gate.Release(); }
    }

    public async Task<ServerError?> ReactAsync(Guid userId, string matchId, string reactionId)
    {
        if (!Reactions.IsValid(reactionId)) return Error(ErrorCode.InvalidRequest);
        var room = Find(matchId);
        if (room is null) return Error(ErrorCode.UnknownMatch);
        var now = time.GetUtcNow();
        SeatSlot? sender;
        List<Guid> recipients;
        await room.Gate.WaitAsync();
        try
        {
            sender = room.SeatOf(userId);
            if (sender is null || sender.Abandoned) return Error(ErrorCode.NotInMatch);
            if (now - sender.LastReactionAt < TimeSpan.FromSeconds(2)) return Error(ErrorCode.RateLimited);
            sender.LastReactionAt = now;
            recipients = room.Humans.Where(h => !h.Abandoned).Select(h => h.UserId!.Value).ToList();
        }
        finally { room.Gate.Release(); }

        foreach (var r in recipients)
        {
            if (r != userId && await social.IsBlockedEitherWayAsync(userId, r)) continue;
            await notifier.SendReactionAsync(r, new ReactionInfo(sender.Seat, reactionId));
        }
        return null;
    }

    public async Task OnConnectedAsync(Guid userId)
    {
        var count = _connections.AddOrUpdate(userId, 1, (_, c) => c + 1);
        if (count != 1) { await SendCurrentSnapshotAsync(userId); return; }
        if (ActiveMatchFor(userId) is not Guid matchId || !_rooms.TryGetValue(matchId, out var room)) return;
        await room.Gate.WaitAsync();
        try
        {
            var slot = room.SeatOf(userId);
            if (slot is null || room.Ended) return;
            slot.Connected = true;
            slot.DisconnectedAt = null;
            if (!slot.Abandoned && room.CurrentSlot == slot) room.Deadline = ComputeDeadline(room, time.GetUtcNow());
            room.LastEvents = Array.Empty<GameEvent>();
            await BroadcastAsync(room);
        }
        finally { room.Gate.Release(); }
    }

    public async Task OnDisconnectedAsync(Guid userId)
    {
        var count = _connections.AddOrUpdate(userId, 0, (_, c) => Math.Max(0, c - 1));
        if (count != 0) return;
        _connections.TryRemove(new KeyValuePair<Guid, int>(userId, 0));
        if (ActiveMatchFor(userId) is not Guid matchId || !_rooms.TryGetValue(matchId, out var room)) return;
        await room.Gate.WaitAsync();
        try
        {
            var slot = room.SeatOf(userId);
            if (slot is null || room.Ended) return;
            var now = time.GetUtcNow();
            slot.Connected = false;
            slot.DisconnectedAt = now;
            if (room.CurrentSlot == slot)
            {
                var shorter = now.AddSeconds(O.DisconnectedTurnSeconds);
                if (shorter < room.Deadline) room.Deadline = shorter;
            }
            room.LastEvents = Array.Empty<GameEvent>();
            await BroadcastAsync(room);
        }
        finally { room.Gate.Release(); }
    }

    /// <summary>Removes the user from any live match (account deletion).</summary>
    public async Task OnAccountDeletedAsync(Guid userId)
    {
        if (ActiveMatchFor(userId) is Guid id) await LeaveAsync(userId, id.ToString("N"));
        _activeMatchByUser.TryRemove(userId, out _);
    }

    /// <summary>Called periodically: enforces turn timers, disconnect grace periods and bot turns.</summary>
    public async Task TickAsync()
    {
        foreach (var room in _rooms.Values)
        {
            var now = time.GetUtcNow();
            if (room.Ended)
            {
                if (room.EndedAt is DateTimeOffset ended && now - ended > TimeSpan.FromSeconds(O.EndedMatchRetentionSeconds))
                    _rooms.TryRemove(room.Id, out _);
                continue;
            }

            if (!await room.Gate.WaitAsync(0)) continue; // busy: try again next tick
            try
            {
                foreach (var slot in room.Humans.Where(h => !h.Abandoned && !h.Connected && h.DisconnectedAt is not null).ToList())
                {
                    if (now - slot.DisconnectedAt!.Value > TimeSpan.FromSeconds(O.DisconnectGraceSeconds))
                        await AbandonAsync(room, slot, "disconnected");
                    if (room.Ended) break;
                }
                if (room.Ended || now < room.Deadline) continue;
                await AutoActAsync(room);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Tick failed for match {MatchId}", room.Id);
            }
            finally { room.Gate.Release(); }
        }
    }

    /// <summary>
    /// After a restart, reloads unfinished matches from the database. Everyone starts disconnected and
    /// rejoins automatically when their app reconnects; the normal grace period applies.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        foreach (var active in await store.GetActiveMatchesAsync(ct))
        {
            try
            {
                var state = GameStateSerializer.Deserialize(active.StateJson);
                var slots = active.Match.Participants.OrderBy(p => p.Seat).Select(p => new SeatSlot
                {
                    Seat = p.Seat,
                    UserId = p.IsBot ? null : p.UserId,
                    IsBot = p.IsBot || p.UserId is null,
                    DisplayName = p.DisplayName,
                    Connected = p.IsBot,
                    DisconnectedAt = p.IsBot ? null : now,
                    Abandoned = !p.IsBot && p.UserId is null, // account deleted mid-match
                }).ToArray();
                var room = new MatchRoom
                {
                    Id = active.Match.Id,
                    Mode = active.Match.Mode,
                    RoomCode = active.Match.RoomCode,
                    State = state,
                    Seats = slots,
                    StartedAt = active.Match.StartedAt,
                    Bot = BotFactory.Create(BotDifficulty.Normal, new Random()),
                };
                foreach (var s in slots.Where(s => s.IsHuman)) s.Gems = await wallet.GetGemsAsync(s.UserId!.Value, ct);
                var profileMap = await profiles.GetProfilesAsync(slots.Where(s => s.UserId is not null).Select(s => s.UserId!.Value), ct);
                foreach (var s in slots.Where(s => s.UserId is not null))
                    if (profileMap.TryGetValue(s.UserId!.Value, out var p)) s.AvatarId = p.AvatarId;
                room.Deadline = ComputeDeadline(room, now);
                _rooms[room.Id] = room;
                foreach (var s in slots.Where(s => s.IsHuman)) _activeMatchByUser[s.UserId!.Value] = room.Id;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not recover match {MatchId}", active.Match.Id);
            }
        }
        logger.LogInformation("Recovered {Count} live matches", _rooms.Count);
    }

    // ---------------- internals (caller holds room.Gate) ----------------

    private async Task AutoActAsync(MatchRoom room)
    {
        var slot = room.CurrentSlot;
        var seat = slot.Seat;

        if (!slot.ServerControlled && GameEngine.IsHeldWithoutMoves(room.State))
        {
            // The reroll decision window ended without a choice: pass. Not a missed turn — the player had nothing to move.
            await CommitAsync(room, GameEngine.Pass(room.State, seat));
            return;
        }

        if (!slot.ServerControlled)
        {
            // A human ran out of time: count the miss once per turn, and play the whole turn for them.
            if (slot.LastMissedTurnNumber != room.State.TurnNumber)
            {
                slot.MissedTurns++;
                slot.LastMissedTurnNumber = room.State.TurnNumber;
            }
            if (slot.Connected && slot.MissedTurns >= O.MaxMissedTurns)
            {
                await AbandonAsync(room, slot, "inactive");
                return;
            }
            if (room.State.Phase == GamePhase.AwaitingRoll)
            {
                await CommitAsync(room, GameEngine.Roll(room.State, seat, dice));
                if (room.Ended || room.State.CurrentPlayer.Seat != seat || room.State.Phase != GamePhase.AwaitingMove) return;
            }
            var choice = room.Bot.ChooseMove(room.State);
            await CommitAsync(room, choice is int p ? GameEngine.Move(room.State, seat, p) : NoMove(room.State, seat));
            return;
        }

        // Bots act one step per tick so humans can follow along.
        if (room.State.Phase == GamePhase.AwaitingRoll)
        {
            await CommitAsync(room, GameEngine.Roll(room.State, seat, dice));
        }
        else
        {
            var choice = room.Bot.ChooseMove(room.State);
            await CommitAsync(room, choice is int p ? GameEngine.Move(room.State, seat, p) : NoMove(room.State, seat));
        }
    }

    private static EngineResult NoMove(GameState state, int seat) =>
        GameEngine.IsHeldWithoutMoves(state) ? GameEngine.Pass(state, seat) : GameEngine.ForfeitTurn(state, seat);

    private async Task CommitAsync(MatchRoom room, EngineResult result)
    {
        if (!result.Ok)
        {
            logger.LogError("Server-side action rejected in match {MatchId}: {Error}", room.Id, result.Error);
            return;
        }
        room.State = result.State;
        room.LastEvents = result.Events;
        foreach (var e in result.Events.Where(e => e.Type == GameEventType.PieceCaptured))
            room.Seats[e.Value].Captures++; // Value = capturing seat

        if (room.State.Phase == GamePhase.Finished)
        {
            await CompleteAsync(room, MatchStatus.Completed, "won");
            return;
        }
        room.Deadline = ComputeDeadline(room, time.GetUtcNow());
        await store.SaveLiveStateAsync(room.Id, GameStateSerializer.Serialize(room.State), room.State.Version);
        await BroadcastAsync(room);
    }

    private async Task AbandonAsync(MatchRoom room, SeatSlot slot, string reason)
    {
        slot.Abandoned = true;
        _activeMatchByUser.TryRemove(slot.UserId!.Value, out _);
        logger.LogInformation("Seat {Seat} in match {MatchId} abandoned ({Reason})", slot.Seat, room.Id, reason);

        if (room.Humans.All(h => h.Abandoned))
        {
            await CompleteAsync(room, MatchStatus.Abandoned, "abandoned");
            return;
        }
        if (room.CurrentSlot == slot) room.Deadline = ComputeDeadline(room, time.GetUtcNow());
        room.LastEvents = Array.Empty<GameEvent>();
        await BroadcastAsync(room);
    }

    private async Task CompleteAsync(MatchRoom room, MatchStatus status, string reason)
    {
        var now = time.GetUtcNow();
        room.Ended = true;
        room.EndedAt = now;
        var standings = room.State.Standings();
        room.FinalStandings = standings;
        var humanCount = room.Humans.Count();

        var outcomes = room.Seats.Select(slot =>
        {
            var placement = standings.ToList().IndexOf(slot.Seat) + 1;
            var result = slot.IsHuman && slot.Abandoned ? ParticipantResult.Abandoned
                : room.State.WinnerSeat == slot.Seat ? ParticipantResult.Win
                : ParticipantResult.Loss;
            var (coins, xp) = slot.IsHuman ? RewardPolicy.For(room.Mode, room.Seats.Length, placement, result, humanCount) : (0, 0);
            var gems = slot.IsHuman ? RewardPolicy.GemsFor(room.Mode, placement, result) : 0;
            return new ParticipantOutcome(slot.Seat, slot.UserId, placement, result, coins, xp, slot.Captures, gems, room.State.Players[slot.Seat].RerollsUsed);
        }).ToList();

        var winnerSlot = room.State.WinnerSeat is int w ? room.Seats[w] : null;
        var completion = new MatchCompletion(room.Id, status, now,
            winnerSlot is { IsHuman: true, Abandoned: false } ? winnerSlot.UserId : null,
            GameStateSerializer.Serialize(room.State), outcomes, room.Seats.Length);

        try { await store.CompleteMatchAsync(completion); }
        catch (Exception ex) { logger.LogError(ex, "Failed to record result for match {MatchId}", room.Id); }

        foreach (var slot in room.Humans) _activeMatchByUser.TryRemove(new KeyValuePair<Guid, Guid>(slot.UserId!.Value, room.Id));

        await BroadcastAsync(room);
        foreach (var o in outcomes.Where(o => o.UserId is not null && !room.Seats[o.Seat].Abandoned))
            await notifier.SendMatchEndedAsync(o.UserId!.Value, new MatchEndedInfo(room.MatchIdString, standings, o.Placement, o.CoinsAwarded, o.XpAwarded, reason, o.GemsAwarded, room.State.Mode));
        logger.LogInformation("Match {MatchId} ended ({Reason}), winner seat {Winner}", room.Id, reason, room.State.WinnerSeat);
    }

    private DateTimeOffset ComputeDeadline(MatchRoom room, DateTimeOffset now)
    {
        var slot = room.CurrentSlot;
        if (slot.ServerControlled) return now.AddSeconds(O.BotDelaySeconds);
        if (!slot.Connected) return now.AddSeconds(O.DisconnectedTurnSeconds);
        if (GameEngine.IsHeldWithoutMoves(room.State)) return now.AddSeconds(O.HeldRollSeconds);
        return now.AddSeconds(O.TurnSeconds);
    }

    private async Task BroadcastAsync(MatchRoom room)
    {
        foreach (var slot in room.Humans.Where(h => !h.Abandoned))
            await notifier.SendSnapshotAsync(slot.UserId!.Value, room.SnapshotFor(slot.UserId));
    }

    private async Task SendCurrentSnapshotAsync(Guid userId)
    {
        var snap = await ResyncAsync(userId, null);
        if (snap is not null) await notifier.SendSnapshotAsync(userId, snap);
    }

    private MatchRoom? Find(string? matchId) =>
        CommandValidator.ValidateMatchId(matchId) == ErrorCode.None && Guid.TryParse(matchId, out var id) ? _rooms.GetValueOrDefault(id) : null;

    private static ServerError Error(ErrorCode code, string? message = null) => new(code, message ?? code switch
    {
        ErrorCode.NotYourTurn => "It's not your turn.",
        ErrorCode.WrongPhase => "That action isn't available right now.",
        ErrorCode.IllegalMove => "That piece can't move with this roll.",
        ErrorCode.InvalidPiece => "Unknown piece.",
        ErrorCode.StaleVersion => "Your board was out of date and has been refreshed.",
        ErrorCode.GameOver => "This match has ended.",
        ErrorCode.UnknownMatch => "Match not found.",
        ErrorCode.NotInMatch => "You're not playing in this match.",
        ErrorCode.RateLimited => "Slow down a little.",
        ErrorCode.NoRerollsLeft => "You've used all rerolls allowed in this mode.",
        ErrorCode.InsufficientGems => "Not enough gems for a reroll.",
        ErrorCode.RerollNotAllowed => "You can only reroll a roll you haven't used yet.",
        ErrorCode.InvalidMode => "That game mode isn't available.",
        _ => "Request rejected.",
    });
}
