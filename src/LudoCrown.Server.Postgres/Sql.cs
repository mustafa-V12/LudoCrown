namespace LudoCrown.Server.Postgres;

/// <summary>
/// Every SQL statement the server runs. Parameters are positional ($1, $2, …) with explicit casts so each
/// statement can be validated against the real schema with PREPARE (see tools/verify-sql.py).
/// </summary>
public static class Sql
{
    // ---------- accounts ----------
    public const string InsertUser = "INSERT INTO users (id, is_guest, created_at) VALUES ($1::uuid, $2::boolean, $3::timestamptz)";
    public const string InsertProfile = "INSERT INTO profiles (user_id, display_name, avatar_id, friend_code, xp, updated_at) VALUES ($1::uuid, $2::text, $3::text, $4::text, 0, $5::timestamptz) ON CONFLICT (friend_code) DO NOTHING";
    public const string InsertStats = "INSERT INTO player_stats (user_id) VALUES ($1::uuid)";
    public const string InsertWallet = "INSERT INTO wallets (user_id, coins, gems) VALUES ($1::uuid, 0, $2::bigint)";
    public const string GetUser = "SELECT id, is_guest, created_at FROM users WHERE id = $1::uuid";
    public const string FindUserByIdentity = "SELECT user_id FROM auth_identities WHERE provider = $1::text AND subject = $2::text";
    public const string InsertIdentity = "INSERT INTO auth_identities (user_id, provider, subject, email, created_at) VALUES ($1::uuid, $2::text, $3::text, $4::text, $5::timestamptz) ON CONFLICT (provider, subject) DO NOTHING";
    public const string GetIdentities = "SELECT user_id, provider, subject, email, created_at FROM auth_identities WHERE user_id = $1::uuid ORDER BY created_at";
    public const string SetGuest = "UPDATE users SET is_guest = $2::boolean WHERE id = $1::uuid";
    public const string AnonymiseParticipants = "UPDATE match_participants SET display_name = 'Deleted player' WHERE user_id = $1::uuid";
    public const string DeleteUser = "DELETE FROM users WHERE id = $1::uuid"; // cascades to identities, profile, stats, wallet, tokens, friends, blocks
    public const string TouchUser = "UPDATE users SET last_seen_at = $2::timestamptz WHERE id = $1::uuid";

    public const string InsertRefreshToken = "INSERT INTO refresh_tokens (id, user_id, token_hash, created_at, expires_at) VALUES ($1::uuid, $2::uuid, $3::bytea, $4::timestamptz, $5::timestamptz)";
    public const string GetRefreshToken = "SELECT id, user_id, token_hash, created_at, expires_at, revoked_at, replaced_by FROM refresh_tokens WHERE token_hash = $1::bytea";
    public const string RevokeRefreshToken = "UPDATE refresh_tokens SET revoked_at = $3::timestamptz, replaced_by = $2::uuid WHERE id = $1::uuid AND revoked_at IS NULL";
    public const string RevokeAllRefreshTokens = "UPDATE refresh_tokens SET revoked_at = $2::timestamptz WHERE user_id = $1::uuid AND revoked_at IS NULL";

    public const string InsertAuthCode = "INSERT INTO auth_codes (id, kind, code_hash, user_id, payload, expires_at, attempts) VALUES ($1::uuid, $2::text, $3::bytea, $4::uuid, $5::text, $6::timestamptz, $7::integer)";
    public const string ConsumeAuthCode = """
        UPDATE auth_codes SET consumed_at = $3::timestamptz
        WHERE kind = $1::text AND code_hash = $2::bytea AND consumed_at IS NULL AND expires_at > $3::timestamptz
        RETURNING id, kind, code_hash, user_id, payload, expires_at, attempts
        """;
    public const string FindLiveAuthCode = """
        SELECT id, kind, code_hash, user_id, payload, expires_at, attempts FROM auth_codes
        WHERE kind = $1::text AND payload = $2::text AND consumed_at IS NULL AND expires_at > $3::timestamptz
        ORDER BY expires_at DESC LIMIT 1
        """;
    public const string IncrementAuthCodeAttempts = "UPDATE auth_codes SET attempts = attempts + 1 WHERE id = $1::uuid RETURNING attempts";
    public const string InvalidateAuthCodes = "UPDATE auth_codes SET consumed_at = now() WHERE kind = $1::text AND payload = $2::text AND consumed_at IS NULL";
    public const string PurgeAuthCodes = "DELETE FROM auth_codes WHERE expires_at < $1::timestamptz";
    public const string PurgeRefreshTokens = "DELETE FROM refresh_tokens WHERE expires_at < $1::timestamptz OR revoked_at < $1::timestamptz";

    // ---------- profiles ----------
    private const string ProfileColumns = "user_id, display_name, avatar_id, friend_code, xp, updated_at, frame_id";
    public const string GetProfile = $"SELECT {ProfileColumns} FROM profiles WHERE user_id = $1::uuid";
    public const string GetProfiles = $"SELECT {ProfileColumns} FROM profiles WHERE user_id = ANY($1::uuid[])";
    public const string GetProfileByFriendCode = $"SELECT {ProfileColumns} FROM profiles WHERE friend_code = $1::text";
    public const string UpdateProfile = $"""
        UPDATE profiles SET display_name = COALESCE($2::text, display_name), avatar_id = COALESCE($3::text, avatar_id), updated_at = $4::timestamptz
        WHERE user_id = $1::uuid RETURNING {ProfileColumns}
        """;
    public const string GetStats = "SELECT user_id, games_played, games_won, two_player_wins, four_player_wins, pieces_captured, abandons, current_win_streak, best_win_streak FROM player_stats WHERE user_id = $1::uuid";
    public const string GetCoins = "SELECT coins FROM wallets WHERE user_id = $1::uuid";
    public const string SetFrame = "UPDATE profiles SET frame_id = $2::text WHERE user_id = $1::uuid";
    public const string InsertProfileSettings = "INSERT INTO profile_settings (user_id) VALUES ($1::uuid) ON CONFLICT DO NOTHING";
    // Accounts created before migration 0003 have no settings row: defaults apply.
    public const string GetPrivacy = "SELECT show_stats, show_league, chat_enabled FROM profile_settings WHERE user_id = $1::uuid";
    public const string UpsertPrivacy = """
        INSERT INTO profile_settings (user_id, show_stats, show_league, chat_enabled, updated_at)
        SELECT u.id, COALESCE($2::boolean, true), COALESCE($3::boolean, true), COALESCE($4::boolean, true), $5::timestamptz FROM users u WHERE u.id = $1::uuid
        ON CONFLICT (user_id) DO UPDATE SET
            show_stats = COALESCE($2::boolean, profile_settings.show_stats),
            show_league = COALESCE($3::boolean, profile_settings.show_league),
            chat_enabled = COALESCE($4::boolean, profile_settings.chat_enabled),
            updated_at = $5::timestamptz
        RETURNING show_stats, show_league, chat_enabled
        """;
    public const string GetModeStats = "SELECT game_mode, games_played, games_won, games_lost, abandons, current_win_streak, best_win_streak FROM player_mode_stats WHERE user_id = $1::uuid";

    // ---------- gems ----------
    public const string GetGems = "SELECT gems FROM wallets WHERE user_id = $1::uuid";
    public const string InsertGemLedger = "INSERT INTO wallet_ledger (user_id, delta, reason, match_id, currency, idempotency_key) VALUES ($1::uuid, $2::bigint, $3::text, $4::uuid, 'gems', $5::text) ON CONFLICT DO NOTHING";

    /// <summary>
    /// One atomic statement: debit only if the balance covers the cost and the key is unused, and write the ledger row.
    /// A concurrent request with the same key makes the ledger insert hit ux_ledger_idempotency, which aborts the
    /// whole statement (debit included). Returns the new balance, or no row when nothing was charged.
    /// </summary>
    public const string SpendGemsOnce = """
        WITH debit AS (
            UPDATE wallets SET gems = gems - $2::bigint
            WHERE user_id = $1::uuid AND gems >= $2::bigint
              AND NOT EXISTS (SELECT 1 FROM wallet_ledger WHERE user_id = $1::uuid AND idempotency_key = $5::text)
            RETURNING gems
        ), ledger AS (
            INSERT INTO wallet_ledger (user_id, delta, reason, match_id, currency, idempotency_key)
            SELECT $1::uuid, -$2::bigint, $3::text, $4::uuid, 'gems', $5::text FROM debit
            RETURNING 1
        )
        SELECT gems FROM debit
        """;

    /// <summary>Why a spend charged nothing: key already used, current balance (NULL = no such wallet).</summary>
    public const string GemSpendState = "SELECT EXISTS (SELECT 1 FROM wallet_ledger WHERE user_id = $1::uuid AND idempotency_key = $2::text), (SELECT gems FROM wallets WHERE user_id = $1::uuid)";

    /// <summary>
    /// One atomic statement: write the ledger row (skipped if its idempotency key or match reward already exists)
    /// and credit the wallet only when the row was written. Returns the new balance, or no row when skipped.
    /// </summary>
    public const string CreditGemsOnce = """
        WITH ledger AS (
            INSERT INTO wallet_ledger (user_id, delta, reason, match_id, currency, idempotency_key)
            VALUES ($1::uuid, $2::bigint, $3::text, $4::uuid, 'gems', $5::text)
            ON CONFLICT DO NOTHING
            RETURNING delta
        )
        UPDATE wallets SET gems = gems + (SELECT delta FROM ledger)
        WHERE user_id = $1::uuid AND EXISTS (SELECT 1 FROM ledger)
        RETURNING gems
        """;

    // ---------- matches ----------
    public const string InsertMatch = "INSERT INTO matches (id, mode, player_count, room_code, started_at, state_json, state_version, game_mode) VALUES ($1::uuid, $2::text, $3::smallint, $4::text, $5::timestamptz, $6::jsonb, 0, $7::text)";
    public const string InsertParticipant = "INSERT INTO match_participants (match_id, seat, user_id, is_bot, display_name, color) VALUES ($1::uuid, $2::smallint, $3::uuid, $4::boolean, $5::text, $6::text)";
    public const string SaveLiveState = "UPDATE matches SET state_json = $2::jsonb, state_version = $3::bigint WHERE id = $1::uuid AND status = 'active' AND state_version <= $3::bigint";
    public const string LockActiveMatch = "SELECT id FROM matches WHERE id = $1::uuid AND status = 'active' FOR UPDATE";
    public const string FinishMatch = "UPDATE matches SET status = $2::text, ended_at = $3::timestamptz, winner_user_id = $4::uuid, state_json = $5::jsonb WHERE id = $1::uuid";
    public const string RecordParticipantResult = """
        UPDATE match_participants SET placement = $3::smallint, result = $4::text, coins_awarded = $5::integer, xp_awarded = $6::integer, pieces_captured = $7::integer,
            rerolls_used = $8::smallint, gems_awarded = $9::integer, boosts_used = $10::smallint, league_points = $11::smallint
        WHERE match_id = $1::uuid AND seat = $2::smallint
        """;
    /// <summary>Per-mode stats: $3 won, $4 lost, $5 abandoned. Streak resets on anything but a win.</summary>
    public const string ApplyModeStats = """
        INSERT INTO player_mode_stats AS s (user_id, game_mode, games_played, games_won, games_lost, abandons, current_win_streak, best_win_streak)
        VALUES ($1::uuid, $2::text, 1, CASE WHEN $3::boolean THEN 1 ELSE 0 END, CASE WHEN $4::boolean THEN 1 ELSE 0 END, CASE WHEN $5::boolean THEN 1 ELSE 0 END,
                CASE WHEN $3::boolean THEN 1 ELSE 0 END, CASE WHEN $3::boolean THEN 1 ELSE 0 END)
        ON CONFLICT (user_id, game_mode) DO UPDATE SET
            games_played = s.games_played + 1,
            games_won = s.games_won + EXCLUDED.games_won,
            games_lost = s.games_lost + EXCLUDED.games_lost,
            abandons = s.abandons + EXCLUDED.abandons,
            current_win_streak = CASE WHEN $3::boolean THEN s.current_win_streak + 1 ELSE 0 END,
            best_win_streak = GREATEST(s.best_win_streak, CASE WHEN $3::boolean THEN s.current_win_streak + 1 ELSE 0 END)
        """;
    /// <summary>League points for one result; only while the season is active. Totals never drop below 0.</summary>
    public const string ApplyLeaguePoints = """
        UPDATE league_standings ls SET
            points = GREATEST(0, ls.points + $3::integer),
            matches_played = ls.matches_played + 1,
            wins = ls.wins + CASE WHEN $4::boolean THEN 1 ELSE 0 END,
            last_points_at = $5::timestamptz
        FROM league_seasons s
        WHERE ls.season_id = $1::integer AND ls.user_id = $2::uuid AND s.id = ls.season_id AND s.status = 'active'
        """;
    public const string ApplyStats = """
        UPDATE player_stats SET
            games_played = games_played + 1,
            games_won = games_won + CASE WHEN $2::boolean THEN 1 ELSE 0 END,
            two_player_wins = two_player_wins + CASE WHEN $2::boolean AND $3::integer = 2 THEN 1 ELSE 0 END,
            four_player_wins = four_player_wins + CASE WHEN $2::boolean AND $3::integer = 4 THEN 1 ELSE 0 END,
            pieces_captured = pieces_captured + $4::integer,
            abandons = abandons + CASE WHEN $5::boolean THEN 1 ELSE 0 END,
            current_win_streak = CASE WHEN $2::boolean THEN current_win_streak + 1 ELSE 0 END,
            best_win_streak = GREATEST(best_win_streak, CASE WHEN $2::boolean THEN current_win_streak + 1 ELSE 0 END)
        WHERE user_id = $1::uuid
        """;
    public const string AddXp = "UPDATE profiles SET xp = xp + $2::integer WHERE user_id = $1::uuid";
    public const string AddCoins = "UPDATE wallets SET coins = coins + $2::bigint WHERE user_id = $1::uuid";
    public const string InsertLedger = "INSERT INTO wallet_ledger (user_id, delta, reason, match_id) VALUES ($1::uuid, $2::bigint, 'match_reward', $3::uuid) ON CONFLICT DO NOTHING";
    public const string GetActiveMatches = "SELECT id, mode, player_count, room_code, started_at, state_json::text, game_mode FROM matches WHERE status = 'active'";
    public const string GetParticipants = "SELECT match_id, seat, user_id, is_bot, display_name, color FROM match_participants WHERE match_id = ANY($1::uuid[]) ORDER BY match_id, seat";
    public const string GetHistory = """
        SELECT m.id, m.mode, m.player_count, m.started_at, m.ended_at, me.placement, me.result, me.coins_awarded, me.xp_awarded,
               COALESCE((SELECT array_agg(o.display_name ORDER BY o.seat) FROM match_participants o WHERE o.match_id = m.id AND o.seat <> me.seat), '{}') AS opponents,
               m.game_mode, me.rerolls_used, me.gems_awarded, me.league_points
        FROM match_participants me
        JOIN matches m ON m.id = me.match_id
        WHERE me.user_id = $1::uuid AND m.status <> 'active' AND me.result IS NOT NULL
          AND ($3::timestamptz IS NULL OR m.started_at < $3::timestamptz)
        ORDER BY m.started_at DESC
        LIMIT $2::integer
        """;
    public const string PurgeOldMatches = "DELETE FROM matches WHERE status <> 'active' AND ended_at < $1::timestamptz";

    // ---------- social ----------
    public const string GetFriends = """
        SELECT CASE WHEN f.user_low = $1::uuid THEN f.user_high ELSE f.user_low END AS other_id,
               p.display_name, p.avatar_id, p.xp, f.status, f.requested_by, p.friend_code
        FROM friendships f
        JOIN profiles p ON p.user_id = CASE WHEN f.user_low = $1::uuid THEN f.user_high ELSE f.user_low END
        WHERE f.user_low = $1::uuid OR f.user_high = $1::uuid
        ORDER BY p.display_name
        """;
    public const string GetFriendship = "SELECT status, requested_by FROM friendships WHERE user_low = $1::uuid AND user_high = $2::uuid FOR UPDATE";
    public const string InsertFriendship = "INSERT INTO friendships (user_low, user_high, status, requested_by, created_at) VALUES ($1::uuid, $2::uuid, 'pending', $3::uuid, $4::timestamptz) ON CONFLICT DO NOTHING";
    public const string AcceptFriendship = "UPDATE friendships SET status = 'accepted' WHERE user_low = $1::uuid AND user_high = $2::uuid AND status = 'pending' AND requested_by = $3::uuid";
    public const string DeleteFriendship = "DELETE FROM friendships WHERE user_low = $1::uuid AND user_high = $2::uuid";
    public const string InsertBlock = "INSERT INTO blocks (blocker_id, blocked_id, created_at) VALUES ($1::uuid, $2::uuid, $3::timestamptz) ON CONFLICT DO NOTHING";
    public const string DeleteBlock = "DELETE FROM blocks WHERE blocker_id = $1::uuid AND blocked_id = $2::uuid";
    public const string GetBlocked = "SELECT blocked_id FROM blocks WHERE blocker_id = $1::uuid ORDER BY created_at";
    public const string IsBlockedEitherWay = "SELECT EXISTS (SELECT 1 FROM blocks WHERE (blocker_id = $1::uuid AND blocked_id = $2::uuid) OR (blocker_id = $2::uuid AND blocked_id = $1::uuid))";
    public const string InsertReport = "INSERT INTO reports (id, reporter_id, reported_id, match_id, reason, details, created_at) VALUES ($1::uuid, $2::uuid, $3::uuid, $4::uuid, $5::text, $6::text, $7::timestamptz)";
    public const string DeleteReportsBy = "UPDATE reports SET reporter_id = NULL WHERE reporter_id = $1::uuid";

    // ---------- economy (coins, gems, inventory) ----------
    public const string GetWallet = "SELECT coins, gems FROM wallets WHERE user_id = $1::uuid";
    public const string GetInventory = "SELECT item_id, quantity FROM inventory WHERE user_id = $1::uuid AND quantity > 0";
    /// <summary>
    /// Applies one economy operation in a single statement: records the idempotency key, changes coins/gems/items and writes a
    /// ledger row per change. CHECK constraints (balances >= 0, item limits) abort the whole statement, so it is all or nothing.
    /// A repeated key inserts no operation row and therefore changes nothing. Returns 1 if applied, 0 if not.
    /// $1 user, $2 key, $3 reason, $4 match, $5 purchase, $6 coins delta, $7 gems delta, $8 item ids, $9 item deltas, $10 time.
    /// </summary>
    public const string ApplyEconomy = """
        WITH op AS (
            INSERT INTO economy_operations (user_id, idempotency_key, reason, match_id, purchase_id, created_at)
            SELECT $1::uuid, $2::text, $3::text, $4::uuid, $5::uuid, $10::timestamptz
            WHERE EXISTS (SELECT 1 FROM wallets WHERE user_id = $1::uuid)
            ON CONFLICT DO NOTHING
            RETURNING user_id
        ), w AS (
            UPDATE wallets SET coins = coins + $6::bigint, gems = gems + $7::bigint
            WHERE user_id IN (SELECT user_id FROM op) AND ($6::bigint <> 0 OR $7::bigint <> 0)
            RETURNING 1
        ), wl AS (
            INSERT INTO wallet_ledger (user_id, delta, reason, match_id, currency, purchase_id, operation_key, created_at)
            SELECT op.user_id, d.delta, $3::text, $4::uuid, d.currency, $5::uuid, $2::text, $10::timestamptz
            FROM op CROSS JOIN (VALUES ('coins', $6::bigint), ('gems', $7::bigint)) AS d(currency, delta)
            WHERE d.delta <> 0
            RETURNING 1
        ), inv_add AS (
            -- Items gained: create or increase the row (limits enforced by CHECK constraints).
            INSERT INTO inventory (user_id, item_id, quantity, updated_at)
            SELECT op.user_id, i.item, i.delta, $10::timestamptz FROM op CROSS JOIN unnest($8::text[], $9::integer[]) AS i(item, delta)
            WHERE i.delta > 0
            ON CONFLICT (user_id, item_id) DO UPDATE SET quantity = inventory.quantity + EXCLUDED.quantity, updated_at = EXCLUDED.updated_at
            RETURNING 1
        ), inv_use AS (
            -- Items spent: decrease existing rows only (CHECK quantity >= 0 rejects overspending).
            UPDATE inventory inv SET quantity = inv.quantity + i.delta, updated_at = $10::timestamptz
            FROM op CROSS JOIN unnest($8::text[], $9::integer[]) AS i(item, delta)
            WHERE i.delta < 0 AND inv.user_id = op.user_id AND inv.item_id = i.item
            RETURNING 1
        ), il AS (
            INSERT INTO inventory_ledger (user_id, item_id, delta, reason, match_id, purchase_id, operation_key, created_at)
            SELECT op.user_id, i.item, i.delta, $3::text, $4::uuid, $5::uuid, $2::text, $10::timestamptz
            FROM op CROSS JOIN unnest($8::text[], $9::integer[]) AS i(item, delta)
            RETURNING 1
        )
        SELECT (SELECT count(*)::integer FROM op) AS applied,
               -- Spending an item the player has no row for: abort everything.
               ludo_require(NOT EXISTS (SELECT 1 FROM op)
                            OR (SELECT count(*) FROM inv_use) = (SELECT count(*) FROM unnest($9::integer[]) AS d(delta) WHERE d.delta < 0),
                            'inventory change violates check constraint (not enough items)') AS ok
        """;
    public const string OperationExists = "SELECT EXISTS (SELECT 1 FROM economy_operations WHERE user_id = $1::uuid AND idempotency_key = $2::text)";
    public const string GetLedger = """
        SELECT asset, delta, reason, created_at, match_id, purchase_id FROM (
            SELECT currency AS asset, delta, reason, created_at, match_id, purchase_id, id AS seq FROM wallet_ledger WHERE user_id = $1::uuid
            UNION ALL
            SELECT item_id, delta::bigint, reason, created_at, match_id, purchase_id, id FROM inventory_ledger WHERE user_id = $1::uuid
        ) l
        ORDER BY created_at DESC, seq DESC
        LIMIT $2::integer
        """;

    // ---------- purchases ----------
    private const string PurchaseColumns = "id, user_id, store, product_id, transaction_id, original_transaction_id, environment, status, quantity, failure_reason, purchased_at, created_at, credited_at, refunded_at";
    public const string GetPurchase = $"SELECT {PurchaseColumns} FROM purchases WHERE store = $1::text AND transaction_id = $2::text";
    public const string InsertPurchase = $"""
        INSERT INTO purchases (id, user_id, store, product_id, transaction_id, original_transaction_id, environment, status, quantity, failure_reason, purchased_at, created_at)
        VALUES ($1::uuid, $2::uuid, $3::text, $4::text, $5::text, $6::text, $7::text, $8::text, $9::smallint, $10::text, $11::timestamptz, $12::timestamptz)
        ON CONFLICT (store, transaction_id) DO NOTHING
        RETURNING {PurchaseColumns}
        """;
    public const string UpdatePurchase = """
        UPDATE purchases SET status = $2::text, failure_reason = $3::text,
            credited_at = CASE WHEN $2::text = 'credited' THEN COALESCE(credited_at, $4::timestamptz) ELSE credited_at END,
            refunded_at = CASE WHEN $2::text = 'refunded' THEN $4::timestamptz ELSE refunded_at END
        WHERE id = $1::uuid
        """;
    public const string GetPurchasesForUser = $"SELECT {PurchaseColumns} FROM purchases WHERE user_id = $1::uuid ORDER BY created_at DESC LIMIT $2::integer";

    // ---------- league ----------
    private const string SeasonColumns = "id, number, starts_at, ends_at, status, finalized_at";
    public const string GetCurrentSeason = $"SELECT {SeasonColumns} FROM league_seasons WHERE status <> 'completed' ORDER BY number LIMIT 1";
    public const string GetLatestSeason = $"SELECT {SeasonColumns} FROM league_seasons ORDER BY number DESC LIMIT 1";
    public const string GetSeasonByNumber = $"SELECT {SeasonColumns} FROM league_seasons WHERE number = $1::integer";
    public const string InsertSeason = $"INSERT INTO league_seasons (number, starts_at, ends_at) VALUES ($1::integer, $2::timestamptz, $3::timestamptz) ON CONFLICT (number) DO NOTHING RETURNING {SeasonColumns}";
    public const string SetSeasonStatus = "UPDATE league_seasons SET status = $2::text, finalized_at = COALESCE($3::timestamptz, finalized_at) WHERE id = $1::integer";
    public const string GetDivision = "SELECT division FROM league_players WHERE user_id = $1::uuid";
    public const string EnsureLeaguePlayer = "INSERT INTO league_players (user_id) VALUES ($1::uuid) ON CONFLICT DO NOTHING";
    public const string UpsertLeagueDivision = "INSERT INTO league_players (user_id, division, updated_at) VALUES ($1::uuid, $2::smallint, now()) ON CONFLICT (user_id) DO UPDATE SET division = EXCLUDED.division, updated_at = now()";
    private const string StandingColumns = "season_id, user_id, group_id, division, points, matches_played, wins, last_points_at, final_rank, outcome";
    public const string GetStanding = $"SELECT {StandingColumns} FROM league_standings WHERE season_id = $1::integer AND user_id = $2::uuid";
    public const string GetSeasonStandings = $"SELECT {StandingColumns} FROM league_standings WHERE season_id = $1::integer";
    /// <summary>Takes a seat in the first open group of the division (row-locked, skipping groups other transactions are filling).</summary>
    public const string ClaimGroupSeat = """
        UPDATE league_groups SET member_count = member_count + 1
        WHERE id = (SELECT id FROM league_groups WHERE season_id = $1::integer AND division = $2::smallint AND member_count < $3::integer
                    ORDER BY id LIMIT 1 FOR UPDATE SKIP LOCKED)
        RETURNING id
        """;
    public const string InsertGroup = "INSERT INTO league_groups (season_id, division, member_count) VALUES ($1::integer, $2::smallint, 1) RETURNING id";
    public const string ReleaseGroupSeat = "UPDATE league_groups SET member_count = member_count - 1 WHERE id = $1::bigint AND member_count > 0";
    public const string InsertStanding = $"INSERT INTO league_standings (season_id, user_id, group_id, division) VALUES ($1::integer, $2::uuid, $3::bigint, $4::smallint) ON CONFLICT DO NOTHING RETURNING {StandingColumns}";
    public const string GetGroup = """
        SELECT s.user_id, p.friend_code, p.display_name, p.avatar_id, p.frame_id, s.points, s.matches_played, s.wins, s.last_points_at
        FROM league_standings s JOIN profiles p ON p.user_id = s.user_id
        WHERE s.group_id = $1::bigint
        ORDER BY s.points DESC, s.wins DESC, s.last_points_at ASC NULLS LAST, s.user_id
        LIMIT $2::integer
        """;
    public const string SetFinalStanding = "UPDATE league_standings SET final_rank = $3::smallint, outcome = $4::text WHERE season_id = $1::integer AND user_id = $2::uuid";
    public const string InsertReward = "INSERT INTO league_rewards (season_id, user_id, division, group_rank, coins, gems, shields) VALUES ($1::integer, $2::uuid, $3::smallint, $4::smallint, $5::integer, $6::integer, $7::integer) ON CONFLICT DO NOTHING";
    private const string RewardColumns = "season_id, user_id, division, group_rank, coins, gems, shields, granted_at";
    public const string GetUngrantedRewards = $"SELECT {RewardColumns} FROM league_rewards WHERE season_id = $1::integer AND granted_at IS NULL";
    public const string MarkRewardGranted = "UPDATE league_rewards SET granted_at = $3::timestamptz WHERE season_id = $1::integer AND user_id = $2::uuid AND granted_at IS NULL";
    public const string GetReward = $"SELECT {RewardColumns} FROM league_rewards WHERE season_id = $1::integer AND user_id = $2::uuid";
    /// <summary>Frees the deleted player's league group seats (their standings are removed by cascade).</summary>
    public const string ReleaseGroupSeatsOfUser = "UPDATE league_groups g SET member_count = g.member_count - 1 FROM league_standings s WHERE s.group_id = g.id AND s.user_id = $1::uuid AND g.member_count > 0";

    // ---------- migrations ----------
    public const string CreateMigrationsTable = "CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())";
    public const string GetAppliedMigrations = "SELECT version FROM schema_migrations";
    public const string RecordMigration = "INSERT INTO schema_migrations (version) VALUES ($1::text)";
    public const string MigrationLock = "SELECT pg_advisory_xact_lock(727274)";
}
