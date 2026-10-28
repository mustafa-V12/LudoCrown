using System.Windows.Input;
using LudoCrown.App.Mvvm;
using LudoCrown.App.Services;
using LudoCrown.Core.Model;
using LudoCrown.Core.Protocol;

namespace LudoCrown.App.ViewModels;

/// <summary>One selectable game mode with the rule explanation shown before the match.</summary>
public sealed class ModeOption(GameModeInfo info) : ObservableObject
{
    private bool _isSelected;
    public GameModeInfo Info { get; } = info;
    public GameMode Mode => Info.Mode;
    public string Name => Info.Name;
    public string Tagline => Info.Tagline;
    public string WinCondition => Info.WinCondition;
    public string RerollsText => $"Up to {Info.MaxRerolls} gem rerolls per match";
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>
/// The mode picker shared by Home and Offline setup. Only implemented modes are listed (from <see cref="GameModes.All"/>),
/// so there are never placeholder buttons. The choice is remembered on the device.
/// </summary>
public sealed class ModePicker : ObservableObject
{
    private readonly AppState _state;
    private readonly ISettingsStore _settings;

    public ModePicker(AppState state, ISettingsStore settings)
    {
        _state = state;
        _settings = settings;
        Options = GameModes.All.Select(m => new ModeOption(m)).ToList();
        if (GameModes.Parse(settings.GetString(SettingKeys.LastMode)) is GameMode saved) _state.SelectedMode = saved;
        SelectCommand = new AsyncCommand(p => { Select(p is ModeOption o ? o.Mode : GameModes.Parse(p?.ToString()) ?? GameMode.Classic); return Task.CompletedTask; });
        Select(_state.SelectedMode);
    }

    public IReadOnlyList<ModeOption> Options { get; }
    public ICommand SelectCommand { get; }
    public GameModeInfo Selected => GameModes.Get(_state.SelectedMode);
    public string SelectedSummary => $"{Selected.Name}: {Selected.WinCondition}";
    public string SelectedDescription => Selected.Description;

    public void Select(GameMode mode)
    {
        if (!GameModes.IsAvailable(mode)) mode = GameMode.Classic;
        _state.SelectedMode = mode;
        _settings.SetString(SettingKeys.LastMode, GameModes.Get(mode).Id);
        foreach (var o in Options) o.IsSelected = o.Mode == mode;
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(SelectedSummary));
        OnPropertyChanged(nameof(SelectedDescription));
    }
}

public static class MatchLabels
{
    public static string Queue(MatchQueue q) => q == MatchQueue.Ranked ? "Ranked" : "Casual";

    public static string QueueDescription(MatchQueue q) => q == MatchQueue.Ranked
        ? "Counts for the league. Gem rerolls allowed; boosts are off."
        : "No league points. Gem rerolls allowed, plus 1 Shield bottle per match.";

    public static string Kind(Game.MatchKind k) => k switch
    {
        Game.MatchKind.Ranked => "Ranked",
        Game.MatchKind.Casual => "Casual",
        Game.MatchKind.Private => "Private table",
        _ => "Offline",
    };
}
