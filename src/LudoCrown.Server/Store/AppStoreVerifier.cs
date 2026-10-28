using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LudoCrown.Server.Store;

/// <summary>
/// Verifies Apple-signed JWS payloads (signed transactions and App Store Server Notifications V2): ES256 signature by the
/// leaf certificate in the x5c header, and a certificate chain that ends at a configured Apple root, with Apple's marker
/// extensions on the leaf and intermediate certificates.
/// </summary>
public sealed class AppleJwsVerifier(IReadOnlyList<X509Certificate2> trustedRoots, TimeProvider time)
{
    private const string LeafMarkerOid = "1.2.840.113635.100.6.11.1";
    private const string IntermediateMarkerOid = "1.2.840.113635.100.6.2.1";

    /// <summary>Returns the verified payload JSON, or throws <see cref="CryptographicException"/>.</summary>
    public JsonElement Verify(string jws)
    {
        var parts = jws.Split('.');
        if (parts.Length != 3) throw new CryptographicException("Malformed JWS.");
        using var header = JsonDocument.Parse(Base64Url(parts[0]));
        if (header.RootElement.GetProperty("alg").GetString() != "ES256") throw new CryptographicException("Unexpected JWS algorithm.");
        var x5c = header.RootElement.GetProperty("x5c").EnumerateArray().Select(e => Convert.FromBase64String(e.GetString()!)).ToList();
        if (x5c.Count < 2) throw new CryptographicException("Certificate chain missing.");

        using var leaf = X509CertificateLoader.LoadCertificate(x5c[0]);
        using var intermediate = X509CertificateLoader.LoadCertificate(x5c[1]);
        if (leaf.Extensions[LeafMarkerOid] is null) throw new CryptographicException("Leaf certificate is not an App Store signing certificate.");
        if (intermediate.Extensions[IntermediateMarkerOid] is null) throw new CryptographicException("Intermediate certificate is not an Apple WWDR certificate.");

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        foreach (var root in trustedRoots) chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.ExtraStore.Add(intermediate);
        // Revocation is not checked online here (no outbound OCSP dependency); refunds/revocations arrive via the
        // transaction's revocationDate and App Store Server Notifications.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = time.GetUtcNow().UtcDateTime;
        if (!chain.Build(leaf)) throw new CryptographicException("Untrusted certificate chain: " + string.Join(", ", chain.ChainStatus.Select(s => s.Status)));
        if (chain.ChainElements.Count < 3 || !trustedRoots.Any(r => r.Thumbprint == chain.ChainElements[^1].Certificate.Thumbprint))
            throw new CryptographicException("Chain does not end at a trusted Apple root.");

        using var key = leaf.GetECDsaPublicKey() ?? throw new CryptographicException("Leaf certificate has no EC key.");
        var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        if (!key.VerifyData(signed, Base64Url(parts[2]), HashAlgorithmName.SHA256)) throw new CryptographicException("Invalid JWS signature.");

        using var payload = JsonDocument.Parse(Base64Url(parts[1]));
        return payload.RootElement.Clone();
    }

    public static byte[] Base64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t.PadRight(t.Length + (4 - t.Length % 4) % 4, '='));
    }

    public static string ToBase64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Apple in-app purchase verification with the App Store Server API ("Get Transaction Info"), which is Apple's current
/// replacement for the deprecated verifyReceipt endpoint. The server authenticates with an ES256 JWT made from the
/// In-App Purchase key (configured as a secret), then verifies the returned signed transaction before trusting it.
/// </summary>
public sealed class AppStoreVerifier : IPurchaseVerifier
{
    public const string ProductionBaseUrl = "https://api.storekit.itunes.apple.com";
    public const string SandboxBaseUrl = "https://api.storekit-sandbox.itunes.apple.com";

    private readonly HttpClient _http;
    private readonly AppleStoreOptions _o;
    private readonly TimeProvider _time;
    private readonly ILogger<AppStoreVerifier> _logger;
    private readonly AppleJwsVerifier? _jws;
    private readonly ECDsa? _signingKey;

    public AppStoreVerifier(IHttpClientFactory http, IOptions<StoreOptions> options, TimeProvider time, ILogger<AppStoreVerifier> logger)
        : this(http.CreateClient("appstore"), options.Value.Apple, time, logger, LoadRoots(options.Value.Apple), LoadKey(options.Value.Apple)) { }

    public AppStoreVerifier(HttpClient http, AppleStoreOptions options, TimeProvider time, ILogger<AppStoreVerifier> logger,
        IReadOnlyList<X509Certificate2> roots, ECDsa? signingKey)
    {
        _http = http;
        _o = options;
        _time = time;
        _logger = logger;
        _signingKey = signingKey;
        _jws = roots.Count > 0 ? new AppleJwsVerifier(roots, time) : null;
    }

    public string Store => Data.PurchaseStores.AppStore;

    public bool IsConfigured => _o.IsConfigured && _jws is not null && _signingKey is not null;

    public AppleJwsVerifier? Jws => _jws;

    public async Task<PurchaseVerification> VerifyAsync(PurchaseClaim claim, CancellationToken ct = default)
    {
        if (!IsConfigured) return PurchaseVerification.Unavailable("App Store verification is not configured on this server.");
        if (string.IsNullOrEmpty(claim.TransactionId) || claim.TransactionId.Length > 64 || !claim.TransactionId.All(char.IsAsciiDigit))
            return PurchaseVerification.Rejected("Invalid transaction id.");

        var first = string.Equals(_o.Environment, "Sandbox", StringComparison.OrdinalIgnoreCase) ? SandboxBaseUrl : ProductionBaseUrl;
        var (status, signed) = await FetchAsync(first, claim.TransactionId, ct);
        if (status == HttpStatusCode.NotFound && first == ProductionBaseUrl && _o.AllowSandboxFallback)
            (status, signed) = await FetchAsync(SandboxBaseUrl, claim.TransactionId, ct);

        if (status == HttpStatusCode.NotFound) return PurchaseVerification.Rejected("The App Store does not know this transaction.");
        if (status != HttpStatusCode.OK || signed is null)
        {
            _logger.LogWarning("App Store Server API returned {Status} for a transaction lookup", (int)status);
            return PurchaseVerification.Unavailable("The App Store could not be reached. The purchase will be checked again.");
        }
        return VerifySignedTransaction(signed);
    }

    /// <summary>Checks a signedTransactionInfo JWS: signature, chain, app, product type and revocation.</summary>
    public PurchaseVerification VerifySignedTransaction(string signedTransaction)
    {
        JsonElement p;
        try { p = _jws!.Verify(signedTransaction); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning("Rejected App Store transaction signature: {Message}", ex.Message);
            return PurchaseVerification.Rejected("The transaction signature is not valid.");
        }

        string? Str(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        long? Num(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

        if (Str("bundleId") != _o.BundleId) return PurchaseVerification.Rejected("Transaction belongs to a different app.");
        if (Str("type") != "Consumable") return PurchaseVerification.Rejected("Only consumable products are sold.");
        if (Num("revocationDate") is not null) return PurchaseVerification.Rejected("The transaction was refunded or revoked.");
        var environment = Str("environment") == "Sandbox" ? "sandbox" : "production";
        return new PurchaseVerification(VerificationStatus.Verified, null, Str("productId"), Str("transactionId"), Str("originalTransactionId"), environment,
            Guid.TryParse(Str("appAccountToken"), out var token) ? token : null,
            Num("purchaseDate") is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null,
            (int)Math.Clamp(Num("quantity") ?? 1, 1, 10));
    }

    private async Task<(HttpStatusCode Status, string? Signed)> FetchAsync(string baseUrl, string transactionId, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/inApps/v1/transactions/{Uri.EscapeDataString(transactionId)}");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CreateApiToken());
            using var res = await _http.SendAsync(req, ct);
            if (res.StatusCode != HttpStatusCode.OK) return (res.StatusCode, null);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return (HttpStatusCode.OK, doc.RootElement.TryGetProperty("signedTransactionInfo", out var s) ? s.GetString() : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "App Store Server API request failed");
            return (HttpStatusCode.ServiceUnavailable, null);
        }
    }

    /// <summary>Short-lived ES256 JWT for the App Store Server API.</summary>
    public string CreateApiToken()
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var header = JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = _o.KeyId, typ = "JWT" });
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { iss = _o.IssuerId, iat = now, exp = now + 300, aud = "appstoreconnect-v1", bid = _o.BundleId });
        var signingInput = AppleJwsVerifier.ToBase64Url(header) + "." + AppleJwsVerifier.ToBase64Url(payload);
        var signature = _signingKey!.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256); // IEEE P1363 (r||s), as JWS requires
        return signingInput + "." + AppleJwsVerifier.ToBase64Url(signature);
    }

    private static IReadOnlyList<X509Certificate2> LoadRoots(AppleStoreOptions o) =>
        o.RootCertificatePaths.Where(File.Exists).Select(path => X509CertificateLoader.LoadCertificateFromFile(path)).ToList();

    private static ECDsa? LoadKey(AppleStoreOptions o)
    {
        var pem = !string.IsNullOrWhiteSpace(o.PrivateKeyPem) ? o.PrivateKeyPem
            : !string.IsNullOrWhiteSpace(o.PrivateKeyPath) && File.Exists(o.PrivateKeyPath) ? File.ReadAllText(o.PrivateKeyPath) : null;
        if (pem is null) return null;
        var key = ECDsa.Create();
        key.ImportFromPem(pem);
        return key;
    }
}
