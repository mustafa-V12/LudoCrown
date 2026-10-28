using System.Security.Cryptography;
using System.Text;

namespace LudoCrown.Server.Store;

/// <summary>What the app reports after the store completes a transaction. Nothing here is trusted until verified.</summary>
public sealed record PurchaseClaim(string Store, string ProductId, string TransactionId, string? SimulateOutcome = null);

public enum VerificationStatus
{
    /// <summary>The store confirmed a valid, unrevoked transaction for this app.</summary>
    Verified,
    /// <summary>Definitively invalid (unknown transaction, wrong app, revoked, tampered...). Never credit.</summary>
    Rejected,
    /// <summary>Could not verify right now (store unreachable, not configured). Retry later; do not credit yet.</summary>
    Unavailable,
}

/// <summary>
/// Facts confirmed by the store. <see cref="ProductId"/> is the store's product id (for the App Store: "{prefix}.{catalogId}").
/// </summary>
public sealed record PurchaseVerification(VerificationStatus Status, string? Reason = null, string? ProductId = null, string? TransactionId = null,
    string? OriginalTransactionId = null, string Environment = "production", Guid? AppAccountToken = null, DateTimeOffset? PurchasedAt = null,
    int Quantity = 1)
{
    public static PurchaseVerification Rejected(string reason) => new(VerificationStatus.Rejected, reason);
    public static PurchaseVerification Unavailable(string reason) => new(VerificationStatus.Unavailable, reason);
}

/// <summary>Verifies transactions for one store. Implementations: <see cref="AppStoreVerifier"/>, <see cref="DevSandboxVerifier"/>.</summary>
public interface IPurchaseVerifier
{
    string Store { get; }
    Task<PurchaseVerification> VerifyAsync(PurchaseClaim claim, CancellationToken ct = default);
}

/// <summary>
/// The token the app attaches to every App Store purchase (StoreKit applicationUsername / appAccountToken). Derived from the
/// account id with a one-way hash so Apple never receives the internal id, yet the server can check the transaction belongs
/// to the account that submits it.
/// </summary>
public static class AppAccountTokens
{
    public static Guid For(Guid userId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("ludocrown-app-account:" + userId.ToString("N")));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50); // version 5-style
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes);
    }
}

/// <summary>
/// DEVELOPMENT ONLY fake store so the full purchase flow (pending, success, failure, duplicates) can be exercised on Windows
/// or in tests without Apple. Registered only when the server runs in Development with Store:DevSandbox=true. Transactions
/// are marked environment "development" in the database. It never runs in production.
/// </summary>
public sealed class DevSandboxVerifier : IPurchaseVerifier
{
    public string Store => Data.PurchaseStores.DevSandbox;

    public Task<PurchaseVerification> VerifyAsync(PurchaseClaim claim, CancellationToken ct = default)
    {
        if (!claim.TransactionId.StartsWith("devtx-", StringComparison.Ordinal) || !Guid.TryParse(claim.TransactionId["devtx-".Length..], out _))
            return Task.FromResult(PurchaseVerification.Rejected("Not a sandbox transaction."));
        return Task.FromResult(claim.SimulateOutcome switch
        {
            "fail" => PurchaseVerification.Rejected("Simulated store failure."),
            "pending" => PurchaseVerification.Unavailable("Simulated: store verification pending."),
            _ => new PurchaseVerification(VerificationStatus.Verified, null, claim.ProductId, claim.TransactionId, claim.TransactionId, "development", null, DateTimeOffset.UtcNow),
        });
    }
}
