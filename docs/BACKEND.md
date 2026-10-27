# Backend: hosting, deployment and operations

## What the server does
- Accounts and sign-in: guest, Apple, Google, Facebook, email codes. It issues short-lived access tokens and rotating refresh tokens.
- Profiles, statistics, match history, friends, blocks, reports, account deletion and data export.
- Matchmaking, private tables, and **authoritative live matches** over SignalR (WebSockets).
- Persists everything to PostgreSQL. Live match state is saved after every move for crash recovery.

## API

**Auth** (rate limited, 30/min per IP):

| Method | Path | Purpose |
|---|---|---|
| GET | `/auth/providers` | Which sign-in methods are enabled |
| POST | `/auth/guest` | `{displayName?}` → tokens |
| POST | `/auth/refresh` | `{refreshToken}` → rotated tokens |
| POST | `/auth/logout` | `{refreshToken}` |
| POST | `/auth/apple` | `{identityToken, fullName?, link}` |
| POST | `/auth/email/start` | `{email}` |
| POST | `/auth/email/verify` | `{email, code, link}` |
| GET | `/auth/oauth/{google\|facebook}/start` | Optional `?link_ticket=` |
| GET | `/auth/oauth/{provider}/callback` | Redirects to `ludocrown://auth?code=…` |
| POST | `/auth/exchange` | `{code}` → tokens |
| POST | `/auth/link-ticket` | Authenticated; returns a one-time ticket for linking via the browser |

**Account and social** (authenticated, 120/min per user):

| Method | Path | Purpose |
|---|---|---|
| GET | `/me` | Your profile, stats and coins |
| PATCH | `/me/profile` | `{displayName?, avatarId?}` |
| GET | `/me/matches?limit=` | Match history |
| GET | `/me/export` | Data download |
| DELETE | `/me` | `{appleAuthorizationCode?}` |
| GET | `/friends` | Friends and requests |
| POST | `/friends/requests` | `{friendCode}` |
| POST | `/friends/{id}/accept` | Accept a request |
| DELETE | `/friends/{id}` | Remove a friend |
| GET / POST / DELETE | `/blocks[/{id}]` | List, block, unblock |
| POST | `/reports` | `{userId, matchId?, reason, details?}` |
| GET | `/players/{id}` | Public profile (hidden if either player blocked the other) |

**Operations:**

| Path | Purpose |
|---|---|
| `/health` | Includes a database check when PostgreSQL is configured |
| `/stats/live` | Number of live matches and queue size |

**Hub** `/hubs/match` (access token in the `access_token` query string):
- **Client → server:** `FindMatch`, `CancelMatchmaking`, `CreatePrivateRoom`, `JoinPrivateRoom`, `StartPrivateRoom`, `LeaveRoom`, `Roll`, `Move`, `Resync`, `SendReaction`, `RequestRematch`
- **Server → client:** `MatchState`, `RoomUpdated`, `MatchmakingStatus`, `Reaction`, `MatchEnded`

Names are defined once in `Core/Protocol/Protocol.cs` (`HubNames`).

## Recommended hosting
Any host that runs a .NET 10 container (or .NET directly) **with WebSockets** and a managed PostgreSQL works. Three straightforward options:

| Option | App | Database | Notes |
|---|---|---|---|
| **Azure** | App Service (Linux, container or code) with *Web sockets: On* and *ARR affinity: On* | Azure Database for PostgreSQL – Flexible Server | Key Vault references for secrets; Application Insights for monitoring |
| **Fly.io / Render / Railway** | Container from `deploy/Dockerfile` | Their managed Postgres or Neon/Supabase | Simple, cheap to start |
| **AWS** | App Runner / ECS Fargate behind an ALB (sticky sessions on) | RDS for PostgreSQL | Secrets Manager |

All of these need **your** account, a payment method, and a domain with HTTPS.

## Deployment steps
1. **Create the database** (PostgreSQL 15 or newer). Note the admin connection string.
2. **Create roles:**

   ```bash
   psql "$ADMIN_URL" -v migrator_pw='…' -v app_pw='…' -f db/roles.sql
   ```

3. **Apply migrations** as the migrator role, in order:

   ```bash
   psql "$MIGRATOR_URL" -v ON_ERROR_STOP=1 -f db/migrations/0001_initial_schema.sql
   ```

   Or set `Database__MigrateOnStartup=true` for the first deploy. Either way, migrations are recorded in `schema_migrations`.
4. **Build and push the image:**

   ```bash
   docker build -f deploy/Dockerfile -t <registry>/ludocrown-server:1.0.0 .
   docker push <registry>/ludocrown-server:1.0.0
   ```

5. **Configure environment variables / secrets** (see `deploy/.env.example`). At minimum:
   - `ConnectionStrings__Postgres` (as `ludocrown_app`, with `SSL Mode=Require`)
   - `Jwt__SigningKey`
   - `Auth__PublicBaseUrl`
   - the variables for each sign-in provider you want to enable
6. **Expose HTTPS** on your domain (e.g. `api.your-domain.com`), with WebSockets enabled.
7. **Check that it works:**
   - `GET /health` → `Healthy`
   - `GET /auth/providers` shows the expected providers
   - run `node tools/smoke/smoke.mjs https://api.your-domain.com`. This creates two guest accounts and plays a match. Delete them afterwards, or run it against staging only.
8. **Point the app at the server:** set `AppConfig.ProductionServerUrl` and rebuild the app.

## Scaling
- **One instance** comfortably handles thousands of concurrent matches. Live rooms are held in memory, and each move costs about one small UPDATE.
- **Several instances** need two things:
  1. sticky sessions, so a player stays on the instance that holds their match,
  2. a SignalR backplane (Azure SignalR Service or Redis), so `Clients.User(...)` reaches every device.

  Matchmaking queues and private-room lobbies would also have to move to Redis, and you'd need a "match owner" routing rule. This is not implemented. It is the main backend work needed before large-scale growth.

## Monitoring
- Use `/health` for liveness/readiness probes.
- Structured logs (`ILogger`) cover match start/end, abandonments, failed result writes and auth provider status at startup. Ship them to your provider's log service (Application Insights, CloudWatch, Grafana Loki).
- Set alerts on:
  - `/health` failing,
  - the error-log rate,
  - database CPU and connections,
  - `Failed to record result` log entries (result-write failures).

## Backups and restore
- Use the managed provider's automated daily backups with **point-in-time recovery**, kept for at least 7 days (30 recommended).
- Weekly logical backup to separate storage: `pg_dump --format=custom "$URL" > ludocrown-$(date +%F).dump`
- **Restore drill** (do this before launch, then quarterly):
  1. `pg_restore --clean --if-exists -d "$STAGING_URL" ludocrown-YYYY-MM-DD.dump`
  2. Point a staging server at it.
  3. Run the smoke test.
- **After a restore:** live matches that were in progress resume from their last saved move. Matches that ended after the backup point are lost.

## Incident handling (short runbook)
1. **Check health:** `/health`, recent deploys, database status page.
2. **Server crash loop:**
   - check the startup log for configuration errors (missing `Jwt__SigningKey` or connection string),
   - roll back to the previous image.
3. **Database down:**
   - the server keeps running live matches in memory, but new sign-ins and results fail,
   - restore database service; failed result writes are logged with the match ID.
4. **Suspected token leak:**
   - rotate `Jwt__SigningKey`; all access tokens become invalid, and users refresh or sign in again,
   - revoke refresh tokens: `UPDATE refresh_tokens SET revoked_at = now() WHERE revoked_at IS NULL;`
5. **Abuse:**
   - query open reports (see [DATABASE.md](DATABASE.md#moderation)),
   - block abusive names by changing `display_name`, or delete the account via SQL using the same steps as the in-app deletion.
6. Write a short postmortem.
