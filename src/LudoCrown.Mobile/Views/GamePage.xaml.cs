using LudoCrown.App.Game;
using LudoCrown.App.Services;
using LudoCrown.App.ViewModels;
using LudoCrown.Core.Model;
using LudoCrown.Core.Protocol;
using LudoCrown.Mobile.Controls;
using Microsoft.Maui.Controls.Shapes;

namespace LudoCrown.Mobile.Views;

public partial class GamePage : BasePage
{
    private readonly GameViewModel _vm;
    private readonly ISettingsStore _settings;
    private readonly Dictionary<PlayerColor, CornerSeat> _corners = new();
    private readonly DiceView _dice = new() { InputTransparent = true };
    private IDispatcherTimer? _clock;
    private string? _lastAnnounced;

    public GamePage(GameViewModel vm, ISettingsStore settings) : base(vm)
    {
        _vm = vm;
        _settings = settings;
        InitializeComponent();
        Board.PieceTapped += async piece => await _vm.TapPieceAsync(piece);
        _vm.BoardChanged += OnBoardChanged;
        _vm.ReactionReceived += OnReaction;

        var secondary = (Style)Application.Current!.Resources["SecondaryButton"];
        foreach (var item in _vm.ChatPhrases)
        {
            var b = new Button { Text = item.Text, Style = secondary, Padding = new Thickness(10, 4), FontSize = 14, Margin = new Thickness(3), MinimumHeightRequest = 40, Command = _vm.ReactCommand, CommandParameter = item.Id };
            SemanticProperties.SetDescription(b, $"Send: {item.Text}");
            PhraseGrid.Children.Add(b);
        }
        foreach (var item in _vm.ChatEmoji)
        {
            var b = new Button { Text = item.Text, Style = secondary, Padding = new Thickness(6, 2), FontSize = 22, Margin = new Thickness(3), MinimumWidthRequest = 50, MinimumHeightRequest = 44, Command = _vm.ReactCommand, CommandParameter = item.Id };
            SemanticProperties.SetDescription(b, $"Send emoji {item.Id}");
            EmojiRow.Children.Add(b);
        }
        _vm.MuteChanged += () => { foreach (var corner in _corners.Values) corner.SetMuted(_vm.IsMuted(corner.Seat)); };
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

    private int _shotTurns;

    private void OnBoardChanged(BoardState? previous, BoardState next)
    {
        // Development screenshots: a few moments of the match (start, when you can roll, mid-game).
        if (Services.DevShots.Enabled && next.CanRoll && previous?.CanRoll != true && _shotTurns++ is 0 or 3 or 8)
            Services.DevShots.Capture($"game-turn{_shotTurns}", 400);
        Board.SetState(previous, next);
        EnsureCorners(next);
        var piecesToWin = next.ModeInfo.PiecesToWin;
        foreach (var seat in next.Seats)
            if (_corners.TryGetValue(seat.Color, out var corner))
            {
                var isTurn = next.CurrentSeat == seat.Seat && !next.IsFinished;
                corner.Update(seat, isTurn, next.WinnerSeat == seat.Seat, isTurn && next.CanRoll, piecesToWin, Board.ReduceMotion);
                if (isTurn) corner.HostDice(_dice);
            }

        var dieColor = next.Seats[next.CurrentSeat].Color;
        _dice.SetState(next.PendingRoll ?? next.LastRoll ?? 1, next.CanRoll, dieColor, Board.ReduceMotion);
        SemanticProperties.SetDescription(Board, $"Ludo board. {_vm.Status}");

        if (next.CurrentSeatView.IsLocalHuman && _vm.Status != _lastAnnounced)
        {
            _lastAnnounced = _vm.Status;
            SemanticScreenReader.Default.Announce(_vm.Status);
        }
    }

    private void EnsureCorners(BoardState state)
    {
        if (_corners.Count == state.Seats.Count) return;
        _corners.Clear();
        foreach (var slot in new[] { SeatTopLeft, SeatTopRight, SeatBottomLeft, SeatBottomRight }) slot.Content = null;
        foreach (var seat in state.Seats)
        {
            var right = seat.Color is PlayerColor.Topaz or PlayerColor.Sapphire;
            var top = seat.Color is PlayerColor.Emerald or PlayerColor.Topaz;
            var corner = new CornerSeat(right, top) { Seat = seat.Seat };
            corner.RollTapped += () => { if (_vm.RollCommand.CanExecute(null)) _vm.RollCommand.Execute(null); };
            corner.AvatarTapped += () => { if (_vm.OpenPlayerCommand.CanExecute(seat.Seat)) _vm.OpenPlayerCommand.Execute(seat.Seat); };
            _corners[seat.Color] = corner;
            var slot = seat.Color switch
            {
                PlayerColor.Emerald => SeatTopLeft,
                PlayerColor.Topaz => SeatTopRight,
                PlayerColor.Ruby => SeatBottomLeft,
                _ => SeatBottomRight,
            };
            slot.Content = corner;
        }
    }

    private void UpdateTimers()
    {
        var board = _vm.Board;
        if (board is null) return;
        var seconds = _vm.SecondsLeft(DateTimeOffset.UtcNow);
        foreach (var seat in board.Seats)
            if (_corners.TryGetValue(seat.Color, out var corner))
                corner.SetTimer(seat.Seat == board.CurrentSeat ? seconds : null);
    }

    private async void OnReaction(ReactionInfo reaction, SeatView seat)
    {
        if (!_corners.TryGetValue(seat.Color, out var corner)) return;
        await corner.ShowReactionAsync(Reactions.TextFor(reaction.ReactionId));
    }
}

/// <summary>
/// One player in a board corner: avatar with a spinning gold ring on their turn, crowned-pieces badge, name, and the die box
/// beside the avatar while it is their turn (glowing with a bouncing arrow when the local player can roll).
/// </summary>
public sealed class CornerSeat : ContentView
{
    private readonly AvatarView _avatar = new() { Size = 62 };
    private readonly Border _ring;
    private readonly Label _name = new() { FontFamily = "BalooHeavy", FontSize = 14, LineBreakMode = LineBreakMode.TailTruncation, MaximumWidthRequest = 110, HorizontalTextAlignment = TextAlignment.Center };
    private readonly Label _detail = new() { FontFamily = "BalooBold", FontSize = 11, TextColor = Palette.Muted, HorizontalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.TailTruncation, MaximumWidthRequest = 120 };
    private readonly Label _crowns = new() { FontFamily = "BalooHeavy", FontSize = 12, VerticalOptions = LayoutOptions.Center };
    private readonly Border _dieBox;
    private readonly ContentView _dieSlot = new();
    private readonly Label _arrow = new() { Text = "▼", FontSize = 18, TextColor = Palette.GoldLight, HorizontalOptions = LayoutOptions.Center, IsVisible = false, InputTransparent = true };
    private readonly Label _bubble = new()
    {
        FontSize = 15, FontFamily = "BalooHeavy", IsVisible = false, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
        BackgroundColor = Color.FromArgb("#F2FBF3E4"), TextColor = Color.FromArgb("#1A1230"), Padding = new Thickness(10, 3), InputTransparent = true,
    };
    private readonly bool _top;
    private bool _muted;
    private bool _active;
    private bool _canRoll;
    private SeatView? _seat;

    public int Seat { get; init; }
    public event Action? RollTapped;
    public event Action? AvatarTapped;

    public CornerSeat(bool alignRight, bool top)
    {
        _top = top;
        _ring = new Border
        {
            WidthRequest = 78, HeightRequest = 78, StrokeShape = new Ellipse(), StrokeThickness = 4, Opacity = 0, InputTransparent = true,
            Stroke = new LinearGradientBrush(new GradientStopCollection
            {
                new(Palette.GoldLight, 0), new(Colors.Transparent, 0.35f), new(Palette.Gold, 0.55f), new(Colors.Transparent, 0.85f), new(Palette.GoldLight, 1),
            }, new Point(0, 0), new Point(1, 1)),
            HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
        };
        var badge = new Border
        {
            Background = Palette.Ink, Stroke = Palette.Gold, StrokeThickness = 2, StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Padding = new Thickness(6, 0), HeightRequest = 20, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.End, Margin = new Thickness(0, 0, 0, -6),
            Content = new HorizontalStackLayout { Spacing = 2, Children = { new Image { Source = "icon_crown.png", WidthRequest = 14, HeightRequest = 12, VerticalOptions = LayoutOptions.Center }, _crowns } },
            InputTransparent = true,
        };
        var avatarArea = new Grid { WidthRequest = 80, HeightRequest = 80, Children = { _ring, _avatar, badge, _bubble } };
        _avatar.HorizontalOptions = LayoutOptions.Center;
        _avatar.VerticalOptions = LayoutOptions.Center;
        var avatarTap = new TapGestureRecognizer();
        avatarTap.Tapped += (_, _) => AvatarTapped?.Invoke();
        avatarArea.GestureRecognizers.Add(avatarTap);

        var who = new VerticalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.Center };
        if (top) { who.Add(avatarArea); who.Add(_name); who.Add(_detail); }
        else { who.Add(_name); who.Add(_detail); who.Add(avatarArea); }

        _dieBox = new Border
        {
            WidthRequest = 62, HeightRequest = 62, StrokeThickness = 3, StrokeShape = new RoundRectangle { CornerRadius = 16 },
            Background = new RadialGradientBrush(new GradientStopCollection { new(Color.FromArgb("#40FFFFFF"), 0), new(Color.FromArgb("#C00A051C"), 1) }, new Point(0.5, 0.35), 0.8),
            Content = _dieSlot, VerticalOptions = LayoutOptions.Center, Opacity = 0, Scale = 0.7,
        };
        _dieSlot.HorizontalOptions = LayoutOptions.Center;
        _dieSlot.VerticalOptions = LayoutOptions.Center;
        var dieTap = new TapGestureRecognizer();
        dieTap.Tapped += (_, _) => { if (_canRoll) RollTapped?.Invoke(); };
        _dieBox.GestureRecognizers.Add(dieTap);
        SemanticProperties.SetHint(_dieBox, "Double tap to roll when it's your turn");
        var dieColumn = new VerticalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.Center };
        if (top) { dieColumn.Add(_dieBox); dieColumn.Add(_arrow); _arrow.Text = "▲"; }
        else { dieColumn.Add(_arrow); dieColumn.Add(_dieBox); }

        var row = new HorizontalStackLayout { Spacing = 8, HorizontalOptions = alignRight ? LayoutOptions.End : LayoutOptions.Start };
        if (alignRight) { row.Add(dieColumn); row.Add(who); }
        else { row.Add(who); row.Add(dieColumn); }
        Content = row;
    }

    /// <summary>Moves the shared die into this player's box.</summary>
    public void HostDice(View dice)
    {
        if (dice.Parent == _dieSlot) return;
        if (dice.Parent is ContentView old) old.Content = null;
        _dieSlot.Content = dice;
    }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        if (_seat is not null) _detail.Text = Detail(_seat, false);
    }

    public void Update(SeatView seat, bool isTurn, bool isWinner, bool canRoll, int piecesToWin, bool reduceMotion)
    {
        _seat = seat;
        var color = Palette.Of(seat.Color);
        _avatar.AvatarId = seat.AvatarId;
        _avatar.RingColor = color;
        _avatar.IsActive = false;
        _name.Text = seat.Name;
        _crowns.Text = $"{seat.FinishedCount}/{piecesToWin}";
        _detail.Text = Detail(seat, isWinner);
        _detail.IsVisible = _detail.Text.Length > 0;
        _dieBox.Stroke = canRoll ? Palette.GoldLight : color;
        Opacity = seat.IsConnected ? 1 : 0.6;

        if (isTurn != _active)
        {
            _active = isTurn;
            this.AbortAnimation("ring");
            _ring.Rotation = 0;
            _ = _ring.FadeTo(isTurn ? 1 : 0, 200);
            _ = _dieBox.FadeTo(isTurn ? 1 : 0, 180);
            _ = _dieBox.ScaleTo(isTurn ? 1 : 0.7, 180, Easing.CubicOut);
            if (isTurn && !reduceMotion)
                new Animation(v => _ring.Rotation = v, 0, 360).Commit(this, "ring", 16, 2200, Easing.Linear, repeat: () => _active);
        }

        if (canRoll != _canRoll)
        {
            _canRoll = canRoll;
            this.AbortAnimation("pulse");
            _dieBox.Scale = _active ? 1 : 0.7;
            _arrow.IsVisible = canRoll;
            _dieBox.Shadow = canRoll ? new Shadow { Brush = Palette.GoldLight, Radius = 18, Opacity = 0.85f, Offset = new Point(0, 0) } : null!;
            if (canRoll && !reduceMotion)
            {
                var pulse = new Animation
                {
                    { 0, 0.5, new Animation(v => _dieBox.Scale = v, 1, 1.08) },
                    { 0.5, 1, new Animation(v => _dieBox.Scale = v, 1.08, 1) },
                    { 0, 0.5, new Animation(v => _arrow.TranslationY = v, 0, _top ? -5 : 5) },
                    { 0.5, 1, new Animation(v => _arrow.TranslationY = v, _top ? -5 : 5, 0) },
                };
                pulse.Commit(this, "pulse", 16, 1000, Easing.SinInOut, repeat: () => _canRoll);
            }
        }
        SemanticProperties.SetDescription(this, $"{seat.Name}, {seat.Color}. {seat.FinishedCount} of {piecesToWin} pieces crowned. {_detail.Text}{(isTurn ? ". Current turn" : "")}");
    }

    public void SetTimer(int? seconds)
    {
        if (_seat is null) return;
        _detail.Text = seconds is int s ? $"⏱ {s}s" : Detail(_seat, false);
        _detail.TextColor = seconds is <= 5 ? Color.FromArgb("#FF6B6B") : Palette.Muted;
        _detail.IsVisible = _detail.Text.Length > 0;
    }

    public async Task ShowReactionAsync(string text)
    {
        _bubble.Text = text;
        _bubble.IsVisible = true;
        _bubble.Opacity = 0;
        _bubble.Scale = 0.6;
        await Task.WhenAll(_bubble.FadeTo(1, 150), _bubble.ScaleTo(1, 200, Easing.SpringOut));
        await Task.Delay(1600);
        await _bubble.FadeTo(0, 300);
        _bubble.IsVisible = false;
    }

    private string Detail(SeatView s, bool winner)
    {
        var parts = new List<string>();
        if (winner) parts.Add("👑 Winner");
        if (s.ShieldActive) parts.Add("🛡 Shield");
        if (!s.IsConnected) parts.Add("Reconnecting…");
        else if (s.IsBot) parts.Add("Bot");
        if (_muted) parts.Add("🔇");
        return string.Join(" · ", parts);
    }
}
