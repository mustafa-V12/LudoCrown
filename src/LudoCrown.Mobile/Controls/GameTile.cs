using System.Windows.Input;
using Microsoft.Maui.Controls.Shapes;

namespace LudoCrown.Mobile.Controls;

/// <summary>A glossy lobby tile: picture, optional big number, title and subtitle. Presses down when tapped.</summary>
public sealed class GameTile : ContentView
{
    public static readonly BindableProperty ArtProperty = BindableProperty.Create(nameof(Art), typeof(string), typeof(GameTile), "", propertyChanged: (b, _, _) => ((GameTile)b).Update());
    public static readonly BindableProperty NumberProperty = BindableProperty.Create(nameof(Number), typeof(string), typeof(GameTile), "", propertyChanged: (b, _, _) => ((GameTile)b).Update());
    public static readonly BindableProperty TitleProperty = BindableProperty.Create(nameof(Title), typeof(string), typeof(GameTile), "", propertyChanged: (b, _, _) => ((GameTile)b).Update());
    public static readonly BindableProperty SubtitleProperty = BindableProperty.Create(nameof(Subtitle), typeof(string), typeof(GameTile), "", propertyChanged: (b, _, _) => ((GameTile)b).Update());
    public static readonly BindableProperty VariantProperty = BindableProperty.Create(nameof(Variant), typeof(string), typeof(GameTile), "small", propertyChanged: (b, _, _) => ((GameTile)b).Update());
    public static readonly BindableProperty CommandProperty = BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(GameTile));
    public static readonly BindableProperty CommandParameterProperty = BindableProperty.Create(nameof(CommandParameter), typeof(object), typeof(GameTile));

    private readonly Border _frame = new() { StrokeThickness = 3, Stroke = Color.FromArgb("#8CFFFFFF") };
    private readonly Image _art = new() { Aspect = Aspect.AspectFit, VerticalOptions = LayoutOptions.Start };
    private readonly Label _number;
    private readonly Label _title;
    private readonly Label _subtitle;
    private readonly BoxView _shine = new() { VerticalOptions = LayoutOptions.Start, InputTransparent = true };

    public GameTile()
    {
        _number = new Label { Style = (Style)Application.Current!.Resources["TileNumber"], VerticalOptions = LayoutOptions.Start, InputTransparent = true };
        _title = new Label { Style = (Style)Application.Current!.Resources["TileLabel"], VerticalOptions = LayoutOptions.End, InputTransparent = true };
        _subtitle = new Label { FontFamily = "BalooBold", FontSize = 12, TextColor = Color.FromArgb("#E6FFFFFF"), HorizontalTextAlignment = TextAlignment.Center, VerticalOptions = LayoutOptions.End, InputTransparent = true };
        _frame.Content = new Grid
        {
            Children =
            {
                _art,
                _shine,
                _number,
                new VerticalStackLayout { VerticalOptions = LayoutOptions.End, Spacing = -4, Padding = new Thickness(4, 0, 4, 8), Children = { _title, _subtitle }, InputTransparent = true },
            },
        };
        Content = _frame;
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            await _frame.TranslateTo(0, 4, 60);
            await _frame.TranslateTo(0, 0, 80);
            if (Command?.CanExecute(CommandParameter) == true) Command.Execute(CommandParameter);
            Tapped?.Invoke(this, EventArgs.Empty);
        };
        GestureRecognizers.Add(tap);
        Update();
    }

    public event EventHandler? Tapped;

    public string Art { get => (string)GetValue(ArtProperty); set => SetValue(ArtProperty, value); }
    public string Number { get => (string)GetValue(NumberProperty); set => SetValue(NumberProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    /// <summary>"gold" or "violet" (big tiles) or "small".</summary>
    public string Variant { get => (string)GetValue(VariantProperty); set => SetValue(VariantProperty, value); }
    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }

    private void Update()
    {
        var big = Variant is "gold" or "violet";
        var res = Application.Current!.Resources;
        _frame.Background = (Brush)res[Variant switch { "gold" => "GoldTileBrush", "violet" => "VioletTileBrush", _ => "SmallTileBrush" }];
        _frame.StrokeShape = new RoundRectangle { CornerRadius = big ? 22 : 18 };
        _frame.Shadow = new Shadow
        {
            Brush = Variant switch { "gold" => Color.FromArgb("#9A5A07"), "violet" => Color.FromArgb("#24106A"), _ => Color.FromArgb("#1E0E52") },
            Offset = new Point(0, 6), Radius = 0, Opacity = 1,
        };
        HeightRequest = big ? 196 : 128;
        _art.Source = string.IsNullOrEmpty(Art) ? null : Art;
        _art.HeightRequest = big ? 148 : 86;
        _art.Margin = new Thickness(0, big ? 4 : 2, 0, 0);
        _shine.HeightRequest = big ? 70 : 44;
        _shine.Color = Color.FromArgb("#1FFFFFFF");
        _shine.CornerRadius = new CornerRadius(big ? 20 : 16, big ? 20 : 16, 0, 0);
        _number.Text = Number;
        _number.IsVisible = !string.IsNullOrEmpty(Number);
        _number.FontSize = big ? 64 : 40;
        _number.Margin = new Thickness(0, big ? -6 : -4, 0, 0);
        _title.Text = Title;
        _title.FontSize = big ? 21 : 15;
        _subtitle.Text = Subtitle;
        _subtitle.IsVisible = !string.IsNullOrEmpty(Subtitle);
        _subtitle.FontSize = big ? 12 : 11;
        SemanticProperties.SetDescription(this, string.IsNullOrEmpty(Subtitle) ? Title : $"{Title}, {Subtitle}");
    }
}
