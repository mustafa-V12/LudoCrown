using System.Security.Cryptography;
using System.Text.Json;
using LudoCrown.Core.Engine;
using LudoCrown.Core.Protocol;
using LudoCrown.Server.Data;
using Microsoft.Extensions.Options;

namespace LudoCrown.Server.Store;

public enum PurchaseOutcome
{
    /// <summary>Verified and credited now.</summary>
    Credited,
    /// <summary>This transaction was already credited to this account (retry / restore). Nothing new was added.</summary>
    AlreadyCredited,
    /// <summary>Not verified yet (store unreachable, not configured, or "Ask to Buy"). Nothing credited; the app retries later.</summary>
    Pending,
    /// <summary>Invalid, revoked, for another app, or owned by another account. Nothing credited.</summary>
    Rejected,
}

public sealed record PurchaseProcessResult(PurchaseOutcome Outcome, string Message, Balances? Balances, IReadOnlyList<AssetChange> Granted);

public enum BuyOutcome { Bought, AlreadyBought, NotEnoughCoins, AlreadyOwned, UnknownProduct, NotForGold }

public sealed record BuyResult(BuyOutcome Outcome, string Message, Balances? Balances);

/// <summary>
/// The only code that turns purchases into balances. Flow for a real-money purchase:
///   1. The app completes a StoreKit transaction and sends its id here (it does NOT finish the transaction yet).
///   2. The store verifier confirms it with Apple (or the dev sandbox in Development).
///   3. The transaction is recorded once per (store, transaction id); a second account can never claim it.
///   4. Contents are credited through an idempotent economy operation keyed by the transaction.
///   5. Only after a Credited/AlreadyCredited answer does the app finish the StoreKit transaction.
/// A crash between steps is harmless: every step is idempotent and a retry continues where it stopped.
/// </summary>
public sealed class PurchaseService(
    IEnumerable<IPurchaseVerifier> verifiers,
    IPurchaseStore purchases,
    IEconomyStore economy,
    IOptions<StoreOptions> options,
    TimeProvider time,
    ILogger<PurchaseService> logger)
{
    private StoreOptions O => options.Value;

    public IReadOnlyList<string> EnabledStores => verifiers.Select(v => v.Store).ToList();

    public async Task<PurchaseProcessResult> ProcessAsync(Guid userId, PurchaseClaim claim, CancellationToken ct = default)
    {
        var verifier = verifiers.FirstOrDefault(v => v.Store == claim.Store);
        if (verifier is null) return Fail(PurchaseOutcome.Rejected, "This store is not available on this server.");
        if (string.IsNullOrWhiteSpace(claim.TransactionId) || claim.TransactionId.Length > 80) return Fail(PurchaseOutcome.Rejected, "Invalid transaction.");

        var claimedProduct = ResolveProduct(claim.Store, claim.ProductId);
        if (claimedProduct is null) return Fail(PurchaseOutcome.Rejected, "Unknown product.");

        var existing = await purchases.GetPurchaseAsync(claim.Store, claim.TransactionId, ct);
        if (existing is not null)
        {
            if (existing.UserId != userId) return Fail(PurchaseOutcome.Rejected, "This purchase belongs to another account.");
            switch (existing.Status)
            {
                case PurchaseStatus.Credited:
                    return new PurchaseProcessResult(PurchaseOutcome.AlreadyCredited, "Already added to your account.", await economy.GetBalancesAsync(userId, ct), Array.Empty<AssetChange>());
                case PurchaseStatus.Rejected:
                case PurchaseStatus.Refunded:
                    return Fail(PurchaseOutcome.Rejected, existing.FailureReason ?? "This purchase was not accepted.");
                case PurchaseStatus.Verified:
                    return await CreditAsync(userId, existing, ct); // a previous attempt stopped before crediting
            }
            // Pending: verify again below.
        }

        var v = await verifier.VerifyAsync(claim, ct);
        var now = time.GetUtcNow();
        if (v.Status == VerificationStatus.Unavailable)
        {
            if (existing is null)
                await purchases.CreatePurchaseAsync(NewRecord(userId, claim, claimedProduct.Id, PurchaseStatus.Pending, v, now), ct);
            return Fail(PurchaseOutcome.Pending, v.Reason ?? "Waiting for the store to confirm your purchase.");
        }

        if (v.Status == VerificationStatus.Verified)
        {
            var verifiedProduct = ResolveProduct(claim.Store, v.ProductId);
            string? problem = null;
            if (verifiedProduct is null || verifiedProduct.Id != claimedProduct.Id) problem = "The store reports a different product.";
            else if (v.TransactionId is not null && v.TransactionId != claim.TransactionId) problem = "Transaction mismatch.";
            else if (claim.Store == PurchaseStores.AppStore)
            {
                if (v.AppAccountToken is Guid token ? token != AppAccountTokens.For(userId) : O.Apple.RequireAppAccountToken)
                    problem = "This purchase was made from a different account.";
            }
            if (problem is not null) v = PurchaseVerification.Rejected(problem);
        }

        if (v.Status == VerificationStatus.Rejected)
        {
            logger.LogWarning("Purchase {Store}/{Tx} rejected: {Reason}", claim.Store, claim.TransactionId, v.Reason);
            if (existing is null) await purchases.CreatePurchaseAsync(NewRecord(userId, claim, claimedProduct.Id, PurchaseStatus.Rejected, v, now), ct);
            else await purchases.UpdatePurchaseAsync(existing.Id, PurchaseStatus.Rejected, v.Reason, now, ct);
            return Fail(PurchaseOutcome.Rejected, v.Reason ?? "The purchase could not be verified.");
        }

        PurchaseRecord record;
        if (existing is null)
        {
            var (stored, created) = await purchases.CreatePurchaseAsync(NewRecord(userId, claim, claimedProduct.Id, PurchaseStatus.Verified, v, now), ct);
            if (!created)
                // Another request for the same transaction got there first: answer from its state.
                return stored.UserId != userId ? Fail(PurchaseOutcome.Rejected, "This purchase belongs to another account.") : await CreditAsync(userId, stored, ct);
            record = stored;
        }
        else
        {
            await purchases.UpdatePurchaseAsync(existing.Id, PurchaseStatus.Verified, null, now, ct);
            record = existing with { Status = PurchaseStatus.Verified, Quantity = v.Quantity, Environment = v.Environment };
        }
        return await CreditAsync(userId, record, ct);
    }

    private async Task<PurchaseProcessResult> CreditAsync(Guid userId, PurchaseRecord record, CancellationToken ct)
    {
        var product = StoreCatalog.Get(record.ProductId)!;
        var contents = StoreCatalog.ContentsTimes(product, Math.Max(1, record.Quantity));
        var op = new EconomyOperation(userId, $"purchase:{record.Store}:{record.TransactionId}", Economy.Reasons.Purchase, contents, time.GetUtcNow(), PurchaseId: record.Id);
        var result = await economy.ApplyAsync(op, ct);
        if (!result.Ok)
        {
            // Insufficient can only mean an item limit; UnknownUser means the account was deleted. Keep "verified" so support can resolve it.
            logger.LogError("Verified purchase {Store}/{Tx} could not be credited: {Outcome}", record.Store, record.TransactionId, result.Outcome);
            return Fail(PurchaseOutcome.Pending, "Your purchase is verified but could not be added yet. Please contact support if this persists.");
        }
        await purchases.UpdatePurchaseAsync(record.Id, PurchaseStatus.Credited, null, time.GetUtcNow(), ct);
        return result.Outcome == EconomyOutcome.Applied
            ? new PurchaseProcessResult(PurchaseOutcome.Credited, $"{product.Title} added to your account.", result.Balances, contents)
            : new PurchaseProcessResult(PurchaseOutcome.AlreadyCredited, "Already added to your account.", result.Balances, Array.Empty<AssetChange>());
    }

    /// <summary>Gold purchases (Shield bottles, cosmetics). Idempotent per request id; cosmetics can be owned once.</summary>
    public async Task<BuyResult> BuyWithCoinsAsync(Guid userId, string productId, string requestId, CancellationToken ct = default)
    {
        if (CommandValidator.ValidateRequestId(requestId) != ErrorCode.None) return new BuyResult(BuyOutcome.UnknownProduct, "Invalid request.", null);
        var product = StoreCatalog.Get(productId);
        if (product is null) return new BuyResult(BuyOutcome.UnknownProduct, "Unknown item.", null);
        if (product.Kind != ProductKind.GoldPurchase || product.CoinPrice is not int price) return new BuyResult(BuyOutcome.NotForGold, "This item is not sold for gold.", null);

        var key = $"buy:{requestId}";
        var balances = await economy.GetBalancesAsync(userId, ct);
        if (balances is null) return new BuyResult(BuyOutcome.UnknownProduct, "Unknown account.", null);
        if (product.IsCosmetic && balances.Count(product.Contents[0].Asset) > 0)
            return new BuyResult(BuyOutcome.AlreadyOwned, "You already own this.", balances);

        var changes = product.Contents.Append(new AssetChange(Economy.Coins, -price)).ToList();
        var result = await economy.ApplyAsync(new EconomyOperation(userId, key, Economy.Reasons.StoreBuy, changes, time.GetUtcNow()), ct);
        return result.Outcome switch
        {
            EconomyOutcome.Applied => new BuyResult(BuyOutcome.Bought, $"{product.Title} bought.", result.Balances),
            EconomyOutcome.AlreadyApplied => new BuyResult(BuyOutcome.AlreadyBought, $"{product.Title} bought.", result.Balances),
            EconomyOutcome.Insufficient when product.IsCosmetic && result.Balances.Count(product.Contents[0].Asset) > 0
                => new BuyResult(BuyOutcome.AlreadyOwned, "You already own this.", result.Balances),
            EconomyOutcome.Insufficient => new BuyResult(BuyOutcome.NotEnoughCoins, $"You need {price:N0} gold.", result.Balances),
            _ => new BuyResult(BuyOutcome.UnknownProduct, "Unknown account.", null),
        };
    }

    /// <summary>
    /// App Store Server Notification V2 (verified JWS). On REFUND the purchase is marked refunded and the credited contents are
    /// removed as far as the balances allow (never below zero). Returns false when the payload is not valid.
    /// </summary>
    public async Task<bool> HandleAppStoreNotificationAsync(string signedPayload, CancellationToken ct = default)
    {
        var apple = verifiers.OfType<AppStoreVerifier>().FirstOrDefault();
        if (apple?.Jws is null || !apple.IsConfigured) return false;
        JsonElement payload;
        try { payload = apple.Jws.Verify(signedPayload); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning("Rejected App Store notification: {Message}", ex.Message);
            return false;
        }

        var type = payload.TryGetProperty("notificationType", out var t) ? t.GetString() : null;
        if (type is not ("REFUND" or "REVOKE")) return true; // acknowledged, nothing to do
        if (!payload.TryGetProperty("data", out var data) || !data.TryGetProperty("signedTransactionInfo", out var sti) || sti.GetString() is not string signedTx) return false;

        PurchaseVerification tx;
        try
        {
            var txPayload = apple.Jws.Verify(signedTx);
            tx = new PurchaseVerification(VerificationStatus.Verified, TransactionId: txPayload.TryGetProperty("transactionId", out var id) ? id.GetString() : null);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException) { return false; }
        if (tx.TransactionId is null) return false;

        var record = await purchases.GetPurchaseAsync(PurchaseStores.AppStore, tx.TransactionId, ct);
        if (record is null || record.Status == PurchaseStatus.Refunded) return true;
        await purchases.UpdatePurchaseAsync(record.Id, PurchaseStatus.Refunded, "Refunded by Apple.", time.GetUtcNow(), ct);
        if (record.UserId is Guid user && record.CreditedAt is not null && await economy.GetBalancesAsync(user, ct) is Balances b)
        {
            var product = StoreCatalog.Get(record.ProductId);
            if (product is not null)
            {
                var clawback = StoreCatalog.ContentsTimes(product, record.Quantity)
                    .Select(c => new AssetChange(c.Asset, -Math.Min(c.Delta, c.Asset switch { Economy.Coins => b.Coins, Economy.Gems => b.Gems, _ => b.Count(c.Asset) })))
                    .ToList();
                await economy.ApplyAsync(new EconomyOperation(user, $"refund:{record.Store}:{record.TransactionId}", Economy.Reasons.Refund, clawback, time.GetUtcNow(), PurchaseId: record.Id), ct);
            }
        }
        logger.LogInformation("Purchase {Tx} refunded", tx.TransactionId);
        return true;
    }

    private StoreProduct? ResolveProduct(string store, string? productId)
    {
        if (productId is null) return null;
        var p = store == PurchaseStores.AppStore ? StoreCatalog.FromAppStoreProductId(O.ProductIdPrefix, productId) : StoreCatalog.Get(productId);
        return p is { Kind: ProductKind.InAppPurchase } ? p : null;
    }

    private static PurchaseRecord NewRecord(Guid userId, PurchaseClaim claim, string catalogId, PurchaseStatus status, PurchaseVerification v, DateTimeOffset now) =>
        new(Guid.NewGuid(), userId, claim.Store, catalogId, claim.TransactionId, v.OriginalTransactionId, EnvironmentFor(claim.Store, v), status, Math.Max(1, v.Quantity),
            status is PurchaseStatus.Rejected or PurchaseStatus.Pending ? v.Reason : null, v.PurchasedAt, now);

    /// <summary>
    /// Sandbox transactions are always "development". For App Store claims that could not be verified (pending/rejected), the
    /// store has not told us its environment yet, so they are recorded as "production" until a verification says otherwise.
    /// </summary>
    private static string EnvironmentFor(string store, PurchaseVerification v) =>
        store == PurchaseStores.DevSandbox ? "development" : v.Status == VerificationStatus.Verified ? v.Environment : "production";

    private static PurchaseProcessResult Fail(PurchaseOutcome o, string message) => new(o, message, null, Array.Empty<AssetChange>());
}
