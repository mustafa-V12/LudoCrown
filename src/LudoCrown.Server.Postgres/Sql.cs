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
    private const string ProfileColumns = "user_id, display_name, avatar_id, friend_code, xp, updated_at";
    public const string GetProfile = $"SELECT {ProfileColumns} FROM profiles WHERE user_id = $1::uuid";
    public const string GetProfiles = $"SELECT {ProfileColumns} FROM profiles WHERE user_id = ANY($1::uuid[])";
    public const string GetProfileByFriendCode = $"SELECT {ProfileColumns} FROM profiles WHERE friend_code = $1::text";
    public const string UpdateProfile = $"""
        UPDATE profiles SET display_name = COALESCE($2::text, display_name), avatar_id = COALESCE($3::text, avatar_id), updated_at = $4::timestamptz
        WHERE user_id = $1::uuid RETURNING {ProfileColumns}
        """;
    public const string GetStats = "SELECT user_id, games_played, games_won, two_player_wins, four_player_wins, pieces_captured, abandons, current_win_streak, best_win_streak FROM player_stats WHERE user_id = $1::uuid";
    public const string GetCoins = "SELECT coins FROM wallets WHERE user_id = $1::uuid";

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
            rerolls_used = $8::smallint, gems_awarded = $9::integer
        WHERE match_id = $1::uuid AND seat = $2::smallint
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
               m.game_mode, me.rerolls_used, me.gems_awarded
        FROM match_participants me
        JOIN matches m ON m.id = me.match_id
        WHERE me.user_id = $1::uuid AND m.status <> 'active' AND me.result IS NOT NULL
        ORDER BY m.ended_at DESC
        LIMIT $2::integer
        """;
    public const string PurgeOldMatches = "DELETE FROM matches WHERE status <> 'active' AND ended_at < $1::timestamptz";

    // ---------- social ----------
    public const string GetFriends = """
        SELECT CASE WHEN f.user_low = $1::uuid THEN f.user_high ELSE f.user_low END AS other_id,
               p.display_name, p.avatar_id, p.xp, f.status, f.requested_by
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

    // ---------- migrations ----------
    public const string CreateMigrationsTable = "CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())";
    public const string GetAppliedMigrations = "SELECT version FROM schema_migrations";
    public const string RecordMigration = "INSERT INTO schema_migrations (version) VALUES ($1::text)";
    public const string MigrationLock = "SELECT pg_advisory_xact_lock(727274)";
}
