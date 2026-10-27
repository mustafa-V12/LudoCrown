using LudoCrown.App.Services;

namespace LudoCrown.Mobile.Services;

/// <summary>Tokens in the iOS Keychain / Android Keystore / Windows DPAPI via MAUI SecureStorage.</summary>
public sealed class MauiSecureStore : ISecureStore
{
    public async Task<string?> GetAsync(string key)
    {
        try { return await SecureStorage.Default.GetAsync(key); }
        catch (Exception)
        {
            // Keychain can be unavailable (e.g. restored backup on a new device): treat as signed out.
            SecureStorage.Default.Remove(key);
            return null;
        }
    }

    public Task SetAsync(string key, string value) => SecureStorage.Default.SetAsync(key, value);
    public void Remove(string key) => SecureStorage.Default.Remove(key);
}

public sealed class MauiSettingsStore : ISettingsStore
{
    public string? GetString(string key) => Preferences.Default.Get<string?>(key, null);
    public void SetString(string key, string? value)
    {
        if (value is null) Preferences.Default.Remove(key);
        else Preferences.Default.Set(key, value);
    }
    public bool GetBool(string key, bool fallback) => Preferences.Default.Get(key, fallback);
    public void SetBool(string key, bool value) => Preferences.Default.Set(key, value);
}

public sealed class ShellNavigationService : INavigationService
{
    public Task GoToAsync(string route) => MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync(route));
    public Task GoBackAsync() => MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync(".."));
    public Task ReplaceAsync(string route) => MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync($"../{route}"));
}

public sealed class MauiDialogService : IDialogService
{
    private static Page Page => Shell.Current?.CurrentPage ?? Application.Current!.Windows[0].Page!;

    public Task AlertAsync(string title, string message, string ok = "OK") =>
        MainThread.InvokeOnMainThreadAsync(() => Page.DisplayAlert(title, message, ok));

    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel = "Cancel") =>
        MainThread.InvokeOnMainThreadAsync(() => Page.DisplayAlert(title, message, accept, cancel));

    public Task<string?> PromptAsync(string title, string message, string accept = "OK", string? initial = null, int maxLength = 50, bool numeric = false) =>
        MainThread.InvokeOnMainThreadAsync(() => Page.DisplayPromptAsync(title, message, accept, "Cancel", initialValue: initial ?? "", maxLength: maxLength,
            keyboard: numeric ? Keyboard.Numeric : Keyboard.Text));

    public Task<string?> ActionSheetAsync(string title, string cancel, params string[] options) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var result = await Page.DisplayActionSheet(title, cancel, null, options);
            return result == cancel ? null : result;
        });
}

/// <summary>Plays a bundled sound file. iOS uses AVAudioPlayer; other platforms are silent in this version.</summary>
public interface IAudioPlayer
{
    void Play(string fileName);
}

public sealed class SilentAudioPlayer : IAudioPlayer
{
    public void Play(string fileName) { }
}

public sealed class FeedbackService(ISettingsStore settings, IAudioPlayer audio) : IFeedbackService
{
    public void Play(SoundEffect effect)
    {
        if (!settings.GetBool(SettingKeys.Sound, true)) return;
        audio.Play(effect switch
        {
            SoundEffect.DiceRoll => "sfx_dice.wav",
            SoundEffect.Move => "sfx_move.wav",
            SoundEffect.Capture => "sfx_capture.wav",
            SoundEffect.Enter => "sfx_enter.wav",
            SoundEffect.Finish => "sfx_finish.wav",
            SoundEffect.Win => "sfx_win.wav",
            SoundEffect.Lose => "sfx_lose.wav",
            SoundEffect.Turn => "sfx_turn.wav",
            _ => "sfx_tap.wav",
        });
    }

    public void Haptic(bool strong = false)
    {
        if (!settings.GetBool(SettingKeys.Haptics, true)) return;
        try
        {
            if (HapticFeedback.Default.IsSupported)
                HapticFeedback.Default.Perform(strong ? HapticFeedbackType.LongPress : HapticFeedbackType.Click);
        }
        catch (Exception) { /* haptics are optional */ }
    }
}

public sealed class MauiShareService : IShareService
{
    public Task ShareTextAsync(string title, string text) =>
        MainThread.InvokeOnMainThreadAsync(() => Share.Default.RequestAsync(new ShareTextRequest { Title = title, Text = text }));

    public async Task OpenUrlAsync(string url)
    {
        try { await Browser.Default.OpenAsync(new Uri(url), BrowserLaunchMode.SystemPreferred); }
        catch (Exception) { /* invalid or placeholder URL */ }
    }
}

public sealed class MauiConnectivityService : IConnectivityService
{
    public bool IsOnline => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;
}

public sealed class MauiMainThread : IMainThread
{
    public void Post(Action action)
    {
        if (MainThread.IsMainThread) action();
        else MainThread.BeginInvokeOnMainThread(action);
    }
}

/// <summary>Google/Facebook sign-in through the system browser sheet (ASWebAuthenticationSession on iOS).</summary>
public sealed class MauiAuthBrowser : IAuthBrowser
{
    public bool IsSupported => DeviceInfo.Platform == DevicePlatform.iOS || DeviceInfo.Platform == DevicePlatform.Android;

    public async Task<IReadOnlyDictionary<string, string>?> AuthenticateAsync(Uri startUrl, Uri callbackUrl)
    {
        try
        {
            var result = await WebAuthenticator.Default.AuthenticateAsync(new WebAuthenticatorOptions
            {
                Url = startUrl,
                CallbackUrl = callbackUrl,
                PrefersEphemeralWebBrowserSession = true,
            });
            return result.Properties;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }
}

/// <summary>Native "Sign in with Apple" sheet. Only supported on iOS 13+.</summary>
public sealed class MauiAppleSignIn : IAppleSignIn
{
    public bool IsSupported => DeviceInfo.Platform == DevicePlatform.iOS && DeviceInfo.Version.Major >= 13;

    public async Task<AppleSignInResult?> SignInAsync()
    {
        if (!IsSupported) return null;
        try
        {
            var r = await AppleSignInAuthenticator.AuthenticateAsync(new AppleSignInAuthenticator.Options
            {
                IncludeEmailScope = false, // the game doesn't need the user's email
                IncludeFullNameScope = true,
            });
            if (string.IsNullOrEmpty(r.IdToken)) return null;
            r.Properties.TryGetValue("authorization_code", out var code);
            r.Properties.TryGetValue("name", out var name);
            return new AppleSignInResult(r.IdToken, code, name);
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }
}

/// <summary>Handles ludocrown://join/CODE links: stores the code and opens the home screen, which joins the table.</summary>
public static class DeepLinks
{
    public static void Handle(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "ludocrown") return;
        if (uri.Host != "join") return;
        var code = Core.Protocol.RoomCodes.Normalize(uri.AbsolutePath.Trim('/'));
        if (code is null) return;
        var state = IPlatformApplication.Current?.Services.GetService<global::LudoCrown.App.ViewModels.AppState>();
        if (state is null) return;
        state.PendingJoinCode = code;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (Shell.Current is not null) await Shell.Current.GoToAsync(global::LudoCrown.App.Services.Routes.Home);
        });
    }
}
