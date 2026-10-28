# Ludo Crown — handoff summary

## Status per area

| Area | State | How it was verified |
|---|---|---|
| Ludo rules engine (`LudoCrown.Core`) | **Implemented** | Unit tests: entering, moves, home column, exact finish, captures (also multi-piece), safe squares, extra turns, three-sixes forfeit, turn order, winning, invalid actions, immutability, 30 full random games |
| Computer opponents (Easy/Normal/Hard) | **Implemented** | Tests: always legal moves, prefers capturing/finishing, escapes threats; Hard beats Easy in most of 60 seeded games |
| Authoritative game server | **Implemented** | 50+ server tests (wrong-turn/stale/illegal commands rejected, server-only dice, timeouts, disconnect grace, abandonment → bot, recovery after restart, rewards paid once, reactions rate-limited and block-aware) |
| Matchmaking, private tables, rematch | **Implemented** | Tests (2P/4P queues, block filtering, bot fill, cancel, codes, full/unknown rooms, host handover, rematch) |
| Auth: guest, email codes, tokens | **Implemented** | Tests (token tampering/`alg:none`/expiry rejected, refresh rotation + reuse detection, email code limits/expiry) |
| Auth: Apple, Google, Facebook | **Implemented, pending configuration** | Apple/Google ID-token verification tested with generated RSA keys. The real providers need your accounts (docs/AUTH_SETUP.md) |
| PostgreSQL schema, roles, migrations | **Implemented** | Migration and roles script applied to a real PostgreSQL 16; all 57 server SQL statements validated with PREPARE |
| PostgreSQL store (`PostgresStore`) | **Implemented** | 8 integration tests against real PostgreSQL. Run through a psql-backed stand-in for Npgsql, because nuget.org was blocked in the build environment. Not yet run with the real Npgsql package |
| Full online game | **Working** | End-to-end: real server + PostgreSQL, two scripted clients over SignalR: guest sign-in → matchmaking → complete game (~320 server-validated actions) → results, rewards, history → account deletion |
| App logic (view models, sessions, API client) | **Implemented** | 29 tests (offline games incl. resume after interruption, online session versioning/resync, token refresh, sign-in flows, screen flows) |
| iPhone app (`LudoCrown.Mobile`, MAUI) | **Written, NOT compiled** | The board's drawing code was rendered to an image (docs/board-preview.png) and the XAML checked for well-formedness. No MAUI/iOS build has been run: that needs Visual Studio with the MAUI workload, and a Mac for iOS |

**Test totals in the build environment: 186 passed, 0 failed.**

These ran through `tools/LudoCrown.TestShim`, an xUnit-compatible runner, because NuGet was unreachable. In Visual Studio, use Test Explorer with the real xUnit packages.

## What has not been done (needs you)
1. **Build the app in Visual Studio** (the next step below). The first build may show small compile errors in `LudoCrown.Mobile`, because it could not be compiled here. They should be minor: API names, XAML bindings.
2. **Mac / iOS:** iOS Simulator and device testing, signing, archive, TestFlight upload. This needs a Mac with Xcode (Pair to Mac) or a cloud macOS build.
3. **Apple Developer Program** account, bundle ID, Sign in with Apple capability and key.
4. **Hosting** for the server, plus a managed PostgreSQL and a domain with HTTPS.
5. **Provider credentials** (Google, Facebook, SMTP), if you want those sign-in methods.
6. **Your information:** privacy policy URL, support URL/email, App Store listing text and screenshots.

Placeholders to replace:

| Placeholder | Where |
|---|---|
| `com.example.ludocrown` | `LudoCrown.Mobile.csproj` |
| `https://YOUR-SERVER-HOST` | `AppConfig` in `src/LudoCrown.App.Logic/Services/PlatformServices.cs` |
| `https://YOUR-WEBSITE/...` | Same file (privacy policy and support URLs) |

## Exact next steps
1. Open `Documents\LudoCrown\LudoCrown.sln` in Visual Studio.
2. In **Visual Studio Installer**, make sure the workloads **.NET Multi-platform App UI development** and **ASP.NET and web development** are installed, plus the .NET 10 SDK.
3. **Build → Rebuild Solution.** NuGet restores packages automatically. If `LudoCrown.Mobile` shows errors, copy the error list to me and I'll fix them.
4. **Test → Run All Tests.** The PostgreSQL tests are skipped unless `LUDOCROWN_TEST_POSTGRES` is set.
5. Set **LudoCrown.Server** as the startup project and press F5. Check <http://localhost:5080/health>.
6. In a second Visual Studio instance (or with multiple startup projects), run **LudoCrown.Mobile** on **Windows Machine**:
   1. try *Play offline*,
   2. then *Play as guest* → *Play online*. Bots join after 10 seconds.
7. When you have a Mac (or a cloud Mac), run on the iOS Simulator, then work through `docs/RELEASE_CHECKLIST.md`.

## Where to read more
`README.md` (setup, run, test, build) · `docs/ARCHITECTURE.md` · `docs/RULES.md` · `docs/BACKEND.md` · `docs/DATABASE.md` · `docs/AUTH_SETUP.md` · `docs/PRIVACY.md` · `docs/RELEASE_CHECKLIST.md` · `docs/DECISIONS.md`

## Known gaps worth doing before launch
- **VoiceOver:** choosing pieces with VoiceOver. The board is one drawn surface; add accessible piece buttons.
- **Reporting from the game screen:** report an opponent directly from their badge. The server side already exists.
- **Scaling:** multi-instance server scaling (sticky sessions + SignalR backplane + Redis queues).
- **Sound on Android/Windows:** sounds currently play on iOS only.
