namespace LudoCrown.Server.Options;

/// <summary>Bound from the "Jwt" section. SigningKey must come from a secret store / environment variable.</summary>
public sealed class JwtOptions
{
    public string Issuer { get; set; } = "ludocrown";
    public string Audience { get; set; } = "ludocrown-app";
    /// <summary>Base64-encoded key, at least 32 bytes. Env var: Jwt__SigningKey.</summary>
    public string? SigningKey { get; set; }
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 60;
}

public sealed class AppleOptions
{
    /// <summary>Accepted "aud" values: the iOS bundle identifier (and a Services ID if web sign-in is added).</summary>
    public List<string> ClientIds { get; set; } = new();
    public string TeamId { get; set; } = "";
    public string KeyId { get; set; } = "";
    /// <summary>Contents of the .p8 key (for token revocation on account deletion). Env var: Auth__Apple__PrivateKey.</summary>
    public string? PrivateKey { get; set; }
    public bool Enabled => ClientIds.Count > 0;
    public bool RevocationConfigured => Enabled && TeamId != "" && KeyId != "" && !string.IsNullOrWhiteSpace(PrivateKey);
}

public sealed class OAuthProviderOptions
{
    public string ClientId { get; set; } = "";
    /// <summary>Env var: Auth__Google__ClientSecret / Auth__Facebook__ClientSecret.</summary>
    public string? ClientSecret { get; set; }
    public bool Enabled => ClientId != "" && !string.IsNullOrWhiteSpace(ClientSecret);
}

public sealed class EmailAuthOptions
{
    public bool Enabled { get; set; }
    public int CodeMinutes { get; set; } = 10;
    public int MaxAttempts { get; set; } = 5;
    /// <summary>"Log" writes codes to the server log (development only). "Smtp" sends real mail.</summary>
    public string Sender { get; set; } = "Log";
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = "";
    public string? SmtpPassword { get; set; }
    public string FromAddress { get; set; } = "";
}

public sealed class AuthOptions
{
    /// <summary>Public base URL of this server, used to build OAuth redirect URIs (e.g. https://api.example.com).</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5080";
    /// <summary>Custom URL scheme the app registers; OAuth results are returned to {scheme}://auth.</summary>
    public string AppCallbackScheme { get; set; } = "ludocrown";
    public bool GuestEnabled { get; set; } = true;
    public AppleOptions Apple { get; set; } = new();
    public OAuthProviderOptions Google { get; set; } = new();
    public OAuthProviderOptions Facebook { get; set; } = new();
    public string FacebookGraphVersion { get; set; } = "v21.0";
    public EmailAuthOptions Email { get; set; } = new();

    public string AppCallbackUrl => $"{AppCallbackScheme}://auth";
}

public sealed class MatchOptions
{
    /// <summary>Seconds a connected player has to act before the server acts for them.</summary>
    public double TurnSeconds { get; set; } = 20;
    /// <summary>Seconds to wait on a disconnected player's turn before auto-playing it.</summary>
    public double DisconnectedTurnSeconds { get; set; } = 6;
    /// <summary>A player disconnected for longer than this is marked abandoned and replaced by a bot.</summary>
    public double DisconnectGraceSeconds { get; set; } = 90;
    /// <summary>Consecutive timed-out turns before a connected-but-idle player is marked abandoned.</summary>
    public int MaxMissedTurns { get; set; } = 3;
    /// <summary>
    /// Decision window after a roll with no legal move when the player could pay for a reroll. The roll is held so
    /// they can reroll (gem) or pass; when the window ends it is passed automatically (not counted as a missed turn).
    /// </summary>
    public double HeldRollSeconds { get; set; } = 10;
    /// <summary>Server-wide switch for in-match quick chat (moderation kill switch). Players can also turn chat off for themselves.</summary>
    public bool ChatEnabled { get; set; } = true;
    /// <summary>Pause before bots act, so moves are readable.</summary>
    public double BotDelaySeconds { get; set; } = 1.0;
    /// <summary>Matchmaking fills empty seats with clearly-labelled bots after this wait. 0 disables.</summary>
    public double MatchmakingBotFillSeconds { get; set; } = 30;
    /// <summary>How long a finished match stays available for results/rematch.</summary>
    public double EndedMatchRetentionSeconds { get; set; } = 180;
    public double PrivateRoomIdleMinutes { get; set; } = 30;
}
