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
| iPhone/Windows app (`LudoCrown.Mobile`, MAUI) | **Written, NOT compiled** | Pages and bindings checked against the view models by script; XAML and the iOS privacy manifest parse. The MAUI build needs Visual Studio with the MAUI workload. iOS and StoreKit also need a Mac. |

**Test totals in the build environment: 343 passed, 0 failed** (Core 88, Server 121, App 54, PostgreSQL 24 test methods, plus theory cases). They ran through `tools/LudoCrown.TestShim` because nuget.org was unreachable. In Visual Studio, use Test Explorer.

## Decisions you made during development
- Gems, bought or earned, can be used for rerolls in **all** matches, ranked league matches included: 7 per Classic match, 5 per Quick match.
- Boost bottles: only in the new **casual** queue and in private tables.

## What still needs you
1. **Build the app in Visual Studio on Windows.** It has not been compiled. Small compile errors in `LudoCrown.Mobile` are likely; send me the error list.
2. **Mac / iOS:** simulator and device testing, signing, archive, TestFlight.
3. **Apple:**
   - Developer Program membership and a bundle ID with Sign in with Apple and In-App Purchase
   - the Paid Applications agreement
   - the 6 products in App Store Connect
   - an In-App Purchase key
   - the server notification URL
   - sandbox testers
4. **Hosting** for the server, plus a managed PostgreSQL and a domain with HTTPS.
5. **Provider credentials** (Google, Facebook, SMTP), if you want those sign-in methods.
6. **Your information:** privacy policy URL, support URL and email, App Store listing text and screenshots.

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
1. Open `Documents\LudoCrown\LudoCrown.sln` in Visual Studio 2022/2026. It needs the **.NET Multi-platform App UI** and **ASP.NET and web development** workloads, plus the .NET 10 SDK.
2. **Build → Rebuild Solution**, then **Test → Run All Tests**.
3. Start **LudoCrown.Server** (F5) and check <http://localhost:5080/info>. It should show `environment: Development`.
   - For PostgreSQL, see README → *Use PostgreSQL locally*. Migrations run automatically in Development.
4. Run **LudoCrown.Mobile** on **Windows Machine** and try:
   1. *Play offline*: Classic and Quick against bots.
   2. *Play as guest* → Settings → Development → *Add test gold, gems & Shields*.
   3. Home → choose a mode and Ranked → *Play online*. Bots join after 10 seconds. Roll, then try a reroll.
   4. Casual → drink a Shield before rolling.
   5. Store: buy with gold, and use the sandbox for gem packs (success, pending, failure).
   6. The League screen, and tap a player for their profile.
5. Optional end-to-end check: `cd tools\smoke`, then `npm install`, then `node smoke.mjs http://localhost:5080`.

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
