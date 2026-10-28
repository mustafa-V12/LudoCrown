# Decisions made where requirements were open

| Topic | Decision | Why |
|---|---|---|
| App stack | .NET MAUI (C#), targeting .NET 10 | You asked for MAUI and Visual Studio. .NET 10 is the current long-term-support release. |
| Extra app targets | Android and Windows targets kept next to iOS | Lets you run and test from Visual Studio on Windows without a Mac. iOS is still the release target. |
| Backend | ASP.NET Core + SignalR, a single process | Same language and the **same rules engine** as the app; WebSockets with automatic reconnect; no extra packages. |
| Database | PostgreSQL via Npgsql with plain SQL + versioned migration files | Widely hosted, strong constraints, transparent SQL that is validated against the schema. |
| Shared rules | `LudoCrown.Core` has no dependencies | Offline play and the server use identical rules; fully unit-testable. |
| App logic split | `LudoCrown.App.Logic` (plain .NET) + `LudoCrown.Mobile` (MAUI views) | View models, sessions and auth are testable without a phone or MAUI. |
| Ruleset | "Crown Classic" (see RULES.md): enter on 6; extra roll on 6, capture or crowning; three 6s forfeit; exact finish; star and start squares safe; no blockades; capture all opponents on a square | A common, easy-to-learn Ludo variant. Most rules can be toggled via `RuleSet`. |
| No-move after a 6 | The turn passes | Simple and predictable; documented. |
| Online player counts | 2 and 4 (the engine supports 3 for future use) | As requested; keeps matchmaking queues full. |
| Turn timer / disconnect | 20 s turn, 6 s while disconnected, 90 s grace, 3 missed turns = left; a bot takes over leavers | Keeps matches moving for the remaining players and discourages leaving (no rewards). |
| Empty seats | Bots after 30 s in matchmaking; always available in private 4P tables | Players are never stuck waiting while the player base is small. Bots are clearly labelled. |
| Email sign-in | Passwordless 6-digit codes | No passwords stored anywhere; simpler for players. |
| Google/Facebook | Server-side OAuth with PKCE, single-use hand-off code to the app | No client secrets in the app; tokens never travel in URLs; works with MAUI WebAuthenticator. |
| Tokens | 15-min HS256 access tokens, 60-day rotating refresh tokens stored hashed; reuse of an old token revokes all sessions | Standard, revocable, detects theft. |
| Chat | Preset phrases (12) and emoji (8), by ID; no free text, no voice | No moderation burden or App Store risk from user-generated text, yet players can communicate. Rate limits, blocks, mute and an off switch still apply. |
| Avatars | 8 built-in original images | No photo uploads to moderate. |
| Currencies | Gold (earned and buyable) and gems (earned and buyable), both server-owned with a full ledger | Gold buys Shield bottles and cosmetic frames. Gems pay for rerolls. No random paid rewards. See STORE.md. |
| Private table rewards | XP only (half), no coins | Prevents farming with friends. |
| Visual identity | "Royal board-game night": midnight velvet, gold, ivory, jewel-tone colours named Ruby / Emerald / Topaz / Sapphire; crown motifs; custom-drawn board and gem pieces | Original look, not derived from Ludo Star's assets or screens. |
| Fonts | System font (San Francisco on iOS) | Best legibility and Dynamic Type support; no font licensing. |
| Sounds | Synthesized in code (`Resources/Raw/*.wav`) | Original, licence-free. iOS playback via AVAudioPlayer (Ambient category respects the silent switch). |
| Orientation | Portrait only on iPhone | The board fills the width; simpler layout. |
| Minimum iOS | 15.0 | Covers nearly all active iPhones; supported by MAUI on .NET 10. |
| Bundle ID, URLs, server URL | Clear placeholders (`com.example.ludocrown`, `YOUR-SERVER-HOST`, `YOUR-WEBSITE`) | Real values must come from you. |

## Phase 2: game modes, rerolls, store, league

| Topic | Decision | Why |
|---|---|---|
| Keep MAUI | Kept .NET MAUI; no move to Unity | No concrete reason to migrate. The rules engine, server, tests and app logic all share C#. A 2D board game doesn't need a game engine. |
| Modes | Classic (4 pieces) and Quick (1 piece) in a registry with room for 5. Unbuilt modes have no buttons. | As specified. The mode is part of `RuleSet`, so offline play and the server enforce the same win condition. |
| Reroll value | A reroll **replaces** the pending roll with a new server roll. A new 6 counts as if it were the original roll. | Simple to explain. It can't be used to dodge the three-6s rule or to farm extra turns. |
| Roll with no moves | Held for a 5-second decision window if the player can afford a reroll. Otherwise it passes immediately. Auto-pass is not a missed turn. | Without this, a player could never reroll a roll that has no legal move. The short window keeps matches flowing. |
| Gems in ranked league matches | **Allowed**: purchased and earned gems, 7 per Classic match / 5 per Quick match | Your explicit decision during development. The spec says ranked matches must not allow paid advantages "unless a different fair league rule is agreed and documented". This is that documented rule. Boosts remain banned in ranked. |
| Boost effect | **Shield bottle**: none of your pieces can be captured until your next turn starts. 1 per match. | Easy to explain and test. It doesn't touch dice or turn order, and it can't guarantee a win. Visible to everyone. |
| Where boosts are allowed | A new **Casual** matchmaking queue, plus private tables. Ranked never. | Your choice. It keeps the league free of purchasable boosts. |
| iOS purchases | StoreKit 1 (`SKPaymentQueue`) in the app, App Store Server API on the server | StoreKit 2 is Swift-only and not callable from .NET. The App Store Server API verifies StoreKit 1 transaction IDs and returns Apple-signed data. |
| Purchase ownership | `appAccountToken` = a one-way hash of the account ID, checked by the server | A leaked transaction ID can't be credited to another account, and Apple never receives the internal ID. |
| Public player ID | The 8-character friend code doubles as the public player ID (profiles, league table, seats) | Internal account IDs are never shown to other players. |
| League | Six original divisions (Squire to Monarch), 14-day seasons, groups of 30, fixed reward table | See LEAGUE.md. Small groups don't demote, and promotion needs 5 matches, so the system can't be farmed. |
| Firebase | **Not added** | PostgreSQL and the authoritative server already handle auth, data and real-time play. Adding Firebase (Auth/Firestore) would create a second source of truth for accounts, balances and results, which the spec forbids. If you later want push notifications or crash reporting, **Firebase Cloud Messaging** or **Crashlytics** can be added as *outbound-only* services. They would hold no game data; the server stays the only writer of trusted state. Either would need a Google account, `GoogleService-Info.plist`, an APNs key and updated privacy answers. |
| Migrations | Forward migrations plus `db/migrations/down/*.down.sql` scripts | The spec requires reversible migrations. Down scripts are tested: down, then up again, on a real database. |
