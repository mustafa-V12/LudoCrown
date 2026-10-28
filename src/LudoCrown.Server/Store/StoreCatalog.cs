using LudoCrown.Core.Model;
using LudoCrown.Server.Data;

namespace LudoCrown.Server.Store;

public enum ProductKind
{
    /// <summary>Real-money consumable sold through Apple in-app purchase (price comes from the App Store, localised).</summary>
    InAppPurchase,
    /// <summary>Bought with gold coins inside the game.</summary>
    GoldPurchase,
}

/// <summary>A store entry with exact, fixed contents (no randomness).</summary>
public sealed record StoreProduct(string Id, ProductKind Kind, string Title, string Description, IReadOnlyList<AssetChange> Contents, int? CoinPrice = null)
{
    public bool IsCosmetic => Contents.Count == 1 && Items.IsFrame(Contents[0].Asset);
}

/// <summary>
/// The store catalogue, defined in server code and versioned with deployments (the server is the source of truth; the app
/// fetches it from GET /store/catalog). App Store product identifiers are built as "{Store:ProductIdPrefix}.{Id}"; the prefix
/// defaults to a placeholder and must be replaced with identifiers created in App Store Connect (see docs/STORE.md).
/// Real-money prices are never hard-coded: the app shows the localised price StoreKit returns.
/// </summary>
public static class StoreCatalog
{
    public const string PlaceholderPrefix = "PLACEHOLDER.BUNDLE.ID";

    public static readonly IReadOnlyList<StoreProduct> Products = new StoreProduct[]
    {
        // --- real-money packs (consumables) ---
        new("gems_small", ProductKind.InAppPurchase, "Pouch of Gems", "50 gems for dice rerolls.", new[] { new AssetChange(Economy.Gems, 50) }),
        new("gems_medium", ProductKind.InAppPurchase, "Chest of Gems", "150 gems for dice rerolls.", new[] { new AssetChange(Economy.Gems, 150) }),
        new("gems_large", ProductKind.InAppPurchase, "Vault of Gems", "400 gems for dice rerolls.", new[] { new AssetChange(Economy.Gems, 400) }),
        new("coins_small", ProductKind.InAppPurchase, "Bag of Gold", "5,000 gold coins.", new[] { new AssetChange(Economy.Coins, 5000) }),
        new("coins_large", ProductKind.InAppPurchase, "Treasury of Gold", "20,000 gold coins.", new[] { new AssetChange(Economy.Coins, 20000) }),
        new("shield_pack", ProductKind.InAppPurchase, "Shield Bottle ×3", "3 Shield bottles for casual matches.", new[] { new AssetChange(Boosts.ShieldId, 3) }),

        // --- gold purchases ---
        new("shield_single", ProductKind.GoldPurchase, "Shield Bottle", Boosts.ShieldEffect + " " + Boosts.ShieldLimits, new[] { new AssetChange(Boosts.ShieldId, 1) }, 2000),
        new("frame_gilded", ProductKind.GoldPurchase, Items.FrameGilded.Name, Items.FrameGilded.Description, new[] { new AssetChange(Items.FrameGilded.Id, 1) }, 5000),
        new("frame_emerald", ProductKind.GoldPurchase, Items.FrameEmerald.Name, Items.FrameEmerald.Description, new[] { new AssetChange(Items.FrameEmerald.Id, 1) }, 8000),
        new("frame_royal", ProductKind.GoldPurchase, Items.FrameRoyal.Name, Items.FrameRoyal.Description, new[] { new AssetChange(Items.FrameRoyal.Id, 1) }, 15000),
    };

    public static StoreProduct? Get(string? id) => Products.FirstOrDefault(p => p.Id == id);

    public static string AppStoreProductId(string prefix, StoreProduct p) => $"{prefix}.{p.Id}";

    /// <summary>Maps an App Store product identifier back to the catalogue entry.</summary>
    public static StoreProduct? FromAppStoreProductId(string prefix, string? appStoreProductId) =>
        Products.FirstOrDefault(p => p.Kind == ProductKind.InAppPurchase && AppStoreProductId(prefix, p) == appStoreProductId);

    /// <summary>The contents multiplied by a purchase quantity.</summary>
    public static IReadOnlyList<AssetChange> ContentsTimes(StoreProduct p, int quantity) =>
        p.Contents.Select(c => c with { Delta = c.Delta * quantity }).ToList();
}

public sealed class StoreOptions
{
    /// <summary>Prefix of App Store product ids, normally your bundle id. Placeholder until configured.</summary>
    public string ProductIdPrefix { get; set; } = StoreCatalog.PlaceholderPrefix;
    /// <summary>DEVELOPMENT ONLY: enables the fake "dev_sandbox" store so purchases can be tested without Apple. Ignored outside Development.</summary>
    public bool DevSandbox { get; set; }
    public AppleStoreOptions Apple { get; set; } = new();
}

/// <summary>App Store Server API credentials. All values come from configuration/secrets — never from source control.</summary>
public sealed class AppleStoreOptions
{
    public string? BundleId { get; set; }
    public string? IssuerId { get; set; }
    public string? KeyId { get; set; }
    /// <summary>Path to the .p8 In-App Purchase key file (or set <see cref="PrivateKeyPem"/> from a secret store).</summary>
    public string? PrivateKeyPath { get; set; }
    public string? PrivateKeyPem { get; set; }
    /// <summary>Apple root certificate(s) (DER .cer) used to verify signed transactions, e.g. AppleRootCA-G3.cer from apple.com/certificateauthority.</summary>
    public string[] RootCertificatePaths { get; set; } = Array.Empty<string>();
    /// <summary>"Production" or "Sandbox": which App Store Server API to call first.</summary>
    public string Environment { get; set; } = "Production";
    /// <summary>When the production API does not know a transaction, retry against the sandbox (needed for TestFlight and App Review).</summary>
    public bool AllowSandboxFallback { get; set; } = true;
    /// <summary>Reject transactions that do not carry the account token the app attached (prevents crediting someone else's transaction id).</summary>
    public bool RequireAppAccountToken { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BundleId) && !string.IsNullOrWhiteSpace(IssuerId) && !string.IsNullOrWhiteSpace(KeyId)
                                && (!string.IsNullOrWhiteSpace(PrivateKeyPath) || !string.IsNullOrWhiteSpace(PrivateKeyPem)) && RootCertificatePaths.Length > 0;
}
