# Database (PostgreSQL)

The schema is in `db/migrations/0001_initial_schema.sql`, and roles are in `db/roles.sql`. All SQL the server runs is in `src/LudoCrown.Server.Postgres/Sql.cs`. You can check every statement against the schema with `tools/verify-sql.py`.

## Tables

| Table | Holds | Key constraints / indexes |
|---|---|---|
| `users` | Account ID, guest flag, timestamps | PK `id` |
| `auth_identities` | Linked sign-in methods: provider + provider subject ID, optional email. **No passwords.** | UNIQUE (`provider`, `subject`), index on `user_id`, provider CHECK |
| `profiles` | Display name, avatar ID, friend code, XP | Name length CHECK, avatar whitelist CHECK, UNIQUE `friend_code` |
| `player_stats` | Games, wins, captures, abandons, streaks | Non-negative CHECKs, wins ≤ games |
| `wallets` | Soft-currency balance (earned only) | `coins >= 0` |
| `wallet_ledger` | Every coin change, with reason and match | UNIQUE (`user_id`, `match_id`, `reason`): a match pays out once |
| `refresh_tokens` | SHA-256 **hashes** of refresh tokens, rotation chain | UNIQUE `token_hash`, partial index of active tokens per user |
| `auth_codes` | Short-lived single-use secrets (email codes, OAuth state, login codes, link tickets), stored as hashes | UNIQUE (`kind`, `code_hash`), index on expiry |
| `matches` | Mode, player count, status, room code, timestamps, winner, live/final state JSON, version | Status CHECK, `ended_at` consistent with status, partial index on active matches |
| `match_participants` | Seat, user (NULL for bots and deleted accounts), name, colour, placement, result, rewards | PK (`match_id`, `seat`), bots can't have a user, index on (`user_id`, `match_id`) |
| `friendships` | One row per pair (`user_low` < `user_high`), pending/accepted, requester | Pair-order CHECK |
| `blocks` | Blocker → blocked | No self-blocks |
| `reports` | Reporter, reported user, match, reason, details (≤ 500 characters), status | Reason CHECK, partial index on open reports |
| `schema_migrations` | Applied migration versions | Created by the migration runner |

## Access rules

**Who can reach the database at all:**
- The database is reachable **only by the server**. The app never connects to it.
- `ludocrown_app` (the running server) has SELECT/INSERT/UPDATE/DELETE only. It cannot change the schema.
- `ludocrown_migrator` owns the schema and is used only for migrations.

**What the server lets each user do, per data type:**

| Data | Read | Write |
|---|---|---|
| Account, identities, tokens | Owner only (`/me`, export) | Server auth flows only |
| Profile | Owner; others see public fields (name, avatar, level, games, wins) unless either has blocked the other | Owner can change name (validated) and avatar (whitelist) |
| Stats, XP, coins, ledger | Owner (`/me`, export) | **Server only**, when a match completes |
| Matches and participants | Participants (own history) | **Server only** |
| Friendships | The two users | Request / accept / remove, by either user |
| Blocks | Blocker | Blocker |
| Reports | Operators (SQL) | Any signed-in user can file one (reason whitelist, length limit) |

## Migrations
- Migrations are versioned files `db/migrations/NNNN_description.sql`, applied in name order. Each runs in a transaction under an advisory lock, and the version is recorded in `schema_migrations`.
- **Automatically:** the server applies them at startup when `Database__MigrateOnStartup=true` (the default in Development).
- **Manually / CI:** `psql "$MIGRATOR_URL" -v ON_ERROR_STOP=1 -f db/migrations/<file>.sql`
- **New change:** add `0002_….sql`. Never edit an applied migration. Prefer additive changes, such as new nullable columns, then backfill.

## Retention
- Expired auth codes are purged daily, and expired or revoked refresh tokens after 7 days (`PostgresMaintenanceService`).
- Finished matches are deleted after `Database:MatchRetentionDays` (default 730 days). Statistics remain.
- Reports: keep while under review, then delete or anonymise according to your policy.

## Account deletion and data export
- **In-app deletion** (`DELETE /me`) runs one transaction that:
  1. renames the user's match seats to "Deleted player",
  2. detaches their reports,
  3. deletes the `users` row. This cascades to identities, profile, stats, wallet, ledger, tokens, auth codes, friendships and blocks. `match_participants.user_id` becomes NULL through `ON DELETE SET NULL`.
- **Sign in with Apple:** the app also obtains a fresh authorization code so the server can revoke the Apple token. This needs `Auth:Apple` key configuration.
- **Export** (`GET /me/export`) returns a JSON file with the account, identities, profile, stats, coins, match history, friends and blocked list.
- Requests made outside the app (for example by email to support) are handled with the same steps by an operator.

## What is stored where, and why

| Location | Data | Why |
|---|---|---|
| iPhone Keychain | Access + refresh tokens | Stay signed in. The Keychain is encrypted and not readable by other apps. |
| iPhone Preferences | Settings (sound, haptics, motion, reactions, auto-move); offline saved game; last offline player name | Device-local preferences; resuming offline games without an account |
| Server database | Accounts, linked identities, profiles, stats, match records, friends, blocks, reports, reward ledger | Shared across devices; must be trustworthy and authoritative |
| Server memory | Live matches, matchmaking queues, private-room lobbies | Low-latency play. Live match state is also saved to the database after every move. |

## Moderation
Useful queries:

```sql
-- Open reports, newest first
SELECT r.created_at, r.reason, r.details, p.display_name AS reported, r.reported_id
FROM reports r LEFT JOIN profiles p ON p.user_id = r.reported_id
WHERE r.status = 'open' ORDER BY r.created_at DESC;

-- Rename an offensive display name
UPDATE profiles SET display_name = 'Player' || floor(random()*9000+1000)::int WHERE user_id = '...';

-- Close a report
UPDATE reports SET status = 'actioned' WHERE id = '...';
```
