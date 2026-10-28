using LudoCrown.Core.Model;
using LudoCrown.Server.Auth;
using LudoCrown.Server.Data;
using LudoCrown.Server.Matches;
using LudoCrown.Server.Store;
using Microsoft.Extensions.Options;

namespace LudoCrown.Server.Endpoints;

public sealed record ContentDto(string Asset, long Amount, string Label);
public sealed record ProductDto(string Id, string Kind, string Title, string Description, IReadOnlyList<ContentDto> Contents, int? CoinPrice, string? AppStoreProductId, bool Owned);
public sealed record CatalogResponse(IReadOnlyList<ProductDto> Products, IReadOnlyList<string> Stores, bool AppStoreConfigured, bool DevSandbox, bool PlaceholderProductIds, string Environment);
public sealed record PurchaseRequest(string Store, string ProductId, string TransactionId, string? Simulate);
public sealed record BalancesDto(long Coins, long Gems, IReadOnlyList<InventoryItemDto> Items);
public sealed record InventoryItemDto(string Id, string Name, string Description, string Kind, int Quantity);
public sealed record PurchaseResponse(string Status, string Message, BalancesDto? Balances, IReadOnlyList<ContentDto> Granted);
public sealed record BuyRequest(string ProductId, string RequestId);
public sealed record BuyResponse(string Status, string Message, BalancesDto? Balances);
public sealed record InventoryResponse(BalancesDto Balances, string? EquippedFrame);
public sealed record FrameRequest(string? FrameId);
public sealed record AppleNotificationRequest(string SignedPayload);
public sealed record DevEconomyGrantRequest(int Coins, int Gems, int Shields);

/// <summary>Store, purchases, inventory. Balances are only ever changed by the server; clients send intents and transaction ids.</summary>
public static class StoreEndpoints
{
    public static ContentDto Content(AssetChange c) => new(c.Asset, c.Delta, c.Asset switch
    {
        Economy.Coins => $"{c.Delta:N0} gold",
        Economy.Gems => $"{c.Delta:N0} gems",
        _ => $"{c.Delta}× {Items.Get(c.Asset)?.Name ?? c.Asset}",
    });

    public static BalancesDto ToDto(Balances b) => new(b.Coins, b.Gems, b.Items
        .Where(i => i.Value > 0)
        .Select(i => Items.Get(i.Key) is ItemDefinition d ? new InventoryItemDto(d.Id, d.Name, d.Description, d.Kind.ToString(), i.Value) : null)
        .Where(i => i is not null).Select(i => i!).OrderBy(i => i.Kind).ThenBy(i => i.Name).ToList());

    public static void MapStoreEndpoints(this WebApplication app)
    {
        var store = app.MapGroup("/store").RequireAuthorization().RequireRateLimiting("api");

        store.MapGet("/catalog", async (HttpContext http, IEconomyStore economy, PurchaseService purchases, IOptions<StoreOptions> o, IWebHostEnvironment env, CancellationToken ct) =>
        {
            var balances = await economy.GetBalancesAsync(http.User.UserId(), ct) ?? Balances.Empty;
            var prefix = o.Value.ProductIdPrefix;
            var apple = http.RequestServices.GetServices<IPurchaseVerifier>().OfType<AppStoreVerifier>().FirstOrDefault();
            var products = StoreCatalog.Products.Select(p => new ProductDto(p.Id, p.Kind == ProductKind.InAppPurchase ? "iap" : "gold", p.Title, p.Description,
                p.Contents.Select(Content).ToList(), p.CoinPrice,
                p.Kind == ProductKind.InAppPurchase ? StoreCatalog.AppStoreProductId(prefix, p) : null,
                p.IsCosmetic && balances.Count(p.Contents[0].Asset) > 0)).ToList();
            return Results.Ok(new CatalogResponse(products, purchases.EnabledStores, apple?.IsConfigured == true,
                purchases.EnabledStores.Contains(PurchaseStores.DevSandbox), prefix == StoreCatalog.PlaceholderPrefix, env.EnvironmentName));
        });

        // The token the app must attach to App Store payments (StoreKit applicationUsername). One-way derived from the account id.
        store.MapGet("/purchase-token", (HttpContext http) => Results.Ok(new { appAccountToken = AppAccountTokens.For(http.User.UserId()) }));

        store.MapPost("/purchases", async (PurchaseRequest req, HttpContext http, PurchaseService purchases, MatchService matches, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var result = await purchases.ProcessAsync(userId, new PurchaseClaim(req.Store ?? "", req.ProductId ?? "", req.TransactionId ?? "", req.Simulate), ct);
            if (result.Outcome == PurchaseOutcome.Credited) await matches.OnBalancesChangedAsync(userId);
            var body = new PurchaseResponse(result.Outcome switch
            {
                PurchaseOutcome.Credited => "credited",
                PurchaseOutcome.AlreadyCredited => "already_credited",
                PurchaseOutcome.Pending => "pending",
                _ => "rejected",
            }, result.Message, result.Balances is null ? null : ToDto(result.Balances), result.Granted.Select(Content).ToList());
            return result.Outcome switch
            {
                PurchaseOutcome.Credited or PurchaseOutcome.AlreadyCredited => Results.Ok(body),
                PurchaseOutcome.Pending => Results.Json(body, statusCode: StatusCodes.Status202Accepted),
                _ => Results.Json(body, statusCode: StatusCodes.Status422UnprocessableEntity),
            };
        });

        store.MapPost("/buy", async (BuyRequest req, HttpContext http, PurchaseService purchases, MatchService matches, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var result = await purchases.BuyWithCoinsAsync(userId, req.ProductId ?? "", req.RequestId ?? "", ct);
            if (result.Outcome == BuyOutcome.Bought) await matches.OnBalancesChangedAsync(userId);
            var body = new BuyResponse(result.Outcome switch
            {
                BuyOutcome.Bought or BuyOutcome.AlreadyBought => "bought",
                BuyOutcome.NotEnoughCoins => "not_enough_coins",
                BuyOutcome.AlreadyOwned => "already_owned",
                _ => "invalid",
            }, result.Message, result.Balances is null ? null : ToDto(result.Balances));
            return result.Outcome is BuyOutcome.Bought or BuyOutcome.AlreadyBought ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status409Conflict);
        });

        // App Store Server Notifications V2 (configure this URL in App Store Connect). Verified by Apple's JWS signature, not by user auth.
        app.MapPost("/store/apple/notifications", async (AppleNotificationRequest req, PurchaseService purchases, CancellationToken ct) =>
            await purchases.HandleAppStoreNotificationAsync(req.SignedPayload ?? "", ct) ? Results.Ok() : Results.BadRequest())
            .RequireRateLimiting("auth");

        var me = app.MapGroup("/me").RequireAuthorization().RequireRateLimiting("api");

        me.MapGet("/inventory", async (HttpContext http, IEconomyStore economy, IProfileStore profiles, CancellationToken ct) =>
        {
            var id = http.User.UserId();
            var balances = await economy.GetBalancesAsync(id, ct);
            if (balances is null) return Results.Unauthorized();
            return Results.Ok(new InventoryResponse(ToDto(balances), (await profiles.GetProfileAsync(id, ct))?.FrameId));
        });

        me.MapGet("/ledger", async (int? limit, HttpContext http, IEconomyStore economy, CancellationToken ct) =>
            Results.Ok(await economy.GetLedgerAsync(http.User.UserId(), Math.Clamp(limit ?? 50, 1, 200), ct)));

        me.MapPost("/frame", async (FrameRequest req, HttpContext http, IEconomyStore economy, IProfileStore profiles, CancellationToken ct) =>
        {
            var id = http.User.UserId();
            if (req.FrameId is not null)
            {
                if (!Items.IsFrame(req.FrameId)) return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "invalid_frame", "Unknown frame.");
                var balances = await economy.GetBalancesAsync(id, ct);
                if (balances is null || balances.Count(req.FrameId) < 1) return AuthEndpoints.Error(StatusCodes.Status403Forbidden, "not_owned", "You don't own this frame.");
            }
            await profiles.SetFrameAsync(id, req.FrameId, ct);
            return Results.Ok(new { frameId = req.FrameId });
        });
    }

    /// <summary>DEVELOPMENT ONLY economy tools; mapped from Program.cs only in Development with DevTools:AllowGemGrant=true.</summary>
    public static void MapDevStoreEndpoints(this WebApplication app)
    {
        app.MapPost("/dev/economy/grant", async (DevEconomyGrantRequest req, HttpContext http, IEconomyStore economy, MatchService matches, TimeProvider time, CancellationToken ct) =>
        {
            if (req.Coins is < 0 or > 100_000 || req.Gems is < 0 or > Economy.MaxDevGrant || req.Shields is < 0 or > 10)
                return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "invalid_amount", "Grant up to 100,000 gold, 100 gems and 10 Shield bottles.");
            var id = http.User.UserId();
            var result = await economy.ApplyAsync(new EconomyOperation(id, $"dev:{Guid.NewGuid():N}", Economy.Reasons.DevGrant, new[]
            {
                new AssetChange(Economy.Coins, req.Coins), new AssetChange(Economy.Gems, req.Gems), new AssetChange(Boosts.ShieldId, req.Shields),
            }, time.GetUtcNow()), ct);
            await matches.OnBalancesChangedAsync(id);
            return Results.Ok(ToDto(result.Balances));
        }).RequireAuthorization().RequireRateLimiting("api");
    }
}
