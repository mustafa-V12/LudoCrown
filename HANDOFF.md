# Ludo Crown: handoff summary

## Status per area

| Area | State | How it was verified |
|---|---|---|
| Rules engine (`LudoCrown.Core`): Classic and Quick modes, rerolls, pass, Shield | **Implemented** | Unit tests: legal and illegal moves in both modes; win conditions (Quick ends at the first crowned piece, Classic needs 4); reroll limits 7/5; reroll before and after moving and out of turn; three-6s rule through rerolls; held roll and pass; Shield protection and expiry; save/load including old saves; tampered state rejected |
| Computer opponents | **Implemented** | Tests: always legal; prefer captures and finishing; full bot games in both modes |
| Authoritative server: modes, rerolls, boosts, chat | **Implemented** | Server tests: gems spent once per request ID (also after a restart and under concurrency); insufficient gems; all rejection paths charge nothing; mode fixed for the match and kept across restart and rematch; separate queues per mode and ranked/casual; Shield accepted once in casual, rejected in ranked, visible to all, survives disconnects, ends on schedule; chat rate limits, blocks, chat off |
| Store, currencies, inventory, purchases | **Implemented; Apple not connected** | Tests: success, pending, failed, duplicate, other-account claim, refund clawback; App Store JWS verification with a generated certificate chain (wrong chain, tampered payload, wrong bundle, revoked, wrong account token all rejected); gold purchases idempotent; concurrent spending. **No real App Store purchase has been made** (see docs/STORE.md). |
| Public profiles, per-mode stats, privacy | **Implemented** | Tests: only public fields returned; hidden stats and league not sent; blocked players can't view; stats from server results only; abandon and streak rules |
| League: seasons, divisions, rewards | **Implemented** (local and dev) | Tests: points table; ranking ties; promotion needs 5 matches; demotion only in groups of 15+; season finalization; rewards paid once even when finalization runs twice; casual, private and bot-only matches give no points |
| PostgreSQL: migrations 0001–0003 + down scripts | **Implemented** | Applied to PostgreSQL 16: all up, 0003 and 0002 down, all up again; all 99 SQL statements validated with PREPARE; 24 integration tests against the real database, including the full purchase flow with constraints |
| Full online flow | **Working** (Development server + PostgreSQL) | `tools/smoke/smoke.mjs`, 30 checks passed: sandbox purchases (success, duplicate, other account, pending→credited, failure); gold buy retry; ranked Classic match with 3 rerolls including a retried request (gem arithmetic checked against the ledger); boost rejected in ranked; held-roll passes; casual Quick match with a Shield seen by the opponent; quick chat delivered; per-mode stats; profile privacy; league points; season end with a single reward |
| App logic (view models, sessions) | **Implemented** | 54 app tests: mode picker, reroll/pass/boost states and texts, store states (pending, cancel, failure, sandbox, recovery), league and profile screens, environment labels |
| Windows app (`LudoCrown.Mobile`, MAUI, net10.0-windows) | **Builds and runs on your PC** | Built with .NET SDK 10.0.403 + MAUI workload on your Windows PC (one compile error fixed). Tested by hand against the local development server:<br>- guest sign-in, and a graceful return to sign-in after a server restart<br>- Classic/Quick picker with rules; ranked/casual<br>- offline game<br>- online Quick match with bots<br>- held roll → reroll ×2 (gems 30 → 28, rerolls 2/5) → pass<br>- Shield (visible on the badge, 3 → 2 bottles)<br>- quick chat panel<br>- results screen (removed for inactivity)<br>- dev test currency<br>- store sandbox purchase (+50 gems)<br>- league screen; settings with privacy and chat switches<br>Screens: `docs/windows-test.jpg` |
| Web version (`web/`, TypeScript, GitHub Pages) | **Implemented** | Offline vs computer and pass-and-play, Classic/Quick, 2–4 players, save/resume, offline after first visit. 30 tests: the rules engine replays 18 games (8,079 steps) recorded from the C# engine and matches every state, event, legal move and bot score; full games through the controller. Played through to the results screen in an iPhone-sized browser. No online features. |
| iPhone app (net10.0-ios, StoreKit) | **Written, NOT compiled** | Needs a Mac with Xcode (Pair to Mac) and the iOS workload. StoreKit purchases need Apple sandbox testing on a device. |

**Tests:**
- **On your Windows PC**, with the real xUnit and NuGet packages: Core 128, Server 134, App 57, PostgreSQL 24, all passing. The PostgreSQL tests return early there, because no database is set up on the PC. Server and app tests were rerun after the final fixes.
- **In the build environment**, against a real PostgreSQL 16, through `tools/LudoCrown.TestShim`: 343 passing. This includes the database tests and the end-to-end smoke test.

## Decisions you made during development
- Gems, bought or earned, can be used for rerolls in **all** matches, ranked league matches included: 7 per Classic match, 5 per Quick match.
- Boost bottles: only in the new **casual** queue and in private tables.

## What still needs you
1. **Mac / iOS:** simulator and device testing, signing, archive, TestFlight.
2. **Apple:**
   - Developer Program membership and a bundle ID with Sign in with Apple and In-App Purchase
   - the Paid Applications agreement
   - the 6 products in App Store Connect
   - an In-App Purchase key
   - the server notification URL
   - sandbox testers
3. **Hosting** for the server, plus a managed PostgreSQL and a domain with HTTPS.
4. **Provider credentials** (Google, Facebook, SMTP), if you want those sign-in methods.
5. **Your information:** privacy policy URL, support URL and email, App Store listing text and screenshots.

Placeholders to replace:

| Placeholder | Where |
|---|---|
| `com.example.ludocrown` | `LudoCrown.Mobile.csproj` |
| `PLACEHOLDER.BUNDLE.ID` (App Store product ID prefix) | `Store:ProductIdPrefix` server setting |
| `https://YOUR-SERVER-HOST` | `AppConfig` in `src/LudoCrown.App.Logic/Services/PlatformServices.cs` |
| `https://YOUR-WEBSITE/...` | Same file (privacy policy and support URLs) |

**The app is not release-ready** until all of the following are true:
- purchases have been tested in Apple's sandbox,
- server verification works with your keys,
- privacy and store information is complete,
- the app has been tested on an iPhone.

## Exact next steps (Windows)
The .NET 10 SDK and the MAUI Windows workload are now installed on your PC. Visual Studio isn't required.

1. In File Explorer, open `Documents\LudoCrown\tools\windows`.
2. Double-click **`run-dev.cmd`**. It builds the app, starts the local development server and opens Ludo Crown.
   - Try *Play as guest*, then Settings → Development → *Add test currency*.
   - Choose Quick or Classic and Ranked or Casual, then *2 players*. Bots join after 10 seconds.
3. Double-click **`build-and-test.cmd`** to run all tests and build everything again. The result is in `build-log.txt`.
4. Optional: install Visual Studio 2026 Community with the **.NET Multi-platform App UI** workload to get a debugger and Test Explorer.
5. For PostgreSQL locally, see README → *Use PostgreSQL locally*. Migrations run automatically in Development.
6. Optional end-to-end check: `cd tools\smoke`, then `npm install`, then `node smoke.mjs http://localhost:5080`.

## Where to read more
- `README.md`: setup, run, test, build.
- `docs/RULES.md`: modes, rerolls, Shield, chat, rewards.
- `docs/STORE.md`: economy, purchases, App Store setup.
- `docs/LEAGUE.md`: seasons, points, rewards, statistics rules.
- `docs/ARCHITECTURE.md`, `docs/BACKEND.md`, `docs/DATABASE.md`, `docs/AUTH_SETUP.md`, `docs/PRIVACY.md`.
- `docs/RELEASE_CHECKLIST.md`, `docs/DECISIONS.md` (including the Firebase evaluation: not added).

## Known gaps worth doing before launch
- **VoiceOver:** choosing pieces with VoiceOver. The board is one drawn surface; add accessible piece buttons.
- **Scaling:** multi-instance server scaling (sticky sessions, a SignalR backplane, Redis queues).
- **Sound on Android/Windows:** sounds play on iOS only.
- **Moderation:** no moderation dashboard; reports are reviewed with SQL.
