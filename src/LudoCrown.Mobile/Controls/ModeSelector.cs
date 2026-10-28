using System.ComponentModel;
using LudoCrown.App.ViewModels;
using Microsoft.Maui.Controls.Shapes;

namespace LudoCrown.Mobile.Controls;

/// <summary>
/// Picture cards for the implemented game modes (Classic, Quick), side by side. Built from <see cref="ModePicker.Options"/>,
/// so a mode appears only once it is implemented — never as a placeholder.
/// </summary>
public sealed class ModeSelector : ContentView
{
    public static readonly BindableProperty PickerProperty = BindableProperty.Create(nameof(Picker), typeof(ModePicker), typeof(ModeSelector),
        propertyChanged: (b, _, n) => ((ModeSelector)b).Build(n as ModePicker));

    private readonly Grid _grid = new() { ColumnSpacing = 10 };
    private readonly List<(ModeOption Option, Border Card, Label Name, Border Check)> _cards = new();

    public ModeSelector() => Content = _grid;

    public ModePicker? Picker { get => (ModePicker?)GetValue(PickerProperty); set => SetValue(PickerProperty, value); }

    private void Build(ModePicker? picker)
    {
        _grid.Children.Clear();
        _grid.ColumnDefinitions.Clear();
        foreach (var c in _cards) c.Option.PropertyChanged -= OnOptionChanged;
        _cards.Clear();
        if (picker is null) return;
        for (var i = 0; i < picker.Options.Count; i++)
        {
            var option = picker.Options[i];
            _grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var name = new Label { Text = option.Name, FontFamily = "BalooHeavy", FontSize = 22, HorizontalTextAlignment = TextAlignment.Center, Margin = new Thickness(0, 2, 0, -4) };
            var tag = new Label { Text = option.Tagline, FontFamily = "BalooBold", FontSize = 13, TextColor = Palette.Muted, HorizontalTextAlignment = TextAlignment.Center };
            var check = new Border
            {
                WidthRequest = 28, HeightRequest = 28, StrokeShape = new Ellipse(), Stroke = Colors.White, StrokeThickness = 3,
                Background = Palette.Emerald, HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Start, Margin = new Thickness(0, -10, -8, 0),
                Content = new Label { Text = "✓", FontFamily = "BalooHeavy", FontSize = 15, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center },
            };
            var card = new Border
            {
                StrokeShape = new RoundRectangle { CornerRadius = 18 },
                StrokeThickness = 3,
                Padding = new Thickness(6, 8, 6, 12),
                Content = new Grid
                {
                    Children =
                    {
                        new VerticalStackLayout
                        {
                            Spacing = 0,
                            Children =
                            {
                                new Image { Source = $"mode_{option.Mode.ToString().ToLowerInvariant()}.png", HeightRequest = 80, Aspect = Aspect.AspectFit },
                                name, tag,
                            },
                        },
                        check,
                    },
                },
            };
            card.GestureRecognizers.Add(new TapGestureRecognizer { Command = picker.SelectCommand, CommandParameter = option });
            SemanticProperties.SetDescription(card, $"{option.Name} mode. {option.WinCondition}");
            _grid.Add(card, i);
            option.PropertyChanged += OnOptionChanged;
            _cards.Add((option, card, name, check));
        }
        Refresh();
    }

    private void OnOptionChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        foreach (var (o, card, name, check) in _cards)
        {
            card.Stroke = o.IsSelected ? Palette.GoldLight : Color.FromArgb("#2EFFFFFF");
            card.Background = o.IsSelected
                ? new LinearGradientBrush(new GradientStopCollection { new(Color.FromArgb("#7850DC"), 0), new(Color.FromArgb("#2E1B5C"), 1) }, new Point(0, 0), new Point(0, 1))
                : new LinearGradientBrush(new GradientStopCollection { new(Color.FromArgb("#5A140C2E"), 0), new(Color.FromArgb("#D9140C2E"), 1) }, new Point(0, 0), new Point(0, 1));
            card.Shadow = o.IsSelected ? new Shadow { Brush = Palette.GoldLight, Radius = 16, Opacity = 0.45f, Offset = new Point(0, 0) } : null!;
            name.TextColor = o.IsSelected ? Palette.GoldLight : Palette.Ivory;
            check.IsVisible = o.IsSelected;
            SemanticProperties.SetDescription(card, $"{o.Name} mode{(o.IsSelected ? ", selected" : "")}. {o.WinCondition}");
        }
    }
}
