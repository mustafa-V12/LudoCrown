using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LudoCrown.Server.Auth;
using LudoCrown.Server.Data;
using Microsoft.Extensions.Options;

namespace LudoCrown.Server.Tests;

public class TokenServiceTests
{
    [Fact]
    public async Task Issued_access_tokens_validate_and_carry_the_user_id()
    {
        var h = new ServerHarness();
        var user = await h.NewUserAsync();
        var pair = await h.Tokens.IssueAsync(user, false);
        var principal = h.Tokens.ValidateAccessToken(pair.AccessToken, "test");
        Assert.NotNull(principal);
        Assert.Equal(user, principal!.UserId());
        Assert.False(principal.IsGuest());
    }

    [Fact]
    public async Task Tampered_or_expired_tokens_are_rejected()
    {
        var h = new ServerHarness();
        var user = await h.NewUserAsync();
        var token = (await h.Tokens.IssueAsync(user, true)).AccessToken;
        var parts = token.Split('.');

        // Change the subject without re-signing.
        var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(TokenService.FromBase64Url(parts[1]))!;
        payload["sub"] = Guid.NewGuid().ToString();
        var forged = $"{parts[0]}.{TokenService.Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload))}.{parts[2]}";
        Assert.Null(h.Tokens.ValidateAccessToken(forged, "test"));

        // "alg": "none" downgrade.
        var none = $"{TokenService.Base64Url("{\"alg\":\"none\"}"u8)}.{parts[1]}.";
        Assert.Null(h.Tokens.ValidateAccessToken(none, "test"));

        Assert.Null(h.Tokens.ValidateAccessToken("garbage", "test"));
        Assert.Null(h.Tokens.ValidateAccessToken(null, "test"));

        h.Time.Advance(TimeSpan.FromMinutes(16));
        Assert.Null(h.Tokens.ValidateAccessToken(token, "test"));
    }

    [Fact]
    public async Task Tokens_signed_with_another_key_are_rejected()
    {
        var h = new ServerHarness();
        var other = new TokenService(Microsoft.Extensions.Options.Options.Create(new LudoCrown.Server.Options.JwtOptions { SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }), h.Store, h.Time);
        var token = other.CreateAccessToken(Guid.NewGuid(), false, out _);
        Assert.Null(h.Tokens.ValidateAccessToken(token, "test"));
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_reuse_revokes_every_session()
    {
        var h = new ServerHarness();
        var user = await h.NewUserAsync();
        var first = await h.Tokens.IssueAsync(user, false);
        var second = await h.Tokens.RefreshAsync(first.RefreshToken);
        Assert.NotNull(second);
        Assert.NotEqual(first.RefreshToken, second!.RefreshToken);

        // Replaying the old token (e.g. stolen) fails and kills the new one too.
        Assert.Null(await h.Tokens.RefreshAsync(first.RefreshToken));
        Assert.Null(await h.Tokens.RefreshAsync(second.RefreshToken));
    }

    [Fact]
    public async Task Refresh_tokens_are_stored_hashed_and_expire()
    {
        var h = new ServerHarness();
        var user = await h.NewUserAsync();
        var pair = await h.Tokens.IssueAsync(user, false);
        Assert.Null(await h.Store.GetRefreshTokenAsync(Encoding.UTF8.GetBytes(pair.RefreshToken)));
        Assert.NotNull(await h.Store.GetRefreshTokenAsync(TokenService.Hash(pair.RefreshToken)));
        h.Time.Advance(TimeSpan.FromDays(61));
        Assert.Null(await h.Tokens.RefreshAsync(pair.RefreshToken));
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        var h = new ServerHarness();
        var pair = await h.Tokens.IssueAsync(await h.NewUserAsync(), false);
        await h.Tokens.RevokeAsync(pair.RefreshToken);
        Assert.Null(await h.Tokens.RefreshAsync(pair.RefreshToken));
    }
}

public class IdTokenValidatorTests
{
    private sealed class FakeJwks(string kid, RSAParameters key) : IJwksSource
    {
        public Task<RSAParameters?> GetKeyAsync(string jwksUrl, string requestedKid, bool forceRefresh, CancellationToken ct) =>
            Task.FromResult<RSAParameters?>(requestedKid == kid ? key : null);
    }

    private static readonly RSA Rsa = RSA.Create(2048);
    private static readonly ManualTimeProvider Time = new();
    private static readonly IdTokenRequirements Apple = new(IdTokenValidator.AppleJwks, IdTokenValidator.AppleIssuers, new[] { "com.example.ludocrown" });

    private static IdTokenValidator Validator() => new(new FakeJwks("k1", Rsa.ExportParameters(false)), Time);

    private static string Sign(object payload, string kid = "k1", RSA? key = null, string alg = "RS256")
    {
        var header = TokenService.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg, kid }));
        var body = TokenService.Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        var sig = (key ?? Rsa).SignData(Encoding.ASCII.GetBytes($"{header}.{body}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{body}.{TokenService.Base64Url(sig)}";
    }

    private static object Claims(string aud = "com.example.ludocrown", string iss = "https://appleid.apple.com", int expMinutes = 10) => new
    {
        iss,
        aud,
        sub = "001234.abcd",
        iat = Time.GetUtcNow().ToUnixTimeSeconds(),
        exp = Time.GetUtcNow().AddMinutes(expMinutes).ToUnixTimeSeconds(),
        email = "x@privaterelay.appleid.com",
        email_verified = "true",
    };

    [Fact]
    public async Task Valid_apple_token_is_accepted()
    {
        var claims = await Validator().ValidateAsync(Sign(Claims()), Apple);
        Assert.NotNull(claims);
        Assert.Equal("001234.abcd", claims!.Subject);
        Assert.True(claims.EmailVerified);
    }

    [Fact]
    public async Task Wrong_audience_issuer_or_expired_tokens_are_rejected()
    {
        Assert.Null(await Validator().ValidateAsync(Sign(Claims(aud: "com.attacker.app")), Apple));
        Assert.Null(await Validator().ValidateAsync(Sign(Claims(iss: "https://evil.example")), Apple));
        Assert.Null(await Validator().ValidateAsync(Sign(Claims(expMinutes: -10)), Apple));
    }

    [Fact]
    public async Task Bad_signatures_unknown_keys_and_other_algorithms_are_rejected()
    {
        using var other = RSA.Create(2048);
        Assert.Null(await Validator().ValidateAsync(Sign(Claims(), key: other), Apple));
        Assert.Null(await Validator().ValidateAsync(Sign(Claims(), kid: "unknown"), Apple));
        Assert.Null(await Validator().ValidateAsync(Sign(Claims(), alg: "HS256"), Apple));
        Assert.Null(await Validator().ValidateAsync("a.b.c", Apple));
    }

    [Fact]
    public void Jwks_documents_are_parsed()
    {
        var p = Rsa.ExportParameters(false);
        var json = JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", kid = "abc", n = TokenService.Base64Url(p.Modulus), e = TokenService.Base64Url(p.Exponent), alg = "RS256", use = "sig" } } });
        var keys = HttpJwksSource.ParseJwks(json);
        Assert.Equal(p.Modulus, keys["abc"].Modulus);
    }
}

public class AccountServiceTests
{
    [Fact]
    public async Task Guests_get_a_working_account_with_a_generated_name()
    {
        var h = new ServerHarness();
        var pair = await h.Accounts.CreateGuestAsync(null);
        Assert.True(pair.IsGuest);
        var profile = await h.Store.GetProfileAsync(pair.UserId);
        Assert.StartsWith("Player", profile!.DisplayName);
        Assert.Equal(8, profile.FriendCode.Length);
    }

    [Fact]
    public async Task Linking_a_provider_upgrades_the_guest_and_keeps_progress()
    {
        var h = new ServerHarness();
        var guest = await h.Accounts.CreateGuestAsync("Mustafa");
        var result = await h.Accounts.SignInWithIdentityAsync(AuthProviders.Apple, "apple-sub", null, null, guest.UserId);
        Assert.Equal(SignInOutcome.Linked, result.Outcome);
        Assert.False(result.Tokens!.IsGuest);
        Assert.Equal(guest.UserId, result.Tokens.UserId);

        // Signing in later with Apple returns the same account.
        var again = await h.Accounts.SignInWithIdentityAsync(AuthProviders.Apple, "apple-sub", null, null, null);
        Assert.Equal(SignInOutcome.SignedIn, again.Outcome);
        Assert.Equal(guest.UserId, again.Tokens!.UserId);
        Assert.Equal("Mustafa", (await h.Store.GetProfileAsync(guest.UserId))!.DisplayName);
    }

    [Fact]
    public async Task An_identity_cannot_be_linked_to_two_accounts()
    {
        var h = new ServerHarness();
        await h.Accounts.SignInWithIdentityAsync(AuthProviders.Google, "g-1", null, "Ana", null);
        var guest = await h.Accounts.CreateGuestAsync(null);
        var result = await h.Accounts.SignInWithIdentityAsync(AuthProviders.Google, "g-1", null, null, guest.UserId);
        Assert.Equal(SignInOutcome.IdentityBelongsToAnotherAccount, result.Outcome);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public async Task Deleting_an_account_removes_personal_data_and_sessions_but_anonymises_shared_history()
    {
        var h = new ServerHarness();
        var (a, b, id) = await h.StartTwoPlayerAsync();
        await h.Matches.LeaveAsync(a, id.ToString("N"));
        await h.Matches.LeaveAsync(b, id.ToString("N"));
        var pair = await h.Tokens.IssueAsync(a, false);
        await h.Store.AddIdentityAsync(a, AuthProviders.Email, "a@example.com", "a@example.com");

        await h.Accounts.DeleteAccountAsync(a);

        Assert.Null(await h.Store.GetUserAsync(a));
        Assert.Null(await h.Store.GetProfileAsync(a));
        Assert.Empty(await h.Store.GetIdentitiesAsync(a));
        Assert.Null(await h.Store.FindUserByIdentityAsync(AuthProviders.Email, "a@example.com"));
        Assert.Null(await h.Tokens.RefreshAsync(pair.RefreshToken));
        var bobHistory = Assert.Single(await h.Store.GetHistoryAsync(b, 10));
        Assert.Contains("Deleted player", bobHistory.Opponents);
    }

    [Fact]
    public async Task Export_contains_the_users_data()
    {
        var h = new ServerHarness();
        var guest = await h.Accounts.CreateGuestAsync("Exporter");
        var export = await h.Accounts.ExportAsync(guest.UserId);
        Assert.Equal("Exporter", export!.Profile!.DisplayName);
        Assert.Contains(export.Identities, i => i.Provider == AuthProviders.Guest);
    }

    [Theory]
    [InlineData("  Ana  María ", "Ana María")]
    [InlineData("<b>Bob</b>", "bBobb")]
    [InlineData("x", null)]
    [InlineData(null, null)]
    public void Provider_names_are_sanitised(string? input, string? expected)
    {
        var name = AccountService.SanitizeName(input);
        if (expected is null) Assert.StartsWith("Player", name);
        else Assert.Equal(expected, name);
    }
}

public class EmailCodeServiceTests
{
    private sealed class CapturingSender : IEmailSender
    {
        public List<(string Email, string Code)> Sent { get; } = new();
        public Task SendSignInCodeAsync(string email, string code, CancellationToken ct) { Sent.Add((email, code)); return Task.CompletedTask; }
    }

    private static (EmailCodeService Service, CapturingSender Sender, ServerHarness H) Create()
    {
        var h = new ServerHarness();
        var sender = new CapturingSender();
        return (new EmailCodeService(h.Store, sender, Microsoft.Extensions.Options.Options.Create(h.Auth), Microsoft.Extensions.Options.Options.Create(h.Jwt), h.Time), sender, h);
    }

    [Fact]
    public async Task Correct_code_signs_in_once()
    {
        var (svc, sender, _) = Create();
        Assert.Equal(EmailCodeResult.Sent, await svc.StartAsync(" Player@Example.com "));
        var (email, code) = Assert.Single(sender.Sent);
        Assert.Equal("player@example.com", email);
        var (result, address) = await svc.VerifyAsync("player@example.com", code);
        Assert.Equal(EmailVerifyResult.Ok, result);
        Assert.Equal("player@example.com", address);
        Assert.Equal(EmailVerifyResult.Invalid, (await svc.VerifyAsync("player@example.com", code)).Result);
    }

    [Fact]
    public async Task Wrong_codes_are_limited()
    {
        var (svc, sender, _) = Create();
        await svc.StartAsync("p@example.com");
        var real = sender.Sent[0].Code;
        var wrong = real == "000000" ? "111111" : "000000";
        for (var i = 0; i < 3; i++) Assert.Equal(EmailVerifyResult.Invalid, (await svc.VerifyAsync("p@example.com", wrong)).Result);
        Assert.Equal(EmailVerifyResult.TooManyAttempts, (await svc.VerifyAsync("p@example.com", real)).Result);
    }

    [Fact]
    public async Task Codes_expire_and_resending_is_throttled()
    {
        var (svc, sender, h) = Create();
        await svc.StartAsync("p@example.com");
        Assert.Equal(EmailCodeResult.TooSoon, await svc.StartAsync("p@example.com"));
        h.Time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(EmailVerifyResult.Invalid, (await svc.VerifyAsync("p@example.com", sender.Sent[0].Code)).Result);
        Assert.Equal(EmailCodeResult.Sent, await svc.StartAsync("p@example.com"));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("a@b")]
    [InlineData("two@@example.com")]
    [InlineData("")]
    public async Task Invalid_addresses_are_rejected(string email)
    {
        var (svc, _, _) = Create();
        Assert.Equal(EmailCodeResult.InvalidEmail, await svc.StartAsync(email));
    }
}
