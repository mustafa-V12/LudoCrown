using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using LudoCrown.Server.Data;
using LudoCrown.Server.Options;
using Microsoft.Extensions.Options;

namespace LudoCrown.Server.Auth;

public interface IEmailSender
{
    Task SendSignInCodeAsync(string email, string code, CancellationToken ct);
}

/// <summary>DEVELOPMENT ONLY: writes sign-in codes to the server log instead of sending mail.</summary>
public sealed class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendSignInCodeAsync(string email, string code, CancellationToken ct)
    {
        logger.LogWarning("DEV EMAIL to {Email}: your Ludo Crown sign-in code is {Code}", email, code);
        return Task.CompletedTask;
    }
}

/// <summary>Sends sign-in codes over SMTP (configure Auth:Email:Smtp*). Use a transactional mail provider.</summary>
public sealed class SmtpEmailSender(IOptions<AuthOptions> options) : IEmailSender
{
    public async Task SendSignInCodeAsync(string email, string code, CancellationToken ct)
    {
        var o = options.Value.Email;
        using var client = new SmtpClient(o.SmtpHost, o.SmtpPort)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(o.SmtpUser, o.SmtpPassword),
        };
        using var message = new MailMessage(o.FromAddress, email)
        {
            Subject = "Your Ludo Crown sign-in code",
            Body = $"Your Ludo Crown sign-in code is {code}.\n\nIt expires in {o.CodeMinutes} minutes. If you did not ask for it, you can ignore this email.",
        };
        await client.SendMailAsync(message, ct);
    }
}

public enum EmailCodeResult { Sent, InvalidEmail, TooSoon, Disabled }
public enum EmailVerifyResult { Ok, Invalid, TooManyAttempts }

/// <summary>
/// Passwordless email sign-in: a 6-digit code, valid for a few minutes, limited attempts.
/// No passwords are ever stored; codes are stored only as keyed hashes bound to the address.
/// </summary>
public sealed class EmailCodeService(IAccountStore store, IEmailSender sender, IOptions<AuthOptions> options, IOptions<JwtOptions> jwt, TimeProvider time)
{
    private readonly byte[] _key = HMACSHA256.HashData(Convert.FromBase64String(jwt.Value.SigningKey!), "ludocrown-email-codes"u8.ToArray());

    public async Task<EmailCodeResult> StartAsync(string? rawEmail, CancellationToken ct = default)
    {
        var o = options.Value.Email;
        if (!o.Enabled) return EmailCodeResult.Disabled;
        var email = Normalize(rawEmail);
        if (email is null) return EmailCodeResult.InvalidEmail;

        var now = time.GetUtcNow();
        var live = await store.FindLiveAuthCodeAsync(AuthCodeKinds.EmailOtp, email, now, ct);
        if (live is not null && live.ExpiresAt - TimeSpan.FromMinutes(o.CodeMinutes) > now - TimeSpan.FromSeconds(45))
            return EmailCodeResult.TooSoon;

        await store.InvalidateAuthCodesAsync(AuthCodeKinds.EmailOtp, email, ct);
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        await store.CreateAuthCodeAsync(new AuthCodeRecord(Guid.NewGuid(), AuthCodeKinds.EmailOtp, HashCode(email, code), null, email, now.AddMinutes(o.CodeMinutes), 0), ct);
        await sender.SendSignInCodeAsync(email, code, ct);
        return EmailCodeResult.Sent;
    }

    public async Task<(EmailVerifyResult Result, string? Email)> VerifyAsync(string? rawEmail, string? code, CancellationToken ct = default)
    {
        var email = Normalize(rawEmail);
        if (email is null || code is null || code.Length != 6 || !code.All(char.IsAsciiDigit)) return (EmailVerifyResult.Invalid, null);
        var now = time.GetUtcNow();
        var live = await store.FindLiveAuthCodeAsync(AuthCodeKinds.EmailOtp, email, now, ct);
        if (live is null) return (EmailVerifyResult.Invalid, null);

        var attempts = await store.IncrementAuthCodeAttemptsAsync(live.Id, ct);
        if (attempts > options.Value.Email.MaxAttempts)
        {
            await store.InvalidateAuthCodesAsync(AuthCodeKinds.EmailOtp, email, ct);
            return (EmailVerifyResult.TooManyAttempts, null);
        }

        var consumed = await store.ConsumeAuthCodeAsync(AuthCodeKinds.EmailOtp, HashCode(email, code), now, ct);
        return consumed is null ? (EmailVerifyResult.Invalid, null) : (EmailVerifyResult.Ok, email);
    }

    public static string? Normalize(string? email)
    {
        var e = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(e) || e.Length > 254) return null;
        var at = e.IndexOf('@');
        if (at < 1 || at != e.LastIndexOf('@') || at == e.Length - 1 || !e[(at + 1)..].Contains('.') || e.Any(char.IsWhiteSpace)) return null;
        return e;
    }

    private byte[] HashCode(string email, string code) => HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{email}\n{code}"));
}
