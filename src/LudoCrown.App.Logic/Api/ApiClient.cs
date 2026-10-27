using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LudoCrown.App.Api;

// ---- DTOs mirroring the server's HTTP API (src/LudoCrown.Server/Endpoints/Dtos.cs) ----

public sealed record TokenResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, Guid UserId, bool IsGuest);
public sealed record ProvidersResponse(bool Guest, bool Apple, bool Google, bool Facebook, bool Email);
public sealed record StatsDto(int GamesPlayed, int GamesWon, int TwoPlayerWins, int FourPlayerWins, int PiecesCaptured, int Abandons, int CurrentWinStreak, int BestWinStreak);
public sealed record MeResponse(Guid UserId, bool IsGuest, string DisplayName, string AvatarId, string FriendCode, int Level, int Xp, int XpForNextLevel, long Coins, StatsDto Stats, IReadOnlyList<string> LinkedProviders);
public sealed record MatchHistoryDto(Guid MatchId, string Mode, int PlayerCount, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, int Placement, string Result, int CoinsAwarded, int XpAwarded, IReadOnlyList<string> Opponents);
public sealed record FriendDto(Guid UserId, string DisplayName, string AvatarId, int Level, string Status, bool IncomingRequest);
public sealed record FriendRequestResponse(string Status, string DisplayName);
public sealed record BlockedDto(Guid UserId, string DisplayName);
public sealed record DeleteAccountResponse(bool Deleted, bool AppleTokenRevoked, string? Note);
public sealed record ApiErrorBody(string Code, string Message);
public sealed record LinkTicketResponse(string Ticket);

public enum ApiErrorKind { None, Network, Unauthorized, NotFound, Conflict, RateLimited, Invalid, Server, NotConfigured }

public sealed record ApiResult<T>(T? Value, ApiErrorKind Kind = ApiErrorKind.None, string? Message = null, string? Code = null)
{
    public bool Ok => Kind == ApiErrorKind.None;
    public static ApiResult<T> Success(T? value) => new(value);
}

/// <summary>Supplies and refreshes the access token. Implemented by <see cref="Auth.SessionService"/>.</summary>
public interface ITokenProvider
{
    Task<string?> GetAccessTokenAsync(bool forceRefresh = false);
}

/// <summary>
/// Typed HTTP client for the Ludo Crown server. Adds the bearer token, refreshes once on 401, and turns
/// failures into friendly <see cref="ApiResult{T}"/> values instead of exceptions.
/// </summary>
public sealed class ApiClient(HttpClient http)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public ITokenProvider? Tokens { get; set; }

    public Task<ApiResult<T>> GetAsync<T>(string path, bool auth = true) => SendAsync<T>(HttpMethod.Get, path, null, auth);
    public Task<ApiResult<T>> PostAsync<T>(string path, object? body, bool auth = false) => SendAsync<T>(HttpMethod.Post, path, body, auth);
    public Task<ApiResult<T>> PatchAsync<T>(string path, object? body) => SendAsync<T>(HttpMethod.Patch, path, body, true);
    public Task<ApiResult<T>> DeleteAsync<T>(string path, object? body = null) => SendAsync<T>(HttpMethod.Delete, path, body, true);

    public async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, bool auth)
    {
        try
        {
            using var response = await SendOnceAsync(method, path, body, auth, forceRefresh: false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && auth && Tokens is not null)
            {
                using var retry = await SendOnceAsync(method, path, body, auth, forceRefresh: true);
                return await ReadAsync<T>(retry);
            }
            return await ReadAsync<T>(response);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new ApiResult<T>(default, ApiErrorKind.Network, "Can't reach the Ludo Crown server. Check your connection and try again.");
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, object? body, bool auth, bool forceRefresh)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        if (auth && Tokens is not null)
        {
            var token = await Tokens.GetAccessTokenAsync(forceRefresh);
            if (token is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
        return await http.SendAsync(request);
    }

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Accepted || response.Content.Headers.ContentLength == 0)
                return ApiResult<T>.Success(default);
            var text = await response.Content.ReadAsStringAsync();
            return ApiResult<T>.Success(string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<T>(text, Json));
        }

        ApiErrorBody? error = null;
        try { error = await response.Content.ReadFromJsonAsync<ApiErrorBody>(Json); } catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        var kind = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => ApiErrorKind.Unauthorized,
            HttpStatusCode.NotFound => error?.Code == "provider_disabled" ? ApiErrorKind.NotConfigured : ApiErrorKind.NotFound,
            HttpStatusCode.Conflict => ApiErrorKind.Conflict,
            HttpStatusCode.TooManyRequests => ApiErrorKind.RateLimited,
            HttpStatusCode.BadRequest => ApiErrorKind.Invalid,
            _ => ApiErrorKind.Server,
        };
        var message = error?.Message ?? kind switch
        {
            ApiErrorKind.Unauthorized => "Your session has expired. Please sign in again.",
            ApiErrorKind.RateLimited => "Too many requests. Please wait a moment.",
            ApiErrorKind.NotFound => "Not found.",
            _ => "Something went wrong on the server. Please try again.",
        };
        return new ApiResult<T>(default, kind, message, error?.Code);
    }
}
