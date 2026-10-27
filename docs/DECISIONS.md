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
| Chat | Preset reactions only | Avoids moderation burden and App Store risk of user-generated text. |
| Avatars | 8 built-in original images | No photo uploads to moderate. |
| Currency | Coins earned only, nothing to buy or spend yet | Progression feel without purchases or gambling concerns. Spending (cosmetics) can be added later. |
| Private table rewards | XP only (half), no coins | Prevents farming with friends. |
| Visual identity | "Royal board-game night": midnight velvet, gold, ivory, jewel-tone colours named Ruby / Emerald / Topaz / Sapphire; crown motifs; custom-drawn board and gem pieces | Original look, not derived from Ludo Star's assets or screens. |
| Fonts | System font (San Francisco on iOS) | Best legibility and Dynamic Type support; no font licensing. |
| Sounds | Synthesized in code (`Resources/Raw/*.wav`) | Original, licence-free. iOS playback via AVAudioPlayer (Ambient category respects the silent switch). |
| Orientation | Portrait only on iPhone | The board fills the width; simpler layout. |
| Minimum iOS | 15.0 | Covers nearly all active iPhones; supported by MAUI on .NET 10. |
| Bundle ID, URLs, server URL | Clear placeholders (`com.example.ludocrown`, `YOUR-SERVER-HOST`, `YOUR-WEBSITE`) | Real values must come from you. |
