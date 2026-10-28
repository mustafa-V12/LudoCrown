# Store, currencies, inventory and in-app purchases

> **Status:** the server, database and app flow are implemented and tested, including a development sandbox. **Real App Store purchases have not been tested.** That needs an Apple Developer account, products in App Store Connect, a Mac build and Apple's sandbox. Product IDs and prices below are placeholders until you create the products.

## Currencies and items

All balances live only on the server, in PostgreSQL. The app sends *intents* (a product ID, a request ID or a store transaction ID) and shows what the server returns. It never writes a balance.

| Asset | Earned by | Bought with | Used for |
|---|---|---|---|
| **Gold coins** | Matchmaking results (see RULES.md), league rewards | In-app purchase packs | Shield bottles and avatar frames. Frames are cosmetic only. |
| **Gems** | 10 starter gems, +1 per matchmaking win, league rewards | In-app purchase packs | Dice rerolls: 1 gem each, up to **7 per Classic match** and **5 per Quick match**, in every online match type, ranked included |
| **Shield bottle** (inventory item) | League rewards | Gold, or an in-app purchase pack | One boost in a casual match or private table (see RULES.md) |
| **Avatar frames** (inventory items) | — | Gold | Shown on your profile and at the table. No gameplay effect. |

Every change is one atomic **economy operation**, recorded in:

- `economy_operations`: the idempotency key and reason. A retried request finds its key and applies nothing again.
- `wallet_ledger` (gold and gems) and `inventory_ledger` (items): signed amount, reason, time, and the match or purchase ID.

Balances can never go below zero; database CHECK constraints enforce this, and so do conditional updates. The ledger reasons are `match_reward`, `reroll`, `starter_grant`, `purchase`, `store_buy`, `boost_use`, `league_reward`, `refund`, `admin_adjustment` and `dev_grant`. Players can see their own ledger at `GET /me/ledger`.

There are **no loot boxes or random paid rewards**. Every pack lists its exact contents. If randomized rewards are ever proposed, they need explicit design approval first.

## Catalog

The catalog is defined in `src/LudoCrown.Server/Store/StoreCatalog.cs`. Prices for real-money packs come only from the App Store, localized, through StoreKit, and are never hardcoded. Gold prices are set by the server.

| Catalog ID | Kind | Contents | Price |
|---|---|---|---|
| `gems_small` | In-app purchase (consumable) | 50 gems | From App Store |
| `gems_medium` | In-app purchase (consumable) | 150 gems | From App Store |
| `gems_large` | In-app purchase (consumable) | 400 gems | From App Store |
| `coins_small` | In-app purchase (consumable) | 5,000 gold | From App Store |
| `coins_large` | In-app purchase (consumable) | 20,000 gold | From App Store |
| `shield_pack` | In-app purchase (consumable) | 3 Shield bottles | From App Store |
| `shield_single` | Gold | 1 Shield bottle | 2,000 gold |
| `frame_gilded` | Gold | Gilded Frame | 5,000 gold |
| `frame_emerald` | Gold | Emerald Frame | 8,000 gold |
| `frame_royal` | Gold | Royal Frame | 15,000 gold |

The App Store product ID is `{Store:ProductIdPrefix}.{catalog id}`, for example `com.yourcompany.ludocrown.gems_small`. The prefix is the placeholder `PLACEHOLDER.BUNDLE.ID` until you set it. The store screen shows "Store products aren't available yet" until Apple returns the products.

## Purchase flow (iPhone)

1. The app asks the server for the player's **app account token** (`GET /store/purchase-token`). This is a one-way hash of the account ID, so Apple never receives the internal ID.
2. StoreKit (`SKPaymentQueue`) charges the player. The payment carries the token as `applicationUsername`. .NET apps use StoreKit 1 because StoreKit 2 is Swift-only.
3. The app sends `{store: "app_store", productId, transactionId}` to `POST /store/purchases`.
4. The server calls Apple's **App Store Server API** (`GET /inApps/v1/transactions/{id}`, authenticated with your In-App Purchase key). It then verifies the signed transaction (JWS): ES256 signature, x5c certificate chain to the configured Apple root, and Apple's marker extensions. It also checks:
   - the bundle ID,
   - that the product exists in the catalog as an in-app purchase,
   - that the transaction is not revoked,
   - that the transaction's `appAccountToken` matches this account.
5. **Credit:** a single transaction inserts the purchase row and applies the economy operation. `UNIQUE (store, transaction_id)` means one transaction is credited once, to one account. A retry returns `already_credited`, and a different account gets `rejected`.
6. Only after the server answers `credited` or `already_credited` does the app call `finishTransaction`.

### What the player sees in each state

| State | Shown as | Server / app behavior |
|---|---|---|
| Purchasing | "Waiting for the App Store…" | — |
| Cancelled | "Cancelled. You weren't charged." | Nothing recorded |
| Deferred (Ask to Buy) | "Waiting for approval" | Credited when the transaction later arrives |
| Pending verification (Apple unreachable or not configured) | "Payment received — waiting for verification" | HTTP 202, `pending` row stored. The transaction stays unfinished and is retried. |
| Verification failed | The error message. **Never** shown as success. | HTTP 422, `rejected` row. The transaction stays unfinished so a genuine purchase can be recovered by support. |
| Credited | "✓ Added …" with new balances | Ledger and inventory updated, transaction finished |

### Recovery and restore

- Consumables are not "restorable" in Apple's model. Instead, the app re-checks **unfinished transactions** every time the store opens, and when the player taps **Check pending purchases**. This covers crashes, network loss and Ask to Buy approvals.
- **Refunds and revocations:** set the App Store Server Notifications V2 URL to `https://YOUR-SERVER-HOST/store/apple/notifications`. Signed `REFUND` and `REVOKE` notifications mark the purchase `refunded` and debit what was granted, but never below zero (`refund` ledger entries).

## Development sandbox (no Apple, no money)

- It is enabled only when the server runs in **Development** with `Store:DevSandbox=true`. That is the default in `appsettings.Development.json`, and false everywhere else.
- Its rows are stored with `store = 'dev_sandbox'` and `environment = 'development'`, enforced by a CHECK constraint.
- In the Windows app (or any build without StoreKit), the store shows a **DEVELOPMENT SANDBOX** banner. Buying lets you simulate success, pending verification or failure, so the whole flow can be tested without charges.
- Production servers never register the sandbox verifier. `/info` reports `devSandboxStore`.
- Separately, `POST /dev/economy/grant` (Development and `DevTools:AllowGemGrant=true` only) gives the signed-in account test gold, gems and Shields. It can grant only to yourself, and only within limits.

## Server configuration

Use environment variables or a secret store, never source control:

| Setting | Example | Notes |
|---|---|---|
| `Store__ProductIdPrefix` | `com.yourcompany.ludocrown` | Usually your bundle ID |
| `Store__Apple__BundleId` | `com.yourcompany.ludocrown` | Must match the app |
| `Store__Apple__IssuerId` | from App Store Connect → Users and Access → Integrations → In-App Purchase | |
| `Store__Apple__KeyId` | ″ | |
| `Store__Apple__PrivateKeyPath` or `Store__Apple__PrivateKeyPem` | the `.p8` key | **Secret** |
| `Store__Apple__RootCertificatePaths__0` | `/app/certs/AppleRootCA-G3.cer` | Download from apple.com/certificateauthority |
| `Store__Apple__Environment` | `Production` | `Sandbox` for a staging server |
| `Store__Apple__AllowSandboxFallback` | `true` | Needed for TestFlight and App Review purchases |

Until all of these are set, `/info` shows `appStoreVerification: false`, and App Store purchases stay *pending*: never credited, and never lost.

## App Store Connect setup (you)

1. Sign the Paid Applications agreement and fill in banking and tax details.
2. Create the six consumable products, with the IDs above and your prefix. Add a display name, description, price tier and review screenshot to each.
3. Create an In-App Purchase key (Users and Access → Integrations) and configure the server settings above.
4. Set the App Store Server Notifications V2 URL (production and sandbox).
5. Enable the **In-App Purchase** capability for the bundle ID.
6. Test with **sandbox testers** on a real iPhone (TestFlight or a development build):
   - success,
   - cancel,
   - Ask to Buy, using a sandbox child account,
   - interrupted purchase: kill the app before the server answers, then reopen the store,
   - refund, using the sandbox refund request,
   - a duplicate claim from a second account (must be rejected).
7. Submit the products with the app version for review.

Do not call the app release-ready until steps 1–7 have passed.
