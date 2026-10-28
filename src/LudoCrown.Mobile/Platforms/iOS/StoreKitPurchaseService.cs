using Foundation;
using LudoCrown.App.Services;
using StoreKit;

namespace LudoCrown.Mobile.Platforms.iOS;

/// <summary>
/// Apple in-app purchases through StoreKit (SKPaymentQueue). StoreKit 2 is Swift-only, so .NET apps use this API; the
/// server verifies each transaction id with the App Store Server API, which works for StoreKit 1 transactions too.
///
/// Flow: PurchaseAsync adds a payment carrying the account's appAccountToken (applicationUsername) and waits for the
/// queue to report Purchased / Failed / Deferred. Purchased transactions are NOT finished here — the app finishes them
/// only after the server confirms the credit (<see cref="FinishAsync"/>). Transactions the queue still holds after a crash,
/// a network loss or an "Ask to Buy" approval are returned by <see cref="GetUnfinishedTransactionsAsync"/> for recovery.
/// Not verified on a device in this repository's environment: needs a Mac build and Apple sandbox testing (see docs/STORE.md).
/// </summary>
public sealed class StoreKitPurchaseService : IInAppPurchaseService
{
    private readonly Observer _observer = new();
    private readonly Dictionary<string, SKProduct> _products = new();
    private bool _registered;

    public bool IsSupported => SKPaymentQueue.CanMakePayments;

    private void EnsureObserver()
    {
        if (_registered) return;
        SKPaymentQueue.DefaultQueue.AddTransactionObserver(_observer);
        _registered = true;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetLocalizedPricesAsync(IReadOnlyList<string> productIds)
    {
        EnsureObserver();
        if (productIds.Count == 0) return new Dictionary<string, string>();
        var tcs = new TaskCompletionSource<SKProductsResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ids = new NSSet<NSString>(productIds.Select(id => new NSString(id)).ToArray());
        var request = new SKProductsRequest(ids) { Delegate = new ProductsDelegate(tcs) };
        request.Start();
        var response = await tcs.Task;
        var prices = new Dictionary<string, string>();
        if (response is null) return prices;
        foreach (var product in response.Products)
        {
            _products[product.ProductIdentifier] = product;
            var formatter = new NSNumberFormatter { NumberStyle = NSNumberFormatterStyle.Currency, Locale = product.PriceLocale };
            prices[product.ProductIdentifier] = formatter.StringFromNumber(product.Price) ?? product.Price.ToString();
        }
        return prices;
    }

    public async Task<StorePurchaseOutcome> PurchaseAsync(string productId, Guid appAccountToken)
    {
        EnsureObserver();
        if (!SKPaymentQueue.CanMakePayments) return new StorePurchaseOutcome(StorePurchaseStatus.Failed, Message: "Purchases are turned off on this device (Screen Time restrictions).");
        if (!_products.TryGetValue(productId, out var product))
        {
            await GetLocalizedPricesAsync(new[] { productId });
            if (!_products.TryGetValue(productId, out product))
                return new StorePurchaseOutcome(StorePurchaseStatus.Failed, Message: "This item isn't available in the App Store right now.");
        }
        var payment = SKMutablePayment.PaymentWithProduct(product);
        // Lets the server check that the transaction belongs to this account (appears as appAccountToken).
        payment.ApplicationUsername = appAccountToken.ToString();
        var wait = _observer.Expect(productId);
        SKPaymentQueue.DefaultQueue.AddPayment(payment);
        return await wait;
    }

    public Task<IReadOnlyList<UnfinishedTransaction>> GetUnfinishedTransactionsAsync()
    {
        EnsureObserver();
        IReadOnlyList<UnfinishedTransaction> list = SKPaymentQueue.DefaultQueue.Transactions
            .Where(t => t.TransactionState == SKPaymentTransactionState.Purchased && t.TransactionIdentifier is not null)
            .Select(t => new UnfinishedTransaction(t.TransactionIdentifier!, t.Payment.ProductIdentifier))
            .ToList();
        return Task.FromResult(list);
    }

    public Task FinishAsync(string transactionId)
    {
        foreach (var t in SKPaymentQueue.DefaultQueue.Transactions.Where(t => t.TransactionIdentifier == transactionId))
            SKPaymentQueue.DefaultQueue.FinishTransaction(t);
        return Task.CompletedTask;
    }

    private sealed class ProductsDelegate(TaskCompletionSource<SKProductsResponse?> tcs) : SKProductsRequestDelegate
    {
        public override void ReceivedResponse(SKProductsRequest request, SKProductsResponse response) => tcs.TrySetResult(response);
        public override void RequestFailed(SKRequest request, NSError error) => tcs.TrySetResult(null);
    }

    private sealed class Observer : SKPaymentTransactionObserver
    {
        private readonly Dictionary<string, TaskCompletionSource<StorePurchaseOutcome>> _waiting = new();

        public Task<StorePurchaseOutcome> Expect(string productId)
        {
            var tcs = new TaskCompletionSource<StorePurchaseOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waiting) _waiting[productId] = tcs;
            return tcs.Task;
        }

        public override void UpdatedTransactions(SKPaymentQueue queue, SKPaymentTransaction[] transactions)
        {
            foreach (var t in transactions)
            {
                var productId = t.Payment.ProductIdentifier;
                StorePurchaseOutcome? outcome = t.TransactionState switch
                {
                    SKPaymentTransactionState.Purchased => new StorePurchaseOutcome(StorePurchaseStatus.Purchased, t.TransactionIdentifier, productId),
                    SKPaymentTransactionState.Deferred => new StorePurchaseOutcome(StorePurchaseStatus.Deferred, null, productId),
                    SKPaymentTransactionState.Failed => (long)(t.Error?.Code ?? 0) == (long)SKError.PaymentCancelled
                        ? new StorePurchaseOutcome(StorePurchaseStatus.Cancelled, null, productId)
                        : new StorePurchaseOutcome(StorePurchaseStatus.Failed, null, productId, t.Error?.LocalizedDescription),
                    _ => null, // Purchasing: wait. Restored: consumables are never restored.
                };
                if (outcome is null) continue;
                // Failed transactions are finished right away (nothing was charged). Purchased ones wait for the server.
                if (t.TransactionState == SKPaymentTransactionState.Failed) queue.FinishTransaction(t);
                TaskCompletionSource<StorePurchaseOutcome>? waiter;
                lock (_waiting) { _waiting.Remove(productId, out waiter); }
                waiter?.TrySetResult(outcome);
            }
        }
    }
}
