# Architecture

```
 ┌──────────────── iPhone app (LudoCrown.Mobile, .NET MAUI) ────────────────┐
 │ XAML screens ─ BoardView/DiceView (Microsoft.Maui.Graphics)              │
 │        │ bindings                                                        │
 │ LudoCrown.App.Logic: ViewModels ─ IGameSession ─ SessionService ─ ApiClient
 │        │                 │ offline              │ HTTPS + tokens         │
 │        │          OfflineGameSession ──► LudoCrown.Core (rules, AI)      │
 │        │          OnlineGameSession ──► IMatchConnection (SignalR) ──────┼──┐
 │ Platform services: Keychain, Preferences, WebAuthenticator, Apple sign-in,│  │
 │ haptics, AVAudioPlayer, share sheet                                      │  │
 └──────────────────────────────────────────────────────────────────────────┘  │
                                                                               ▼
 ┌──────────────── LudoCrown.Server (ASP.NET Core) ─────────────────────────────┐
 │ REST: /auth/*, /me, /friends, /blocks, /reports     SignalR hub: /hubs/match │
 │ TokenService · AccountService · OAuthService · AppleSignInService · Email    │
 │ MatchService (live rooms, timers, bots) · MatchmakingService · PrivateRooms  │
 │        │ uses LudoCrown.Core rules engine (same code as the app)            │
 │ IAccountStore / IProfileStore / IMatchStore / ISocialStore                   │
 └────────────┬─────────────────────────────────────────────────────────────────┘
              ▼
   PostgresStore (Npgsql) ──► PostgreSQL       (InMemoryStore in Development)
```

## Module boundaries

| Concern | Where |
|---|---|
| App startup, navigation, DI | `Mobile/MauiProgram.cs`, `App.xaml.cs`, `AppShell.xaml(.cs)`, `Services/ShellNavigationService` |
| Screens and reusable visuals | `Mobile/Views/*`, `Mobile/Controls/*`, `Resources/Styles/*` |
| Domain models and Ludo rules | `Core/Model`, `Core/Board`, `Core/Engine` |
| Game state and turn management | `Core/Engine/GameEngine` (pure), `App.Logic/Game/*Session`, `Server/Matches/MatchService` |
| Offline opponents | `Core/AI/Bots.cs` (Easy/Normal/Hard + weight profiles) |
| Authentication | `App.Logic/Auth/SessionService`, `Server/Auth/*` |
| Online communication | `Core/Protocol` (shared contracts), `Mobile/Services/SignalRMatchConnection`, `Server/Hubs/MatchHub` |
| Backend services and database access | `Server/*`, `Server.Data` (interfaces), `Server.Postgres` |
| Persistence and settings (device) | `ISecureStore` (Keychain), `ISettingsStore` (Preferences + offline save) |
| Platform-specific iOS | `Mobile/Platforms/iOS/*` |
| Tests and development tools | `tests/*`, `tools/*` |

External services always sit behind interfaces: `IAuthBrowser`, `IAppleSignIn`, `IMatchConnection`, `IEmailSender`, `IJwksSource`, `IOAuthProvider` and the store interfaces. Tests replace them with fakes.

## Single source of truth
- **Offline:** `OfflineGameSession` owns one `GameState`. Every action goes through `GameEngine`, which returns a *new* state with `Version + 1`. The board screen draws `BoardState` snapshots built from that state.
- **Online:** the server's `MatchRoom.State` is the only truth. The app shows the latest `MatchSnapshot` it received and never edits it.
- **Animations** in `BoardView` read the state but never change it. If a new state arrives mid-animation, the board snaps to it.

## Online play: how cheating is prevented
1. **Intents only.** The hub methods are `Roll(matchId, version)` and `Move(matchId, piece, version)`. No method accepts a die value, a position or a result.
2. **Server dice.** `CryptoDiceRoller` (`RandomNumberGenerator`) runs on the server.
3. **Full validation** before any change: authenticated user → seat in this match → not abandoned → `expectedVersion == state.Version` (rejects stale or replayed commands) → turn and phase → piece index → legality (`GameEngine`).
4. **One command at a time** per match (`SemaphoreSlim`), so there are no races between players or timers.
5. **Results and rewards** are computed in `MatchService.CompleteAsync` and written in one transaction by `IMatchStore.CompleteMatchAsync`. The transaction is idempotent: the match row is locked and its status checked, and a unique ledger key prevents paying a match out twice. Clients have no API that writes stats, XP or coins.
6. **Rate limits:** a per-connection token bucket on the hub (10 calls/s, burst 20), fixed-window limits on REST (30/min per IP on `/auth`, 120/min per user elsewhere), and reactions limited to one per 2 seconds.
7. **Reactions are preset IDs**, validated by the server. There is no free text to abuse.

## Synchronisation and reconnection
- After every change the server pushes each player a personal snapshot: the full state, their seat, their legal pieces, the turn deadline, and the events that caused the change.
- Clients ignore snapshots older than the one they're showing.
- SignalR reconnects automatically with back-off (0, 1, 3, 5, 10, 20, 30 s). After reconnecting, and whenever the app returns to the foreground, the app calls `Resync(matchId)` and redraws from the server's answer.
- If a command is rejected as `StaleVersion`, the server immediately sends the current snapshot.
- **Server restart:** active matches (state JSON + participants) are saved on every change and reloaded at startup (`MatchService.RecoverAsync`). Players start as disconnected and rejoin automatically.

## Timeout and abandonment policy
See [RULES.md](RULES.md#online-only-rules-server). Implemented in `MatchService.TickAsync` / `AutoActAsync` and driven by `GameLoopService` every 250 ms.

## Matchmaking
- One queue for each player count, first come first served (`MatchmakingService`).
- Players who blocked each other are never grouped.
- After `MatchmakingBotFillSeconds` (30 s in production, 10 s in Development), empty seats are filled with bots. Bots are clearly labelled in the UI.

## Room lifecycle (private tables)
1. **Create:** the host gets a code.
2. **Join:** with the code. Blocked players get a generic "not found".
3. The host can leave, which hands host to the next player. An empty room closes.
4. **Start** needs at least 2 humans; empty seats become bots.
5. The match runs. A **rematch** reopens a table with the same people.
6. Idle rooms expire after 30 minutes.
