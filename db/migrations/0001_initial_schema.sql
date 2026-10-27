-- Ludo Crown — initial schema (PostgreSQL 15+).
-- Applied by the server's migration runner (LudoCrown.Server.Postgres) inside one transaction,
-- or manually: psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f db/migrations/0001_initial_schema.sql
--
-- Design rules:
--   * No passwords are stored anywhere. Sign-in uses provider subject ids (Apple/Google/Facebook),
--     passwordless email codes (stored only as keyed hashes) and server-issued tokens (stored as hashes).
--   * Only the game server connects to this database. Clients never write to it directly.
--   * Competitive data (results, stats, XP, coins) is written only by the server when a match ends.

CREATE TABLE users (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    is_guest        boolean     NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now(),
    last_seen_at    timestamptz
);

CREATE TABLE auth_identities (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id     uuid        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    provider    text        NOT NULL CHECK (provider IN ('guest', 'apple', 'google', 'facebook', 'email')),
    subject     text        NOT NULL CHECK (length(subject) BETWEEN 1 AND 320),
    email       text        CHECK (email IS NULL OR (email = lower(email) AND length(email) <= 254)),
    created_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_identity UNIQUE (provider, subject)
);
CREATE INDEX ix_identities_user ON auth_identities (user_id);

CREATE TABLE profiles (
    user_id       uuid PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    display_name  text        NOT NULL CHECK (length(display_name) BETWEEN 3 AND 16),
    avatar_id     text        NOT NULL CHECK (avatar_id IN ('crown', 'knight', 'rook', 'jester', 'lion', 'swan', 'star', 'gem')),
    friend_code   char(8)     NOT NULL UNIQUE,
    xp            integer     NOT NULL DEFAULT 0 CHECK (xp >= 0),
    updated_at    timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE player_stats (
    user_id            uuid PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    games_played       integer NOT NULL DEFAULT 0 CHECK (games_played >= 0),
    games_won          integer NOT NULL DEFAULT 0 CHECK (games_won >= 0),
    two_player_wins    integer NOT NULL DEFAULT 0 CHECK (two_player_wins >= 0),
    four_player_wins   integer NOT NULL DEFAULT 0 CHECK (four_player_wins >= 0),
    pieces_captured    integer NOT NULL DEFAULT 0 CHECK (pieces_captured >= 0),
    abandons           integer NOT NULL DEFAULT 0 CHECK (abandons >= 0),
    current_win_streak integer NOT NULL DEFAULT 0 CHECK (current_win_streak >= 0),
    best_win_streak    integer NOT NULL DEFAULT 0 CHECK (best_win_streak >= 0),
    CONSTRAINT ck_wins_le_played CHECK (games_won <= games_played)
);

-- Soft currency earned only through play (no purchases). Every change is recorded in the ledger.
CREATE TABLE wallets (
    user_id  uuid PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    coins    bigint NOT NULL DEFAULT 0 CHECK (coins >= 0)
);

CREATE TABLE refresh_tokens (
    id          uuid PRIMARY KEY,
    user_id     uuid        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash  bytea       NOT NULL UNIQUE CHECK (length(token_hash) = 32),
    created_at  timestamptz NOT NULL,
    expires_at  timestamptz NOT NULL,
    revoked_at  timestamptz,
    replaced_by uuid
);
CREATE INDEX ix_refresh_tokens_user ON refresh_tokens (user_id) WHERE revoked_at IS NULL;

-- Short-lived single-use secrets: email codes, OAuth state, login hand-off codes, link tickets.
CREATE TABLE auth_codes (
    id          uuid PRIMARY KEY,
    kind        text        NOT NULL CHECK (kind IN ('email_otp', 'oauth_state', 'login_code', 'link_ticket')),
    code_hash   bytea       NOT NULL,
    user_id     uuid        REFERENCES users(id) ON DELETE CASCADE,
    payload     text,
    expires_at  timestamptz NOT NULL,
    consumed_at timestamptz,
    attempts    integer     NOT NULL DEFAULT 0,
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_auth_codes_hash ON auth_codes (kind, code_hash);
CREATE INDEX ix_auth_codes_payload ON auth_codes (kind, payload) WHERE consumed_at IS NULL;
CREATE INDEX ix_auth_codes_expiry ON auth_codes (expires_at);

CREATE TABLE matches (
    id             uuid PRIMARY KEY,
    mode           text        NOT NULL CHECK (mode IN ('matchmaking', 'private')),
    player_count   smallint    NOT NULL CHECK (player_count IN (2, 3, 4)),
    status         text        NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'completed', 'abandoned')),
    room_code      char(6),
    started_at     timestamptz NOT NULL,
    ended_at       timestamptz,
    winner_user_id uuid        REFERENCES users(id) ON DELETE SET NULL,
    -- Authoritative live state (JSON) for crash recovery, and the final state once ended.
    state_json     jsonb       NOT NULL,
    state_version  bigint      NOT NULL DEFAULT 0,
    CONSTRAINT ck_ended CHECK ((status = 'active') = (ended_at IS NULL))
);
CREATE INDEX ix_matches_active ON matches (started_at) WHERE status = 'active';
CREATE INDEX ix_matches_ended ON matches (ended_at) WHERE status <> 'active';

CREATE TABLE match_participants (
    match_id        uuid     NOT NULL REFERENCES matches(id) ON DELETE CASCADE,
    seat            smallint NOT NULL CHECK (seat BETWEEN 0 AND 3),
    user_id         uuid     REFERENCES users(id) ON DELETE SET NULL,  -- NULL for bots and deleted accounts
    is_bot          boolean  NOT NULL,
    display_name    text     NOT NULL,
    color           text     NOT NULL CHECK (color IN ('Ruby', 'Emerald', 'Topaz', 'Sapphire')),
    placement       smallint CHECK (placement BETWEEN 1 AND 4),
    result          text     CHECK (result IN ('win', 'loss', 'abandoned')),
    coins_awarded   integer  NOT NULL DEFAULT 0 CHECK (coins_awarded >= 0),
    xp_awarded      integer  NOT NULL DEFAULT 0 CHECK (xp_awarded >= 0),
    pieces_captured integer  NOT NULL DEFAULT 0 CHECK (pieces_captured >= 0),
    PRIMARY KEY (match_id, seat),
    CONSTRAINT ck_bot_has_no_user CHECK (NOT is_bot OR user_id IS NULL)
);
CREATE INDEX ix_participants_user ON match_participants (user_id, match_id) WHERE user_id IS NOT NULL;

CREATE TABLE wallet_ledger (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id    uuid        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    delta      bigint      NOT NULL,
    reason     text        NOT NULL CHECK (reason IN ('match_reward', 'admin_adjustment')),
    match_id   uuid        REFERENCES matches(id) ON DELETE SET NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_ledger_match_reward UNIQUE (user_id, match_id, reason)  -- a match pays out once
);

-- Friendships are stored once per pair with user_low < user_high.
CREATE TABLE friendships (
    user_low     uuid        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    user_high    uuid        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    status       text        NOT NULL CHECK (status IN ('pending', 'accepted')),
    requested_by uuid        NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_low, user_high),
    CONSTRAINT ck_pair_order CHECK (user_low < user_high),
    CONSTRAINT ck_requester CHECK (requested_by IN (user_low, user_high))
);
CREATE INDEX ix_friendships_high ON friendships (user_high);

CREATE TABLE blocks (
    blocker_id uuid        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    blocked_id uuid        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (blocker_id, blocked_id),
    CONSTRAINT ck_not_self CHECK (blocker_id <> blocked_id)
);
CREATE INDEX ix_blocks_blocked ON blocks (blocked_id);

CREATE TABLE reports (
    id          uuid PRIMARY KEY,
    reporter_id uuid        REFERENCES users(id) ON DELETE SET NULL,
    reported_id uuid        REFERENCES users(id) ON DELETE SET NULL,
    match_id    uuid        REFERENCES matches(id) ON DELETE SET NULL,
    reason      text        NOT NULL CHECK (reason IN ('offensive_name', 'cheating', 'harassment', 'other')),
    details     text        CHECK (details IS NULL OR length(details) <= 500),
    status      text        NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'actioned', 'dismissed')),
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_reports_open ON reports (created_at) WHERE status = 'open';
