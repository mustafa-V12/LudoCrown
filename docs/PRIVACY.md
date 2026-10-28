# Privacy and user safety

> This is engineering documentation, **not a privacy policy and not legal advice**. You need to publish your own privacy policy, with your legal entity name, contact address and jurisdiction, at the URL set in `AppConfig.PrivacyPolicyUrl`. Have it reviewed for the regions you launch in (for example GDPR in the EU/Sweden, CCPA in California, and children's privacy rules if you target minors).

## Data the game collects (and why)

| Data | When | Purpose | Linked to user | Tracking |
|---|---|---|---|---|
| Account ID (random UUID) | Any online use | Run the account | Yes | No |
| Sign-in identifier from Apple, Google or Facebook (an opaque provider user ID) | If that sign-in is used | Signing in | Yes | No |
| Email address | Only with email sign-in | Signing in (sending codes) | Yes | No |
| Display name, avatar choice | Online use | Shown to other players | Yes | No |
| Gameplay data: matches, placements, stats (overall and per mode), XP, gold, gems, items, rerolls and boosts used, league standing | Online matches | Progression, history, fair play | Yes | No |
| Purchase records: store transaction ID, product, time, status. **No payment details:** Apple handles payment. | When buying | Crediting purchases once, refunds, support | Yes (kept with the account link removed after deletion, for accounting) | No |
| Privacy settings (show stats, show league, chat on/off) | If changed | Respecting your choices | Yes | No |
| Friends list, blocks | If used | Social features | Yes | No |
| Reports you file (reason + optional text ≤ 500 characters) | If used | Moderation | Yes | No |
| IP address | Every request (transient) | Rate limiting and security logs, kept by your hosting provider per their log retention | Not stored in the database | No |

**Not collected:** precise or coarse location, contacts, photos, microphone, advertising identifier, analytics or crash SDKs, payment card data. Quick-chat messages are not stored.

- Apple and Google sign-in do **not** request email (Apple's email scope is off; Google uses `openid profile`).
- Facebook requests `public_profile` only. The name from it is used only to suggest a display name.

## On the device
- **Keychain:** sign-in tokens.
- **Preferences:** settings and the offline saved game.

Nothing is shared with other apps.

## App Store Connect: App Privacy answers (suggested)
- **Data used to track you:** none.
- **Data linked to you:**
  - *Contact info → Email address* (only if email sign-in is offered) — App Functionality
  - *Identifiers → User ID* — App Functionality
  - *User content → Gameplay content* — App Functionality
  - *User content → Other user content* (display name, reports) — App Functionality
  - *Purchases → Purchase history* — App Functionality
- **Data not linked to you:** none, unless you add crash reporting later.

These answers match `Platforms/iOS/PrivacyInfo.xcprivacy`. Update both if you add SDKs.

## Account deletion (App Store Review Guideline 5.1.1(v))
- **In the app:** Settings → Delete account. It asks twice and requires typing DELETE. Deletion runs immediately on the server (see [DATABASE.md](DATABASE.md#account-deletion-and-data-export)).
- **Sign in with Apple** tokens are revoked when Apple key configuration is present. Otherwise the app tells the user how to remove the app from their Apple ID settings.
- **Outside the app:** publish a support page or email address for deletion requests, at `AppConfig.SupportUrl` / your App Store listing.
- **Data download:** Settings → Download my data (JSON via the share sheet).

## Social safety controls
- **No free-text chat and no voice chat.** Quick chat offers 12 fixed phrases and 8 emoji, sent by ID, so no user text can be sent. It is rate-limited on the server (at most 6 messages per 20 seconds). It can be turned off in Settings, which the server enforces, and a player can be muted for the match from their badge.
- **Display names:** 3–16 characters (letters, numbers, space, `.`, `-`, `_`), validated on both the server and the client. Avatars are chosen from 8 built-in images; there are no photo uploads.
- **Block:** a blocked player isn't matched with you, can't join your tables (they see a generic "not found"), can't send you a friend request, and their reactions are hidden from you. Blocking also removes the friendship.
- **Report:** from an opponent's public profile (opened from their badge on the board, the results screen, the friends list or the league table) or from the friends list. Reasons: offensive name, cheating, harassment, other. Reports are stored for review; see [DATABASE.md](DATABASE.md#moderation).
- **Public profiles** show only the display name, avatar, frame, level, public player code, statistics and league standing. Statistics and league standing can each be hidden in Settings → Profile privacy & chat. Email, sign-in providers and internal account IDs are never shown.
- **Friend requests** use the public player code, which is visible on profiles, at the table and in league tables. There is no free-text player search. Blocked players can't send requests.

## Age rating
- There is no user-generated free text and no gambling. **There are in-app purchases**: consumable gold, gem and Shield packs with fixed, listed contents. There are no loot boxes or random paid rewards. Nothing can be cashed out, and league rewards are in-game items only.
- Expect a low age rating in App Store Connect's questionnaire. Answer the "simulated gambling" question **No**: dice are a board-game mechanic, and there are no wagers or chance-based purchases.
