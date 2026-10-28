# App Store release checklist

Items marked **[you]** need your accounts, information or hardware. None of these values were invented in the code; placeholders are clearly marked.

## 1. Identity and versioning
- [ ] **[you]** Choose the final app name and check that it's available in App Store Connect ("Ludo Crown" may be taken; have alternatives ready).
- [ ] **[you]** Register a bundle identifier (e.g. `com.<yourcompany>.ludocrown`). Replace `com.example.ludocrown` in `src/LudoCrown.Mobile/LudoCrown.Mobile.csproj` (`ApplicationId`).
- [ ] Set `ApplicationDisplayVersion` (1.0.0) and increase `ApplicationVersion` (the build number) for every upload.
- [ ] App icon (`Resources/AppIcon`) and launch screen (`Resources/Splash`) are original artwork. Check how they look on device at all sizes.

## 2. Apple Developer and signing
- [ ] **[you]** Enrol in the Apple Developer Program (individual or organisation; an organisation needs a D-U-N-S number).
- [ ] **[you]** Create the App ID with the **Sign in with Apple** and **In-App Purchase** capabilities.
- [ ] **[you]** Create an **Apple Distribution** certificate and an **App Store** provisioning profile. Automatic signing from Visual Studio or Xcode on the paired Mac is easiest.
- [ ] **[you]** In App Store Connect, create the app record: name, primary language, bundle ID, SKU.
- [ ] Build with `-p:EnableAppleSignIn=true` so the entitlement is included.

## 3. Production backend
- [ ] **[you]** Hosting account, PostgreSQL, a domain with HTTPS. Follow [BACKEND.md](BACKEND.md#deployment-steps).
- [ ] Generate a production `Jwt__SigningKey` and store it in the secret store.
- [ ] Database roles (`db/roles.sql`) and migrations applied. Backups and point-in-time recovery enabled. One restore drill done.
- [ ] Set `AppConfig.ProductionServerUrl` (`src/LudoCrown.App.Logic/Services/PlatformServices.cs`) to your HTTPS API URL.
- [ ] Health checks and alerts configured; incident contacts written down.
- [ ] Smoke test passes against staging: `node tools/smoke/smoke.mjs https://staging…`

## 3b. In-app purchases ([STORE.md](STORE.md))
- [ ] **[you]** Sign the Paid Applications agreement; banking and tax forms complete.
- [ ] **[you]** Create the 6 consumable products in App Store Connect: `<prefix>.gems_small`, `gems_medium`, `gems_large`, `coins_small`, `coins_large`, `shield_pack`. You choose the price tiers; no prices are set in code.
- [ ] Set `Store__ProductIdPrefix` (replacing the placeholder `PLACEHOLDER.BUNDLE.ID`) and all `Store__Apple__…` settings on the server. `/info` must show `appStoreVerification: true`.
- [ ] **[you]** Create the In-App Purchase key and download Apple Root CA G3. Store the key in the secret store only.
- [ ] **[you]** Set the App Store Server Notifications V2 URL (production and sandbox) to `/store/apple/notifications`.
- [ ] On production, `/info` shows `devSandboxStore: false` and `devGemGrant: false`.
- [ ] **[you, iPhone]** Sandbox tests pass on a real device, each checked against the server ledger (`/me/ledger`):
  - [ ] success
  - [ ] cancel
  - [ ] Ask to Buy (deferred)
  - [ ] app killed before verification, then recovered on reopening the store
  - [ ] network off during verification
  - [ ] refund
  - [ ] duplicate claim from another account rejected
  - [ ] localized prices shown
- [ ] App Review notes say what each pack contains, that rewards are not random, and how to reach the store.

## 4. Sign-in providers ([AUTH_SETUP.md](AUTH_SETUP.md))
- [ ] **[you]** Apple: key (`.p8`), Team ID, Key ID on the server, for token revocation.
- [ ] **[you]** Google OAuth client (optional).
- [ ] **[you]** Facebook app, live (optional).
- [ ] **[you]** SMTP provider with a verified domain (optional).
- [ ] If Google or Facebook is enabled, Sign in with Apple must be enabled too.

## 5. Privacy, legal and support
- [ ] **[you]** Publish a privacy policy and a support page. Set `AppConfig.PrivacyPolicyUrl` and `AppConfig.SupportUrl`.
- [ ] **[you]** Support contact email for App Store Connect.
- [ ] Fill in App Privacy answers per [PRIVACY.md](PRIVACY.md), consistent with `PrivacyInfo.xcprivacy`.
- [ ] Account deletion works end to end on a production build, including Apple revocation.
- [ ] Age rating questionnaire. Answer per [PRIVACY.md](PRIVACY.md#age-rating): in-app purchases yes; no gambling or loot boxes; no free-text chat.
- [ ] App Privacy now includes **Purchases → Purchase history** (linked, App Functionality), as in PRIVACY.md.
- [ ] Export compliance: `ITSAppUsesNonExemptEncryption=false` is set (standard HTTPS only). Confirm in App Store Connect.

## 6. Listing assets
- [ ] **[you]** Screenshots for 6.9" and 6.5" iPhones (required sizes change; check App Store Connect), taken from real builds.
- [ ] **[you]** Description, keywords, promotional text, category (Games → Board), support URL, marketing URL (optional), copyright.
- [ ] Optional app preview video.

## 7. Testing
- [ ] All automated tests pass in Visual Studio Test Explorer or CI.
- [ ] **[you, Mac/iPhone]** First iOS build on the paired Mac; fix any compile warnings or errors.
- [ ] Test on at least: the smallest supported iPhone (iPhone SE size), a standard iPhone, a Pro Max, and the newest iOS plus your minimum (iOS 15).
- [ ] Check these flows:
  - [ ] guest → link account
  - [ ] each enabled provider
  - [ ] matchmaking 2P and 4P, in Classic and Quick, ranked and casual
  - [ ] rerolls: gem cost, limits (7 Classic, 5 Quick), the held-roll window, offline explanation
  - [ ] Shield bottle in casual matches (visible to the opponent); rejected in ranked
  - [ ] quick chat: send, mute, chat turned off, blocked player
  - [ ] store: gold purchases; frames equipped
  - [ ] public profile from board, results, friends and league; privacy switches
  - [ ] league screen: clock, group table, reward table
  - [ ] bot fill
  - [ ] private table invite via link
  - [ ] rematch
  - [ ] offline vs bots at each difficulty
  - [ ] pass-and-play
  - [ ] resume after force-quit
  - [ ] airplane mode during a match (reconnect)
  - [ ] leaving a match
  - [ ] block and report
  - [ ] delete account
  - [ ] data export
- [ ] VoiceOver pass:
  - [ ] buttons are labelled
  - [ ] turn announcements are spoken
  - [ ] the die is reachable
  - [ ] **known gap:** choosing pieces with VoiceOver. The board is a single drawn surface, so add accessible piece buttons before launch if accessibility is a priority.
- [ ] Dynamic Type at large sizes; Reduce Motion setting; dark backgrounds readable in sunlight.

## 8. TestFlight and review
- [ ] Archive: `dotnet publish src/LudoCrown.Mobile -f net10.0-ios -c Release -p:ArchiveOnBuild=true -p:EnableAppleSignIn=true`, on the Mac or in macOS CI.
- [ ] Upload with Transporter or Xcode Organizer. Resolve any processing emails (missing privacy keys, icons).
- [ ] Internal TestFlight, then external TestFlight (needs a short beta review).
- [ ] App Review notes:
  - how to test online play: "tap Play as guest, then Play online; bots join after 30 seconds". League points need two humans; the reviewer may only see a 0-point standing.
  - that purchases are consumable packs with fixed contents, and that the reviewer can test them in the sandbox
  - where account deletion is
- [ ] Submit. Approval is not guaranteed; respond to reviewer questions in App Store Connect.

## 9. After launch
- [ ] Watch logs and alerts; review reports weekly.
- [ ] Keep `FacebookGraphVersion`, NuGet packages and .NET servicing updates current.
- [ ] Plan the multi-instance scaling work before heavy marketing ([BACKEND.md](BACKEND.md#scaling)).
