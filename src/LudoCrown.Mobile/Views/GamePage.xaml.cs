using LudoCrown.App.Game;
using LudoCrown.App.Services;
using LudoCrown.App.ViewModels;
using LudoCrown.Core.Model;
using LudoCrown.Core.Protocol;
using LudoCrown.Mobile.Controls;

namespace LudoCrown.Mobile.Views;

public partial class GamePage : BasePage
{
    private static readonly Dictionary<string, string> ReactionEmoji = new()
    {
        ["wave"] = "👋", ["gg"] = "🤝 GG", ["wow"] = "😮", ["laugh"] = "😂", ["crown"] = "👑", ["oops"] = "😅", ["thanks"] = "🙏", ["hurry"] = "⏳",
    };

    private readonly GameViewModel _vm;
    private readonly ISettingsStore _settings;
    private readonly Dictionary<PlayerColor, PlayerChip> _chips = new();
    private IDispatcherTimer? _clock;
    private string? _lastAnnounced;

    public GamePage(GameViewModel vm, ISettingsStore settings) : base(vm)
    {
        _vm = vm;
        _settings = settings;
        InitializeComponent();
        Board.PieceTapped += async piece => await _vm.TapPieceAsync(piece);
        Dice.Tapped += () => _vm.RollCommand.Execute(null);
        _vm.BoardChanged += OnBoardChanged;
        _vm.ReactionReceived += OnReaction;

        foreach (var id in Reactions.All)
        {
            var b = new Button
            {
                Text = ReactionEmoji.GetValueOrDefault(id, id),
                Style = (Style)Application.Current!.Resources["SecondaryButton"],
                Padding = new Thickness(12, 6),
                MinimumWidthRequest = 52,
                FontSize = 18,
                Command = _vm.ReactCommand,
                CommandParameter = id,
            };
            SemanticProperties.SetDescription(b, $"Send reaction {id}");
            ReactionBar.Children.Add(b);
        }
    }

    protected override void OnAppearing()
    {
        Board.ReduceMotion = _settings.GetBool(SettingKeys.ReduceMotion, false);
        base.OnAppearing();
        DeviceDisplay.Current.KeepScreenOn = true;
        _clock = Dispatcher.CreateTimer();
        _clock.Interval = TimeSpan.FromMilliseconds(250);
        _clock.Tick += (_, _) => UpdateTimers();
        _clock.Start();
    }

    protected override void OnDisappearing()
    {
        _clock?.Stop();
        DeviceDisplay.Current.KeepScreenOn = false;
        base.OnDisappearing();
    }

    // Swipe-back/hardware back asks before leaving a game in progress.
    protected override bool OnBackButtonPressed()
    {
        _vm.LeaveCommand.Execute(null);
        return true;
    }

    private void OnBoardChanged(BoardState? previous, BoardState next)
    {
        Board.SetState(previous, next);
        EnsureChips(next);
        foreach (var seat in next.Seats)
            if (_chips.TryGetValue(seat.Color, out var chip))
                chip.Update(seat, next.CurrentSeat == seat.Seat && !next.IsFinished, next.WinnerSeat == seat.Seat);

        var dieColor = next.Seats[next.CurrentSeat].Color;
        Dice.SetState(next.PendingRoll ?? next.LastRoll ?? 1, next.CanRoll, dieColor, Board.ReduceMotion);
        SemanticProperties.SetDescription(Board, $"Ludo board. {_vm.Status}");

        if (next.CurrentSeatView.IsLocalHuman && _vm.Status != _lastAnnounced)
        {
            _lastAnnounced = _vm.Status;
            SemanticScreenReader.Default.Announce(_vm.Status);
        }
    }

    private void EnsureChips(BoardState state)
    {
        if (_chips.Count == state.Seats.Count) return;
        _chips.Clear();
        foreach (var slot in new[] { SeatTopLeft, SeatTopRight, SeatBottomLeft, SeatBottomRight }) slot.Content = null;
        foreach (var seat in state.Seats)
        {
            var chip = new PlayerChip(seat.Color is PlayerColor.Topaz or PlayerColor.Sapphire);
            _chips[seat.Color] = chip;
            var slot = seat.Color switch
            {
                PlayerColor.Emerald => SeatTopLeft,
                PlayerColor.Topaz => SeatTopRight,
                PlayerColor.Ruby => SeatBottomLeft,
                _ => SeatBottomRight,
            };
            slot.Content = chip;
        }
    }

    private void UpdateTimers()
    {
        var board = _vm.Board;
        if (board is null) return;
        var seconds = _vm.SecondsLeft(DateTimeOffset.UtcNow);
        foreach (var seat in board.Seats)
            if (_chips.TryGetValue(seat.Color, out var chip))
                chip.SetTimer(seat.Seat == board.CurrentSeat ? seconds : null);
    }

    private async void OnReaction(ReactionInfo reaction, SeatView seat)
    {
        if (!_chips.TryGetValue(seat.Color, out var chip)) return;
        await chip.ShowReactionAsync(ReactionEmoji.GetValueOrDefault(reaction.ReactionId, reaction.ReactionId));
    }
}

/// <summary>Seat badge: avatar, name, and status (bot, reconnecting, pieces home, turn timer).</summary>
public sealed class PlayerChip : ContentView
{
    private readonly AvatarView _avatar = new() { Size = 44 };
    private readonly Label _name = new() { FontAttributes = FontAttributes.Bold, FontSize = 15, LineBreakMode = LineBreakMode.TailTruncation };
    private readonly Label _detail = new() { FontSize = 12, TextColor = Palette.Gold };
    private readonly Label _bubble = new() { FontSize = 22, IsVisible = false, HorizontalOptions = LayoutOptions.Center };
    private readonly Border _frame;
    private SeatView? _seat;

    public PlayerChip(bool alignRight)
    {
        var text = new VerticalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.Center, Children = { _name, _detail } };
        var row = new Grid { ColumnSpacing = 8, ColumnDefinitions = alignRight ? new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)) : new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)) };
        if (alignRight)
        {
            _name.HorizontalTextAlignment = _detail.HorizontalTextAlignment = TextAlignment.End;
            row.Add(text, 0);
            row.Add(_avatar, 1);
        }
        else
        {
            row.Add(_avatar, 0);
            row.Add(text, 1);
        }
        _frame = new Border
        {
            Padding = new Thickness(8, 6),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            Background = Palette.Velvet,
            StrokeThickness = 1.5,
            Content = new Grid { Children = { row, _bubble } },
        };
        Content = _frame;
    }

    public void Update(SeatView seat, bool isTurn, bool isWinner)
    {
        _seat = seat;
        _avatar.AvatarId = seat.AvatarId;
        _avatar.RingColor = Palette.Of(seat.Color);
        _avatar.IsActive = isTurn;
        _name.Text = seat.Name;
        _frame.Stroke = isTurn ? Palette.Gold : Palette.Of(seat.Color).WithAlpha(0.6f);
        _frame.Opacity = seat.IsConnected ? 1 : 0.6;
        _detail.Text = Detail(seat, isWinner);
        SemanticProperties.SetDescription(this, $"{seat.Name}, {seat.Color}. {_detail.Text}{(isTurn ? ". Current turn" : "")}");
    }

    public void SetTimer(int? seconds)
    {
        if (_seat is null) return;
        _detail.Text = seconds is int s ? $"⏱ {s}s · {Detail(_seat, false)}" : Detail(_seat, false);
        _detail.TextColor = seconds is <= 5 ? Color.FromArgb("#FF6B6B") : Palette.Gold;
    }

    public async Task ShowReactionAsync(string text)
    {
        _bubble.Text = text;
        _bubble.IsVisible = true;
        _bubble.Opacity = 0;
        await _bubble.FadeTo(1, 150);
        await Task.Delay(1600);
        await _bubble.FadeTo(0, 300);
        _bubble.IsVisible = false;
    }

    private static string Detail(SeatView s, bool winner)
    {
        if (winner) return "👑 Winner";
        var home = $"{s.FinishedCount}/4 home";
        if (!s.IsConnected) return $"Reconnecting… · {home}";
        return s.IsBot ? $"Bot · {home}" : home;
    }
}
