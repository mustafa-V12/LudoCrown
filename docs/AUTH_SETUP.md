# Sign-in provider setup

Every provider is **off until configured**. The server reports enabled providers at `/auth/providers`, and the app shows only those buttons. **Guest** and **offline** play work with no setup.

Current status of each provider in this repository:

| Provider | Code | Configuration |
|---|---|---|
| Guest | Implemented and tested | Enabled by default |
| Email codes | Implemented and tested (Development writes codes to the console) | **Pending:** an SMTP / transactional email account |
| Sign in with Apple | Implemented; token verification is tested with generated keys | **Pending:** your Apple Developer account |
| Google | Implemented (server OAuth + PKCE) | **Pending:** a Google Cloud OAuth client |
| Facebook | Implemented (server OAuth + PKCE, `appsecret_proof`) | **Pending:** a Meta developer app and App Review |

> Apple's rule: if the app offers Google or Facebook sign-in, it must also offer Sign in with Apple (or an equivalent privacy-focused option). Configure Apple before you enable Google or Facebook in a release build.

## Sign in with Apple
1. In the Apple Developer portal, go to **Certificates, IDs & Profiles → Identifiers**. Select your App ID (the bundle identifier, which replaces `com.example.ludocrown`) and enable **Sign in with Apple**.
2. Regenerate the provisioning profiles.
3. In `LudoCrown.Mobile.csproj`, build with `-p:EnableAppleSignIn=true`, or set the default to `true`. This adds the entitlement.
4. **Server** — set:
   - `Auth__Apple__ClientIds__0=<your bundle id>`
   - **For token revocation on account deletion** (Apple requires this):
     1. Create a key under **Keys** with *Sign in with Apple* enabled, and download the `.p8` file (you can only download it once).
     2. Set `Auth__Apple__TeamId`, `Auth__Apple__KeyId`, and `Auth__Apple__PrivateKey` (the full PEM text). Keep the key in your secret store, **never** in git.
5. **Test on a real device or simulator signed in with an Apple ID:**
   1. Sign in.
   2. Check that `/me` shows `linkedProviders: ["apple"]`.
   3. Delete the account, then check that Ludo Crown disappears from **Settings → Apple ID → Sign in with Apple**.

## Google
1. In Google Cloud Console, go to **APIs & Services → OAuth consent screen**. Choose External, enter your app name and support email, add the scope `openid profile`, and publish.
2. Under **Credentials → Create OAuth client ID → Web application**, add the authorized redirect URI `https://<your-server>/auth/oauth/google/callback`.
3. Server:
   - `Auth__Google__ClientId=…apps.googleusercontent.com`
   - `Auth__Google__ClientSecret=…`
   - `Auth__PublicBaseUrl=https://<your-server>`

The app opens the server's `/auth/oauth/google/start` in the system sheet. The server finishes the OAuth flow and hands the app a single-use code through `ludocrown://auth`. Only the profile scope is requested; the email address is not.

## Facebook
1. At developers.facebook.com, create an app (type: Consumer) and add **Facebook Login**.
2. Under **Valid OAuth Redirect URIs**, add `https://<your-server>/auth/oauth/facebook/callback`.
3. Set the privacy policy URL, data deletion instructions URL, and app icon. Switch the app to **Live**. `public_profile` is available without extra review.
4. Server:
   - `Auth__Facebook__ClientId=<App ID>`
   - `Auth__Facebook__ClientSecret=<App Secret>`
   - `Auth__FacebookGraphVersion` (default `v21.0`): check the current Graph API version and update it if needed.

## Email (passwordless codes)
1. Get a transactional email account (e.g. Postmark, SendGrid, Amazon SES, Mailgun) and verify your sending domain (SPF/DKIM).
2. Server settings:
   - `Auth__Email__Enabled=true`
   - `Auth__Email__Sender=Smtp`
   - `Auth__Email__SmtpHost`, `SmtpPort` (587), `SmtpUser`, `SmtpPassword`
   - `Auth__Email__FromAddress=no-reply@your-domain.com`
3. How codes are protected:
   - 6 digits, valid for 10 minutes, at most 5 attempts per code,
   - one new code per 45 seconds per address,
   - IP rate limiting on `/auth`.

## Guest accounts
- A guest account is a normal server account with no linked sign-in method. Its refresh token in the Keychain is the only key.
- **Profile → Link an account** attaches Apple, Google, Facebook or email to the same account, keeping all progress.
- If that sign-in already belongs to another account, the app explains it and nothing is merged.
- Signing out of an unlinked guest account warns that the account can't be recovered.

## Development-only behaviour
- In Development, email codes appear in the server console (`DEV EMAIL to …`), and the JWT key is random on each start.
- **There is no mock "sign in as anyone" login.** Development uses the real guest and email flows.
