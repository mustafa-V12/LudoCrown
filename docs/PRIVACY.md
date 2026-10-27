# Privacy and user safety

> This is engineering documentation, **not a privacy policy and not legal advice**. You need to publish your own privacy policy, with your legal entity name, contact address and jurisdiction, at the URL set in `AppConfig.PrivacyPolicyUrl`. Have it reviewed for the regions you launch in (for example GDPR in the EU/Sweden, CCPA in California, and children's privacy rules if you target minors).

## Data the game collects (and why)

| Data | When | Purpose | Linked to user | Tracking |
|---|---|---|---|---|
| Account ID (random UUID) | Any online use | Run the account | Yes | No |
| Sign-in identifier from Apple, Google or Facebook (an opaque provider user ID) | If that sign-in is used | Signing in | Yes | No |
| Email address | Only with email sign-in | Signing in (sending codes) | Yes | No |
| Display name, avatar choice | Online use | Shown to other players | Yes | No |
| Gameplay data: matches, placements, stats, XP, coins | Online matches | Progression, history, fair play | Yes | No |
| Friends list, blocks | If used | Social features | Yes | No |
| Reports you file (reason + optional text ≤ 500 characters) | If used | Moderation | Yes | No |
| IP address | Every request (transient) | Rate limiting and security logs, kept by your hosting provider per their log retention | Not stored in the database | No |

**Not collected:** precise or coarse location, contacts, photos, microphone, advertising identifier, analytics or crash SDKs, purchases, payment data.

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
- **Data not linked to you:** none, unless you add crash reporting later.

These answers match `Platforms/iOS/PrivacyInfo.xcprivacy`. Update both if you add SDKs.

## Account deletion (App Store Review Guideline 5.1.1(v))
- **In the app:** Settings → Delete account. It asks twice and requires typing DELETE. Deletion runs immediately on the server (see [DATABASE.md](DATABASE.md#account-deletion-and-data-export)).
- **Sign in with Apple** tokens are revoked when Apple key configuration is present. Otherwise the app tells the user how to remove the app from their Apple ID settings.
- **Outside the app:** publish a support page or email address for deletion requests, at `AppConfig.SupportUrl` / your App Store listing.
- **Data download:** Settings → Download my data (JSON via the share sheet).

## Social safety controls
- **No free-text chat and no voice chat.** In-game reactions are 8 fixed presets, which can be turned off in Settings.
- **Display names:** 3–16 characters (letters, numbers, space, `.`, `-`, `_`), validated on both the server and the client. Avatars are chosen from 8 built-in images; there are no photo uploads.
- **Block:** a blocked player isn't matched with you, can't join your tables (they see a generic "not found"), can't send you a friend request, and their reactions are hidden from you. Blocking also removes the friendship.
- **Report:** from the friends list (offensive name, cheating, harassment, other). Reports are stored for review; see [DATABASE.md](DATABASE.md#moderation).
- **Friend requests** need the other player's private friend code. There is no public player search.

> **Not yet built:** reporting directly from an opponent's badge during a match. It is a recommended addition before launch; the server endpoint already exists.

## Age rating
- There is no user-generated free text, no gambling, and no purchases. Coins can't be bought or cashed out.
- Expect a low age rating in App Store Connect's questionnaire. Answer the "simulated gambling" question **No**: dice are a board-game mechanic, and there are no wagers or chance-based purchases.
