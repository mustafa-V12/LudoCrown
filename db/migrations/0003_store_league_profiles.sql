-- Ludo Crown — store, purchases, inventory, boosts, quick chat settings, public profiles, per-mode stats and leagues.
-- Reverse with db/migrations/down/0003_store_league_profiles.down.sql (see docs/DATABASE.md).
--
-- Rules carried over: only the game server writes here; every balance change is an idempotent operation with ledger rows;
-- balances can never go negative (CHECK constraints abort the whole statement).

-- ---------- matches: casual queue, boosts and league points ----------
ALTER TABLE matches DROP CONSTRAINT matches_mode_check;
ALTER TABLE matches ADD CONSTRAINT matches_mode_check CHECK (mode IN ('matchmaking', 'private', 'casual'));
ALTER TABLE match_participants ADD COLUMN boosts_used smallint NOT NULL DEFAULT 0 CONSTRAINT ck_participants_boosts CHECK (boosts_used BETWEEN 0 AND 3);
-- NULL = the match did not count for the league.
ALTER TABLE match_participants ADD COLUMN league_points smallint CONSTRAINT ck_participants_league_points CHECK (league_points BETWEEN -100 AND 100);

-- ---------- profiles ----------
ALTER TABLE profiles ADD COLUMN frame_id text CONSTRAINT ck_profiles_frame CHECK (frame_id IS NULL OR frame_id IN ('frame_gilded', 'frame_emerald', 'frame_royal'));

CREATE TABLE profile_settings (
    user_id      uuid PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    show_stats   boolean     NOT NULL DEFAULT true,
    show_league  boolean     NOT NULL DEFAULT true,
    chat_enabled boolean     NOT NULL DEFAULT true,
    updated_at   timestamptz NOT NULL DEFAULT now()
);

-- Trusted per-mode aggregates, written only when the server records a match result.
CREATE TABLE player_mode_stats (
    user_id            uuid    NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    game_mode          text    NOT NULL CHECK (game_mode IN ('classic', 'quick')),
    games_played       integer NOT NULL DEFAULT 0 CHECK (games_played >= 0),
    games_won          integer NOT NULL DEFAULT 0 CHECK (games_won >= 0),
    games_lost         integer NOT NULL DEFAULT 0 CHECK (games_lost >= 0),
    abandons           integer NOT NULL DEFAULT 0 CHECK (abandons >= 0),
    current_win_streak integer NOT NULL DEFAULT 0 CHECK (current_win_streak >= 0),
    best_win_streak    integer NOT NULL DEFAULT 0 CHECK (best_win_streak >= 0),
    PRIMARY KEY (user_id, game_mode),
    CONSTRAINT ck_mode_stats_sum CHECK (games_won + games_lost + abandons <= games_played)
);

-- ---------- purchases ----------
CREATE TABLE purchases (
    id                      uuid PRIMARY KEY,
    user_id                 uuid        REFERENCES users(id) ON DELETE SET NULL, -- kept for accounting/refunds after account deletion
    store                   text        NOT NULL CHECK (store IN ('app_store', 'dev_sandbox')),
    product_id              text        NOT NULL CHECK (product_id ~ '^[a-z0-9_]{1,40}$'),
    transaction_id          text        NOT NULL CHECK (length(transaction_id) BETWEEN 1 AND 80),
    original_transaction_id text        CHECK (original_transaction_id IS NULL OR length(original_transaction_id) <= 80),
    environment             text        NOT NULL CHECK (environment IN ('production', 'sandbox', 'development')),
    status                  text        NOT NULL CHECK (status IN ('pending', 'verified', 'credited', 'rejected', 'refunded')),
    quantity                smallint    NOT NULL DEFAULT 1 CHECK (quantity BETWEEN 1 AND 10),
    failure_reason          text        CHECK (failure_reason IS NULL OR length(failure_reason) <= 300),
    purchased_at            timestamptz,
    created_at              timestamptz NOT NULL DEFAULT now(),
    credited_at             timestamptz,
    refunded_at             timestamptz,
    CONSTRAINT uq_purchase_transaction UNIQUE (store, transaction_id),  -- one store transaction is credited once, to one account
    CONSTRAINT ck_purchase_credited CHECK (status <> 'credited' OR credited_at IS NOT NULL),
    CONSTRAINT ck_dev_sandbox_env CHECK ((store = 'dev_sandbox') = (environment = 'development'))
);
CREATE INDEX ix_purchases_user ON purchases (user_id, created_at) WHERE user_id IS NOT NULL;

-- ---------- economy operations & ledgers ----------
-- One row per applied operation: the idempotency record. A retried operation finds its key and applies nothing.
CREATE TABLE economy_operations (
    user_id         uuid        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    idempotency_key text        NOT NULL CHECK (length(idempotency_key) BETWEEN 1 AND 200),
    reason          text        NOT NULL,
    match_id        uuid        REFERENCES matches(id) ON DELETE SET NULL,
    purchase_id     uuid        REFERENCES purchases(id) ON DELETE SET NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, idempotency_key)
);

ALTER TABLE wallet_ledger DROP CONSTRAINT wallet_ledger_reason_check;
ALTER TABLE wallet_ledger ADD CONSTRAINT wallet_ledger_reason_check CHECK (reason IN
    ('match_reward', 'admin_adjustment', 'reroll', 'starter_grant', 'dev_grant', 'purchase', 'store_buy', 'boost_use', 'league_reward', 'refund'));
ALTER TABLE wallet_ledger ADD COLUMN purchase_id uuid REFERENCES purchases(id) ON DELETE SET NULL;
ALTER TABLE wallet_ledger ADD COLUMN operation_key text;

-- Inventory: Shield bottles stack (max 999); cosmetic frames are owned at most once.
CREATE TABLE inventory (
    user_id    uuid        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    item_id    text        NOT NULL CHECK (item_id IN ('boost_shield', 'frame_gilded', 'frame_emerald', 'frame_royal')),
    quantity   integer     NOT NULL CHECK (quantity >= 0 AND quantity <= 999),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, item_id),
    CONSTRAINT ck_inventory_frame_once CHECK (item_id NOT LIKE 'frame\_%' OR quantity <= 1)
);

-- Raises a check_violation (aborting the whole statement) when a condition is false. Used by the single-statement
-- economy operation to refuse spending an item the player does not have.
CREATE FUNCTION ludo_require(ok boolean, message text) RETURNS boolean LANGUAGE plpgsql AS $$
BEGIN
    IF NOT ok THEN
        RAISE EXCEPTION '%', message USING ERRCODE = 'check_violation';
    END IF;
    RETURN true;
END
$$;

CREATE TABLE inventory_ledger (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id       uuid        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    item_id       text        NOT NULL,
    delta         integer     NOT NULL CHECK (delta <> 0),
    reason        text        NOT NULL CHECK (reason IN ('admin_adjustment', 'dev_grant', 'purchase', 'store_buy', 'boost_use', 'league_reward', 'refund')),
    match_id      uuid        REFERENCES matches(id) ON DELETE SET NULL,
    purchase_id   uuid        REFERENCES purchases(id) ON DELETE SET NULL,
    operation_key text,
    created_at    timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_inventory_ledger_user ON inventory_ledger (user_id, created_at);

-- ---------- league ----------
CREATE TABLE league_seasons (
    id           integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    number       integer     NOT NULL UNIQUE CHECK (number > 0),
    starts_at    timestamptz NOT NULL,
    ends_at      timestamptz NOT NULL,
    status       text        NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'finalizing', 'completed')),
    finalized_at timestamptz,
    CONSTRAINT ck_season_dates CHECK (ends_at > starts_at)
);
-- At most one season is open at a time.
CREATE UNIQUE INDEX ux_one_open_season ON league_seasons ((true)) WHERE status <> 'completed';

-- Division carried between seasons (0 Squire … 5 Monarch).
CREATE TABLE league_players (
    user_id    uuid PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    division   smallint    NOT NULL DEFAULT 0 CHECK (division BETWEEN 0 AND 5),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE league_groups (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    season_id    integer     NOT NULL REFERENCES league_seasons(id) ON DELETE CASCADE,
    division     smallint    NOT NULL CHECK (division BETWEEN 0 AND 5),
    member_count integer     NOT NULL DEFAULT 0 CHECK (member_count BETWEEN 0 AND 30),
    created_at   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_league_groups_open ON league_groups (season_id, division, id) WHERE member_count < 30;

CREATE TABLE league_standings (
    season_id      integer     NOT NULL REFERENCES league_seasons(id) ON DELETE CASCADE,
    user_id        uuid        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    group_id       bigint      NOT NULL REFERENCES league_groups(id) ON DELETE CASCADE,
    division       smallint    NOT NULL CHECK (division BETWEEN 0 AND 5),
    points         integer     NOT NULL DEFAULT 0 CHECK (points >= 0),
    matches_played integer     NOT NULL DEFAULT 0 CHECK (matches_played >= 0),
    wins           integer     NOT NULL DEFAULT 0 CHECK (wins >= 0),
    last_points_at timestamptz,
    final_rank     smallint    CHECK (final_rank IS NULL OR final_rank BETWEEN 1 AND 30),
    outcome        text        CHECK (outcome IS NULL OR outcome IN ('promoted', 'stayed', 'demoted')),
    PRIMARY KEY (season_id, user_id),
    CONSTRAINT ck_standing_wins CHECK (wins <= matches_played)
);
CREATE INDEX ix_league_standings_group ON league_standings (group_id, points DESC, wins DESC, last_points_at);

-- One reward row per player per season; granted_at is set when the economy operation succeeded.
CREATE TABLE league_rewards (
    season_id  integer     NOT NULL REFERENCES league_seasons(id) ON DELETE CASCADE,
    user_id    uuid        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    division   smallint    NOT NULL CHECK (division BETWEEN 0 AND 5),
    group_rank smallint    NOT NULL CHECK (group_rank BETWEEN 1 AND 30),
    coins      integer     NOT NULL CHECK (coins >= 0),
    gems       integer     NOT NULL CHECK (gems >= 0),
    shields    integer     NOT NULL CHECK (shields >= 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    granted_at timestamptz,
    PRIMARY KEY (season_id, user_id)
);
CREATE INDEX ix_league_rewards_ungranted ON league_rewards (season_id) WHERE granted_at IS NULL;
