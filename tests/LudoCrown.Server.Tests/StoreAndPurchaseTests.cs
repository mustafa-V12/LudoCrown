using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LudoCrown.Core.Model;
using LudoCrown.Server.Data;
using LudoCrown.Server.Store;
using Microsoft.Extensions.Logging.Abstractions;

namespace LudoCrown.Server.Tests;

/// <summary>A verifier whose answer the test controls (stands in for Apple).</summary>
public sealed class FakeVerifier(string store) : IPurchaseVerifier
{
    public string Store => store;
    public Func<PurchaseClaim, PurchaseVerification> Answer { get; set; } = c => new PurchaseVerification(VerificationStatus.Verified, null, c.ProductId, c.TransactionId);
    public int Calls { get; private set; }
    public Task<PurchaseVerification> VerifyAsync(PurchaseClaim claim, CancellationToken ct = default) { Calls++; return Task.FromResult(Answer(claim)); }
}

public class PurchaseTests
{
    private static string Tx() => "devtx-" + Guid.NewGuid();

    [Fact]
    public async Task Successful_sandbox_purchase_is_verified_credited_and_ledgered()
    {
        var h = new ServerHarness();
        var u = await h.NewUserAsync();
        var tx = Tx();
        var r = await h.Purchases.ProcessAsync(u, new PurchaseClaim(PurchaseStores.DevSandbox, "gems_small", tx));
        Assert.Equal(PurchaseOutcome.Credited, r.Outcome);
        Assert.Equal(Economy.StarterGems + 50, r.Balances!.Gems);
        Assert.Equal(Economy.StarterGems + 50, await h.Store.GetGemsAsync(u));
        var record = await h.Store.GetPurchaseAsync(PurchaseStores.DevSandbox, tx);
        Assert.Equal(PurchaseStatus.Credited, record!.Status);
        Assert.Equal("development", record.Environment);
        Assert.Contains(h.Store.Ledger, l => l.User == u && l.Entry.Reason == Economy.Reasons.Purchase && l.Entry.Delta == 50 && l.Entry.PurchaseId == record.Id);
    }

    [Fact]
    public async Task Duplicate_purchase_is_credited_once()
    {
        var h = new ServerHarness();
        var u = await h.NewUserAsync();
        var claim = new PurchaseClaim(PurchaseStores.DevSandbox, "coins_small", Tx());
        Assert.Equal(PurchaseOutcome.Credited, (await h.Purchases.ProcessAsync(u, claim)).Outcome);
        var again = await h.Purchases.ProcessAsync(u, claim);
        Assert.Equal(PurchaseOutcome.AlreadyCredited, again.Outcome);
        Assert.Empty(again.Granted);
        Assert.Equal(5000, await h.Store.GetCoinsAsync(u));
    }

    [Fact]
    public async Task Concurrent_duplicate_submissions_credit_once()
    {
        var h = new ServerHarness();
        var u = await h.NewUserAsync();
        var claim = new PurchaseClaim(PurchaseStores.DevSandbox, "shield_pack", Tx());
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => h.Purchases.ProcessAsync(u, claim))));
        Assert.All(results, r => Assert.Contains(r.Outcome, new[] { PurchaseOutcome.Credited, PurchaseOutcome.AlreadyCredited }));
        Assert.Equal(3, (await h.Store.GetBalancesAsync(u))!.Count(Boosts.ShieldId));
    }

    [Fact]
    public async Task Failed_verification_credits_nothing_and_is_recorded()
    {
        var h = new ServerHarness();
        var u = await h.NewUserAsync();
        var tx = Tx();
        var r = await h.Purchases.ProcessAsync(u, new PurchaseClaim(PurchaseStores.DevSandbox, "gems_large", tx, "fail"));
        Assert.Equal(PurchaseOutcome.Rejected, r.Outcome);
        Assert.Equal(Economy.StarterGems, await h.Store.GetGemsAsync(u));
        Assert.Equal(PurchaseStatus.Rejected, (await h.Store.GetPurchaseAsync(PurchaseStores.DevSandbox, tx))!.Status);
        // Retrying a rejected transaction does not turn it into a success.
        Assert.Equal(PurchaseOutcome.Rejected, (await h.Purchases.ProcessAsync(u, new PurchaseClaim(PurchaseStores.DevSandbox, "gems_large", tx))).Outcome);
    }

    [Fact]
    public async Task Pending_verification_credits_nothing_until_a_retry_succeeds()
    {
        var h = new ServerHarness();
        var u = await h.NewUserAsync();
        var tx = Tx();
        var pending = await h.Purchases.ProcessAsync(u, new PurchaseClaim(PurchaseStores.DevSandbox, "gems_medium", tx, "pending"));
        Assert.Equal(PurchaseOutcome.Pending, pending.Outcome);
        Assert.Null(pending.Balances);
        Assert.Equal(Economy.StarterGems, await h.Store.GetGemsAsync(u));
        Assert.Equal(PurchaseStatus.Pending, (await h.Store.GetPurchaseAsync(PurchaseStores.DevSandbox, tx))!.Status);

        var later = await h.Purchases.ProcessAsync(u, new PurchaseClaim(PurchaseStores.DevSandbox, "gems_medium", tx));
        Assert.Equal(PurchaseOutcome.Credited, later.Outcome);
        Assert.Equal(Economy.StarterGems + 150, await h.Store.GetGemsAsync(u));
    }

    [Fact]
    public async Task A_transaction_cannot_be_claimed_by_a_second_account()
    {
        var h = new ServerHarness();
        var a = await h.NewUserAsync("Alice");
        var b = await h.NewUserAsync("Bobby");
        var claim = new PurchaseClaim(PurchaseStores.DevSandbox, "gems_small", Tx());
        await h.Purchases.ProcessAsync(a, claim);
        var stolen = await h.Purchases.ProcessAsync(b, claim);
        Assert.Equal(PurchaseOutcome.Rejected, stolen.Outcome);
        Assert.Equal(Economy.StarterGems, await h.Store.GetGemsAsync(b));
    }

    [Fact]
    public async Task Unknown_products_gold_items_and_disabled_stores_are_rejected()
    {
        var h = new ServerHarness();
        var u = await h.NewUserAsync();
        Assert.Equal(PurchaseOutcome.Rejected, (await h.Purchases.ProcessAsync(u, new PurchaseClaim(PurchaseStores.DevSandbox, "gems_infinite", Tx()))).Outcome);
        Assert.Equal(PurchaseOutcome.Rejected, (await h.Purchases.ProcessAsync(u, new PurchaseClaim(PurchaseStores.DevSandbox, "frame_royal", Tx()))).Outcome);
        Assert.Equal(PurchaseOutcome.Rejected, (await h.Purchases.ProcessAsync(u, new PurchaseClaim(PurchaseStores.AppStore, "PLACEHOLDER.BUNDLE.ID.gems_small", "123"))).Outcome);
        Assert.Equal(PurchaseOutcome.Rejected, (await h.Purchases.ProcessAsync(u, new PurchaseClaim(PurchaseStores.DevSandbox, "gems_small", "not-a-sandbox-tx"))).Outcome);
        Assert.Equal(Economy.StarterGems, await h.Store.GetGemsAsync(u));
    }

    [Fact]
    public async Task App_store_purchase_requires_matching_account_token_and_product()
    {
        var h = new ServerHarness();
        var apple = new FakeVerifier(PurchaseStores.AppStore);
        h.Verifiers.Add(apple);
        var u = await h.NewUserAsync();
        var productId = StoreCatalog.AppStoreProductId(h.StoreOptions.ProductIdPrefix, StoreCatalog.Get("gems_small")!);

        apple.Answer = c => new PurchaseVerification(VerificationStatus.Verified, null, c.ProductId, c.TransactionId, c.TransactionId, "sandbox", AppAccountTokens.For(Guid.NewGuid()));
        Assert.Equal(PurchaseOutcome.Rejected, (await h.Purchases.ProcessAsync(u, new PurchaseClaim(PurchaseStores.AppStore, productId, "1001"))).Outcome);

        apple.Answer = c => new PurchaseVerification(VerificationStatus.Verified, null, c.ProductId, c.TransactionId, c.TransactionId, "sandbox", null);
        Assert.Equal(PurchaseOutcome.Rejected, (await h.Purchases.ProcessAsync(u, new PurchaseClaim(PurchaseStores.AppStore, productId, "1002"))).Outcome);

        var otherProduct = StoreCatalog.AppStoreProductId(h.StoreOptions.ProductIdPrefix, StoreCatalog.Get("gems_large")!);
        apple.Answer = c => new PurchaseVerification(VerificationStatus.Verified, null, otherProduct, c.TransactionId, c.TransactionId, "sandbox", AppAccountTokens.For(u));
        Assert.Equal(PurchaseOutcome.Rejected, (await h.Purchases.ProcessAsync(u, new PurchaseClaim(PurchaseStores.AppStore, productId, "1003"))).Outcome);

        apple.Answer = c => new PurchaseVerification(VerificationStatus.Verified, null, c.ProductId, c.TransactionId, c.TransactionId, "sandbox", AppAccountTokens.For(u), Quantity: 2);
        var ok = await h.Purchases.ProcessAsync(u, new PurchaseClaim(PurchaseStores.AppStore, productId, "1004"));
        Assert.Equal(PurchaseOutcome.Credited, ok.Outcome);
        Assert.Equal(Economy.StarterGems + 100, await h.Store.GetGemsAsync(u)); // quantity 2 × 50
        Assert.Equal("sandbox", (await h.Store.GetPurchaseAsync(PurchaseStores.AppStore, "1004"))!.Environment);
    }

    [Fact]
    public async Task App_account_token_is_stable_per_account_and_does_not_reveal_the_id()
    {
        var id = Guid.NewGuid();
        Assert.Equal(AppAccountTokens.For(id), AppAccountTokens.For(id));
        Assert.NotEqual(AppAccountTokens.For(id), AppAccountTokens.For(Guid.NewGuid()));
        Assert.NotEqual(id, AppAccountTokens.For(id));
    }

    [Fact]
    public async Task Catalog_has_fixed_contents_and_placeholder_product_ids()
    {
        Assert.All(StoreCatalog.Products, p => Assert.NotEmpty(p.Contents));
        Assert.All(StoreCatalog.Products.Where(p => p.Kind == ProductKind.GoldPurchase), p => Assert.True(p.CoinPrice > 0));
        Assert.All(StoreCatalog.Products.Where(p => p.Kind == ProductKind.InAppPurchase), p => Assert.Null(p.CoinPrice));
        Assert.Equal(StoreCatalog.PlaceholderPrefix, new StoreOptions().ProductIdPrefix);
        Assert.Equal("PLACEHOLDER.BUNDLE.ID.gems_small", StoreCatalog.AppStoreProductId(StoreCatalog.PlaceholderPrefix, StoreCatalog.Get("gems_small")!));
        Assert.Equal(StoreCatalog.Products.Count, StoreCatalog.Products.Select(p => p.Id).Distinct().Count());
    }
}

public class GoldStoreTests
{
    [Fact]
    public async Task Buying_with_gold_is_atomic_idempotent_and_needs_enough_coins()
    {
        var h = new ServerHarness();
        var u = await h.NewUserAsync();
        var req = Guid.NewGuid().ToString();
        Assert.Equal(BuyOutcome.NotEnoughCoins, (await h.Purchases.BuyWithCoinsAsync(u, "shield_single", req)).Outcome);
        Assert.Equal(0, (await h.Store.GetBalancesAsync(u))!.Count(Boosts.ShieldId));

        await h.GrantAsync(u, coins: 2500);
        req = Guid.NewGuid().ToString();
        Assert.Equal(BuyOutcome.Bought, (await h.Purchases.BuyWithCoinsAsync(u, "shield_single", req)).Outcome);
        Assert.Equal(BuyOutcome.AlreadyBought, (await h.Purchases.BuyWithCoinsAsync(u, "shield_single", req)).Outcome); // retry
        var b = (await h.Store.GetBalancesAsync(u))!;
        Assert.Equal(500, b.Coins);
        Assert.Equal(1, b.Count(Boosts.ShieldId));
        Assert.Contains(h.Store.Ledger, l => l.User == u && l.Entry.Asset == Economy.Coins && l.Entry.Delta == -2000 && l.Entry.Reason == Economy.Reasons.StoreBuy);
    }

    [Fact]
    public async Task Cosmetics_are_owned_once_and_real_money_items_are_not_sold_for_gold()
    {
        var h = new ServerHarness();
        var u = await h.NewUserAsync();
        await h.GrantAsync(u, coins: 20000);
        Assert.Equal(BuyOutcome.Bought, (await h.Purchases.BuyWithCoinsAsync(u, "frame_gilded", Guid.NewGuid().ToString())).Outcome);
        Assert.Equal(BuyOutcome.AlreadyOwned, (await h.Purchases.BuyWithCoinsAsync(u, "frame_gilded", Guid.NewGuid().ToString())).Outcome);
        Assert.Equal(15000, await h.Store.GetCoinsAsync(u));
        Assert.Equal(BuyOutcome.NotForGold, (await h.Purchases.BuyWithCoinsAsync(u, "gems_large", Guid.NewGuid().ToString())).Outcome);
        Assert.Equal(BuyOutcome.UnknownProduct, (await h.Purchases.BuyWithCoinsAsync(u, "frame_gilded", "not-a-guid")).Outcome);
    }

    [Fact]
    public async Task Concurrent_spending_never_goes_below_zero()
    {
        var h = new ServerHarness();
        var u = await h.NewUserAsync();
        await h.GrantAsync(u, coins: 5000); // enough for 2 Shields
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => h.Purchases.BuyWithCoinsAsync(u, "shield_single", Guid.NewGuid().ToString()))));
        Assert.Equal(2, results.Count(r => r.Outcome == BuyOutcome.Bought));
        var b = (await h.Store.GetBalancesAsync(u))!;
        Assert.Equal(1000, b.Coins);
        Assert.Equal(2, b.Count(Boosts.ShieldId));
    }
}

public class EconomyStoreTests
{
    [Fact]
    public async Task Operations_are_all_or_nothing_and_idempotent()
    {
        var store = new InMemoryStore();
        var u = (await store.CreateUserAsync(false, "Eco", "crown")).Id;
        var now = DateTimeOffset.UtcNow;
        var op = new EconomyOperation(u, "k1", Economy.Reasons.AdminAdjustment, new[] { new AssetChange(Economy.Coins, 100), new AssetChange(Economy.Gems, -1000) }, now);
        Assert.Equal(EconomyOutcome.Insufficient, (await store.ApplyAsync(op)).Outcome);
        Assert.Equal(0, await store.GetCoinsAsync(u)); // coins not added either

        var ok = new EconomyOperation(u, "k2", Economy.Reasons.AdminAdjustment, new[] { new AssetChange(Economy.Coins, 100), new AssetChange(Economy.Coins, 50), new AssetChange(Boosts.ShieldId, 2) }, now);
        Assert.Equal(EconomyOutcome.Applied, (await store.ApplyAsync(ok)).Outcome);
        Assert.Equal(EconomyOutcome.AlreadyApplied, (await store.ApplyAsync(ok)).Outcome);
        var b = (await store.GetBalancesAsync(u))!;
        Assert.Equal(150, b.Coins);
        Assert.Equal(2, b.Count(Boosts.ShieldId));
        Assert.Equal(EconomyOutcome.UnknownUser, (await store.ApplyAsync(ok with { UserId = Guid.NewGuid() })).Outcome);

        var frameTwice = new EconomyOperation(u, "k3", Economy.Reasons.AdminAdjustment, new[] { new AssetChange(Items.FrameRoyal.Id, 2) }, now);
        Assert.Equal(EconomyOutcome.Insufficient, (await store.ApplyAsync(frameTwice)).Outcome); // frames max 1
        var ledger = await store.GetLedgerAsync(u, 10);
        Assert.Contains(ledger, l => l.Asset == Economy.Coins && l.Delta == 150);
        Assert.Contains(ledger, l => l.Asset == Boosts.ShieldId && l.Delta == 2);
    }
}

public class AppStoreVerifierTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Requests.Add(request); return Task.FromResult(respond(request)); }
    }

    private sealed record Chain(X509Certificate2 Root, X509Certificate2 Intermediate, X509Certificate2 Leaf, ECDsa LeafKey);

    private static Chain MakeChain(bool markers = true)
    {
        var now = DateTimeOffset.UtcNow;
        var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootReq = new CertificateRequest("CN=Test Apple Root", rootKey, HashAlgorithmName.SHA256);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var root = rootReq.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));

        var interKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var interReq = new CertificateRequest("CN=Test WWDR", interKey, HashAlgorithmName.SHA256);
        interReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        interReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        if (markers) interReq.CertificateExtensions.Add(new X509Extension(new Oid("1.2.840.113635.100.6.2.1"), new byte[] { 0x05, 0x00 }, false));
        var inter = interReq.Create(root, now.AddDays(-1), now.AddYears(4), new byte[] { 1, 2, 3, 4 }).CopyWithPrivateKey(interKey);

        var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafReq = new CertificateRequest("CN=Test StoreKit Signing", leafKey, HashAlgorithmName.SHA256);
        leafReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        if (markers) leafReq.CertificateExtensions.Add(new X509Extension(new Oid("1.2.840.113635.100.6.11.1"), new byte[] { 0x05, 0x00 }, false));
        var leaf = leafReq.Create(inter, now.AddDays(-1), now.AddYears(3), new byte[] { 5, 6, 7, 8 });
        return new Chain(root, inter, leaf, leafKey);
    }

    private static string Sign(Chain c, object payload)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", x5c = new[] { c.Leaf, c.Intermediate, c.Root }.Select(x => Convert.ToBase64String(x.RawData)).ToArray() });
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        var input = AppleJwsVerifier.ToBase64Url(header) + "." + AppleJwsVerifier.ToBase64Url(body);
        return input + "." + AppleJwsVerifier.ToBase64Url(c.LeafKey.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256));
    }

    private static object Tx(string id = "2000000123", string bundle = "com.placeholder.test", string type = "Consumable", long? revoked = null, Guid? token = null) => new Dictionary<string, object?>
    {
        ["transactionId"] = id, ["originalTransactionId"] = id, ["bundleId"] = bundle, ["productId"] = "PLACEHOLDER.BUNDLE.ID.gems_small",
        ["purchaseDate"] = 1767225600000L, ["quantity"] = 1, ["type"] = type, ["environment"] = "Sandbox",
        ["appAccountToken"] = token?.ToString(), ["revocationDate"] = revoked,
    };

    private static (AppStoreVerifier Verifier, Handler Handler) Make(Chain trusted, Func<HttpRequestMessage, HttpResponseMessage> respond, ECDsa? apiKey = null)
    {
        var handler = new Handler(respond);
        var options = new AppleStoreOptions { BundleId = "com.placeholder.test", IssuerId = "issuer", KeyId = "KEYID", PrivateKeyPem = "set-by-test", RootCertificatePaths = new[] { "test-root" } };
        var v = new AppStoreVerifier(new HttpClient(handler), options, TimeProvider.System, NullLogger<AppStoreVerifier>.Instance,
            new[] { trusted.Root }, apiKey ?? ECDsa.Create(ECCurve.NamedCurves.nistP256));
        return (v, handler);
    }

    private static HttpResponseMessage Ok(string signed) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { signedTransactionInfo = signed })) };

    [Fact]
    public async Task Valid_signed_transaction_is_verified()
    {
        var chain = MakeChain();
        var token = Guid.NewGuid();
        var (v, handler) = Make(chain, _ => Ok(Sign(chain, Tx(token: token))));
        var r = await v.VerifyAsync(new PurchaseClaim(PurchaseStores.AppStore, "PLACEHOLDER.BUNDLE.ID.gems_small", "2000000123"));
        Assert.Equal(VerificationStatus.Verified, r.Status);
        Assert.Equal("PLACEHOLDER.BUNDLE.ID.gems_small", r.ProductId);
        Assert.Equal("sandbox", r.Environment);
        Assert.Equal(token, r.AppAccountToken);
        var req = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.EndsWith("/inApps/v1/transactions/2000000123", req.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Api_token_is_an_es256_jwt_with_the_required_claims()
    {
        var chain = MakeChain();
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (v, _) = Make(chain, _ => new HttpResponseMessage(HttpStatusCode.NotFound), key);
        var jwt = v.CreateApiToken().Split('.');
        using var header = JsonDocument.Parse(AppleJwsVerifier.Base64Url(jwt[0]));
        using var payload = JsonDocument.Parse(AppleJwsVerifier.Base64Url(jwt[1]));
        Assert.Equal("ES256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("KEYID", header.RootElement.GetProperty("kid").GetString());
        Assert.Equal("appstoreconnect-v1", payload.RootElement.GetProperty("aud").GetString());
        Assert.Equal("issuer", payload.RootElement.GetProperty("iss").GetString());
        Assert.Equal("com.placeholder.test", payload.RootElement.GetProperty("bid").GetString());
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]), AppleJwsVerifier.Base64Url(jwt[2]), HashAlgorithmName.SHA256));
    }

    [Fact]
    public async Task Tampered_untrusted_or_unmarked_signatures_are_rejected()
    {
        var chain = MakeChain();
        var signed = Sign(chain, Tx());
        var parts = signed.Split('.');
        var tamperedPayload = AppleJwsVerifier.ToBase64Url(JsonSerializer.SerializeToUtf8Bytes(Tx(id: "2000000999")));
        var tampered = parts[0] + "." + tamperedPayload + "." + parts[2];
        Assert.Equal(VerificationStatus.Rejected, (await Make(chain, _ => Ok(tampered)).Verifier.VerifyAsync(new PurchaseClaim(PurchaseStores.AppStore, "x", "2000000123"))).Status);

        var otherChain = MakeChain();
        Assert.Equal(VerificationStatus.Rejected, (await Make(chain, _ => Ok(Sign(otherChain, Tx()))).Verifier.VerifyAsync(new PurchaseClaim(PurchaseStores.AppStore, "x", "2000000123"))).Status);

        var unmarked = MakeChain(markers: false);
        Assert.Equal(VerificationStatus.Rejected, (await Make(unmarked, _ => Ok(Sign(unmarked, Tx()))).Verifier.VerifyAsync(new PurchaseClaim(PurchaseStores.AppStore, "x", "2000000123"))).Status);
    }

    [Fact]
    public async Task Wrong_app_non_consumable_and_revoked_transactions_are_rejected()
    {
        var chain = MakeChain();
        foreach (var payload in new[] { Tx(bundle: "com.other.app"), Tx(type: "Non-Consumable"), Tx(revoked: 1767225700000L) })
        {
            var r = await Make(chain, _ => Ok(Sign(chain, payload))).Verifier.VerifyAsync(new PurchaseClaim(PurchaseStores.AppStore, "x", "2000000123"));
            Assert.Equal(VerificationStatus.Rejected, r.Status);
        }
    }

    [Fact]
    public async Task Production_404_falls_back_to_sandbox_and_unknown_is_rejected()
    {
        var chain = MakeChain();
        var (v, handler) = Make(chain, req => req.RequestUri!.Host.Contains("sandbox") ? Ok(Sign(chain, Tx())) : new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Equal(VerificationStatus.Verified, (await v.VerifyAsync(new PurchaseClaim(PurchaseStores.AppStore, "x", "2000000123"))).Status);
        Assert.Equal(2, handler.Requests.Count);

        var (v2, _) = Make(chain, _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Equal(VerificationStatus.Rejected, (await v2.VerifyAsync(new PurchaseClaim(PurchaseStores.AppStore, "x", "2000000123"))).Status);
    }

    [Fact]
    public async Task Store_outage_is_pending_not_rejected_and_bad_ids_never_reach_apple()
    {
        var chain = MakeChain();
        var (v, handler) = Make(chain, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        Assert.Equal(VerificationStatus.Unavailable, (await v.VerifyAsync(new PurchaseClaim(PurchaseStores.AppStore, "x", "2000000123"))).Status);
        var before = handler.Requests.Count;
        Assert.Equal(VerificationStatus.Rejected, (await v.VerifyAsync(new PurchaseClaim(PurchaseStores.AppStore, "x", "../../evil"))).Status);
        Assert.Equal(before, handler.Requests.Count);
    }

    [Fact]
    public async Task Unconfigured_verifier_reports_pending()
    {
        var v = new AppStoreVerifier(new HttpClient(), new AppleStoreOptions(), TimeProvider.System, NullLogger<AppStoreVerifier>.Instance, Array.Empty<X509Certificate2>(), null);
        Assert.False(v.IsConfigured);
        Assert.Equal(VerificationStatus.Unavailable, (await v.VerifyAsync(new PurchaseClaim(PurchaseStores.AppStore, "x", "1"))).Status);
    }
}
