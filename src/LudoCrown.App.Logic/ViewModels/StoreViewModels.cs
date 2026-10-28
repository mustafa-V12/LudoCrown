using System.Collections.ObjectModel;
using System.Windows.Input;
using LudoCrown.App.Api;
using LudoCrown.App.Mvvm;
using LudoCrown.App.Services;
using LudoCrown.Core.Model;

namespace LudoCrown.App.ViewModels;

public enum StoreItemState { Idle, Working, Pending, Done, Failed }

/// <summary>One product card in the store.</summary>
public sealed class StoreItemView : ObservableObject
{
    private string _priceText = "";
    private string _statusText = "";
    private StoreItemState _state;
    private bool _owned;

    public StoreItemView(ProductDto p)
    {
        Product = p;
        _owned = p.Owned;
    }

    public ProductDto Product { get; }
    public string Id => Product.Id;
    public string Title => Product.Title;
    public string Description => Product.Description;
    public string ContentsText => string.Join(" + ", Product.Contents.Select(c => c.Label));
    public bool IsRealMoney => Product.Kind == "iap";
    public bool IsCosmetic => Product.Contents.Count == 1 && Product.Contents[0].Asset.StartsWith("frame_", StringComparison.Ordinal);
    public string PriceText { get => _priceText; set => Set(ref _priceText, value); }
    public string StatusText { get => _statusText; set { if (Set(ref _statusText, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => StatusText.Length > 0;
    public StoreItemState State { get => _state; set { if (Set(ref _state, value)) OnPropertyChanged(nameof(CanBuy)); } }
    public bool Owned { get => _owned; set { if (Set(ref _owned, value)) { OnPropertyChanged(nameof(CanBuy)); OnPropertyChanged(nameof(BuyText)); } } }
    public bool CanBuy => State != StoreItemState.Working && !Owned;
    public string BuyText => Owned ? "Owned" : IsRealMoney ? "Buy" : "Buy for gold";
}

/// <summary>
/// The store. Gold items are bought with an idempotent server request. Real-money packs go through the platform store
/// (StoreKit on iPhone); the server verifies every transaction with Apple before anything is credited, and the app finishes
/// the transaction only after the server confirms. Prices for real-money packs always come from the store, localised.
/// In development, a clearly labelled sandbox lets the whole flow (success, pending, failure, retry) run on Windows.
/// </summary>
public sealed class StoreViewModel : ViewModelBase
{
    private readonly ApiClient _api;
    private readonly IInAppPurchaseService _iap;
    private readonly IDialogService _dialogs;
    private readonly AppConfig _config;
    private CatalogResponse? _catalog;
    private BalancesDto? _balances;
    private string _notice = "";
    private readonly Dictionary<string, string> _pendingSandbox = new(); // productId -> sandbox transaction id awaiting verification

    public StoreViewModel(ApiClient api, IInAppPurchaseService iap, IDialogService dialogs, AppConfig config)
    {
        _api = api;
        _iap = iap;
        _dialogs = dialogs;
        _config = config;
        BuyCommand = new AsyncCommand(p => p is StoreItemView i ? BuyAsync(i) : Task.CompletedTask);
        RecoverCommand = new AsyncCommand(RecoverAsync);
        RefreshCommand = new AsyncCommand(LoadAsync);
    }

    public ObservableCollection<StoreItemView> Gems { get; } = new();
    public ObservableCollection<StoreItemView> Gold { get; } = new();
    public ObservableCollection<StoreItemView> Boosts { get; } = new();
    public ObservableCollection<StoreItemView> Cosmetics { get; } = new();
    public ICommand BuyCommand { get; }
    public ICommand RecoverCommand { get; }
    public ICommand RefreshCommand { get; }

    public string CoinsText => _balances is null ? "—" : $"{_balances.Coins:N0}";
    public string GemsText => _balances is null ? "—" : $"{_balances.Gems:N0}";
    public string ShieldsText => _balances is null ? "—" : _balances.Count(Core.Model.Boosts.ShieldId).ToString();
    public string Notice { get => _notice; private set { if (Set(ref _notice, value)) OnPropertyChanged(nameof(HasNotice)); } }
    public bool HasNotice => Notice.Length > 0;
    public bool IsSandbox => _catalog?.DevSandbox == true && !_iap.IsSupported;
    public string GemUseText => $"Gems pay for dice rerolls: {GameModes.RerollGemCost} gem each, up to {GameModes.Classic.MaxRerolls} per Classic match and {GameModes.Quick.MaxRerolls} per Quick match.";
    public string ShieldUseText => $"{Core.Model.Boosts.ShieldEffect} {Core.Model.Boosts.ShieldLimits}";
    public string GoldUseText => "Gold buys Shield bottles and cosmetic avatar frames. Cosmetics never affect gameplay.";
    public string FairnessText => "Every pack lists its exact contents. There are no random rewards. Purchases are added only after the server verifies them.";

    public override Task OnAppearingAsync() => LoadAsync();

    private Task LoadAsync() => RunBusyAsync(async () =>
    {
        var catalog = await _api.GetAsync<CatalogResponse>("/store/catalog");
        if (!catalog.Ok) { Error = catalog.Message; return; }
        _catalog = catalog.Value!;
        await RefreshBalancesAsync();

        foreach (var list in new[] { Gems, Gold, Boosts, Cosmetics }) list.Clear();
        var items = _catalog.Products.Select(p => new StoreItemView(p)).ToList();
        foreach (var i in items)
        {
            var target = i.IsCosmetic ? Cosmetics
                : i.Product.Contents.Any(c => c.Asset == Core.Model.Boosts.ShieldId) ? Boosts
                : i.Product.Contents.Any(c => c.Asset == "gems") ? Gems : Gold;
            target.Add(i);
            i.PriceText = i.IsRealMoney ? (IsSandbox ? "Sandbox · no charge" : "…") : $"{i.Product.CoinPrice:N0} gold";
        }

        if (_iap.IsSupported)
        {
            var ids = items.Where(i => i.IsRealMoney && i.Product.AppStoreProductId is not null).Select(i => i.Product.AppStoreProductId!).ToList();
            var prices = await _iap.GetLocalizedPricesAsync(ids);
            foreach (var i in items.Where(i => i.IsRealMoney))
                i.PriceText = i.Product.AppStoreProductId is string pid && prices.TryGetValue(pid, out var price) ? price : "Not available";
            Notice = prices.Count == 0
                ? "Store products aren't available yet. (App Store Connect products must be created and approved for this app.)"
                : "";
            await RecoverTransactionsAsync(quiet: true);
        }
        else
        {
            foreach (var i in items.Where(i => i.IsRealMoney && !IsSandbox)) i.PriceText = "iPhone only";
            Notice = IsSandbox
                ? "DEVELOPMENT SANDBOX — purchases here are simulated, free, and only exist on the development server."
                : "Real-money purchases are available in the iPhone app. Gold items can be bought here.";
        }
        OnPropertyChanged(nameof(IsSandbox));
    });

    private async Task RefreshBalancesAsync()
    {
        var inv = await _api.GetAsync<InventoryResponse>("/me/inventory");
        if (inv.Ok) SetBalances(inv.Value!.Balances);
    }

    private void SetBalances(BalancesDto? b)
    {
        if (b is null) return;
        _balances = b;
        OnPropertyChanged(nameof(CoinsText));
        OnPropertyChanged(nameof(GemsText));
        OnPropertyChanged(nameof(ShieldsText));
        foreach (var c in Cosmetics) c.Owned = b.Count(c.Product.Contents[0].Asset) > 0;
    }

    private async Task BuyAsync(StoreItemView item)
    {
        if (!item.CanBuy) return;
        if (!item.IsRealMoney) { await BuyWithGoldAsync(item); return; }
        if (_iap.IsSupported) { await BuyWithStoreKitAsync(item); return; }
        if (IsSandbox) { await BuyInSandboxAsync(item); return; }
        await _dialogs.AlertAsync("Not available here", "Real-money packs can be bought in the Ludo Crown iPhone app.");
    }

    private async Task BuyWithGoldAsync(StoreItemView item)
    {
        if (!await _dialogs.ConfirmAsync($"Buy {item.Title}?", $"{item.ContentsText} for {item.Product.CoinPrice:N0} gold.", "Buy", "Cancel")) return;
        item.State = StoreItemState.Working;
        item.StatusText = "Buying…";
        var r = await _api.PostAsync<BuyResponse>("/store/buy", new { productId = item.Id, requestId = Guid.NewGuid().ToString() }, auth: true);
        if (r.Ok)
        {
            SetBalances(r.Value!.Balances);
            item.State = StoreItemState.Done;
            item.StatusText = "✓ " + r.Value.Message;
            if (item.IsCosmetic) item.Owned = true;
        }
        else
        {
            item.State = StoreItemState.Failed;
            item.StatusText = r.Kind == ApiErrorKind.Network ? "Couldn't reach the server. Nothing was charged." : r.Message ?? "Purchase failed.";
            await RefreshBalancesAsync();
        }
    }

    private async Task BuyWithStoreKitAsync(StoreItemView item)
    {
        var productId = item.Product.AppStoreProductId!;
        var token = await _api.GetAsync<PurchaseTokenResponse>("/store/purchase-token");
        if (!token.Ok) { item.State = StoreItemState.Failed; item.StatusText = token.Message ?? "Couldn't start the purchase."; return; }

        item.State = StoreItemState.Working;
        item.StatusText = "Waiting for the App Store…";
        var outcome = await _iap.PurchaseAsync(productId, token.Value!.AppAccountToken);
        switch (outcome.Status)
        {
            case StorePurchaseStatus.Cancelled:
                item.State = StoreItemState.Idle;
                item.StatusText = "Cancelled. You weren't charged.";
                return;
            case StorePurchaseStatus.Deferred:
                item.State = StoreItemState.Pending;
                item.StatusText = "Waiting for approval (Ask to Buy). It will be added once approved.";
                return;
            case StorePurchaseStatus.Failed:
                item.State = StoreItemState.Failed;
                item.StatusText = outcome.Message ?? "The App Store couldn't complete the purchase.";
                return;
        }
        await VerifyAsync(item, PurchaseStoresApp, productId, outcome.TransactionId!, finish: true);
    }

    private const string PurchaseStoresApp = "app_store";
    private const string PurchaseStoresSandbox = "dev_sandbox";

    private async Task BuyInSandboxAsync(StoreItemView item)
    {
        var choice = await _dialogs.ActionSheetAsync($"Sandbox: {item.Title}", "Cancel", "Simulate success", "Simulate pending verification", "Simulate failure");
        string? simulate = choice switch
        {
            "Simulate success" => null,
            "Simulate pending verification" => "pending",
            "Simulate failure" => "fail",
            _ => "cancel",
        };
        if (simulate == "cancel") return;
        var tx = _pendingSandbox.TryGetValue(item.Id, out var existing) ? existing : "devtx-" + Guid.NewGuid();
        await VerifyAsync(item, PurchaseStoresSandbox, item.Id, tx, finish: false, simulate);
    }

    /// <summary>Asks the server to verify and credit. Only a credited answer counts as success.</summary>
    private async Task VerifyAsync(StoreItemView item, string store, string productId, string transactionId, bool finish, string? simulate = null)
    {
        item.State = StoreItemState.Working;
        item.StatusText = "Verifying purchase…";
        var r = await _api.PostAsync<PurchaseResponse>("/store/purchases", new { store, productId, transactionId, simulate }, auth: true);
        if (r.Ok && r.Value!.Status is "credited" or "already_credited")
        {
            if (finish) await _iap.FinishAsync(transactionId);
            _pendingSandbox.Remove(item.Id);
            SetBalances(r.Value.Balances);
            item.State = StoreItemState.Done;
            item.StatusText = "✓ " + r.Value.Message;
            return;
        }
        if (r.Ok && r.Value!.Status == "pending")
        {
            if (store == PurchaseStoresSandbox) _pendingSandbox[item.Id] = transactionId;
            item.State = StoreItemState.Pending;
            item.StatusText = "Payment received — waiting for verification. It will be added automatically; you can also tap \"Check pending purchases\".";
            return;
        }
        if (r.Kind == ApiErrorKind.Network)
        {
            if (store == PurchaseStoresSandbox) _pendingSandbox[item.Id] = transactionId;
            item.State = StoreItemState.Pending;
            item.StatusText = "Couldn't reach the server. Your purchase is kept and will be checked again.";
            return;
        }
        // Rejected: never shown as success. App Store transactions stay unfinished so support can recover a genuine purchase.
        _pendingSandbox.Remove(item.Id);
        item.State = StoreItemState.Failed;
        item.StatusText = r.Message ?? "The purchase couldn't be verified.";
    }

    /// <summary>Re-checks purchases that were paid but not yet credited (after a crash, network loss, or Ask to Buy approval).</summary>
    private Task RecoverAsync() => RunBusyAsync(() => RecoverTransactionsAsync(quiet: false));

    private async Task RecoverTransactionsAsync(bool quiet)
    {
        var found = 0;
        if (_iap.IsSupported)
        {
            foreach (var t in await _iap.GetUnfinishedTransactionsAsync())
            {
                found++;
                var item = Gems.Concat(Gold).Concat(Boosts).FirstOrDefault(i => i.Product.AppStoreProductId == t.ProductId);
                if (item is not null) await VerifyAsync(item, PurchaseStoresApp, t.ProductId, t.TransactionId, finish: true);
            }
        }
        foreach (var (productId, tx) in _pendingSandbox.ToList())
        {
            found++;
            var item = Gems.Concat(Gold).Concat(Boosts).FirstOrDefault(i => i.Id == productId);
            if (item is not null) await VerifyAsync(item, PurchaseStoresSandbox, productId, tx, finish: false);
        }
        if (!quiet && found == 0) await _dialogs.AlertAsync("No pending purchases", "Everything you bought has been added to your account.");
    }
}
