using System.ComponentModel;
using LudoCrown.App.ViewModels;

namespace LudoCrown.Mobile.Controls;

/// <summary>
/// Segmented picker for the implemented game modes (Classic, Quick). Built from <see cref="ModePicker.Options"/>, so a mode
/// appears only once it is implemented — never as a placeholder.
/// </summary>
public sealed class ModeSelector : ContentView
{
    public static readonly BindableProperty PickerProperty = BindableProperty.Create(nameof(Picker), typeof(ModePicker), typeof(ModeSelector),
        propertyChanged: (b, _, n) => ((ModeSelector)b).Build(n as ModePicker));

    private readonly Grid _grid = new() { ColumnSpacing = 12 };
    private readonly List<(ModeOption Option, Button Button)> _buttons = new();

    public ModeSelector() => Content = _grid;

    public ModePicker? Picker { get => (ModePicker?)GetValue(PickerProperty); set => SetValue(PickerProperty, value); }

    private void Build(ModePicker? picker)
    {
        _grid.Children.Clear();
        _grid.ColumnDefinitions.Clear();
        foreach (var (o, _) in _buttons) o.PropertyChanged -= OnOptionChanged;
        _buttons.Clear();
        if (picker is null) return;
        for (var i = 0; i < picker.Options.Count; i++)
        {
            var option = picker.Options[i];
            _grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var button = new Button
            {
                Text = $"{option.Name}\n{option.Tagline}",
                Style = (Style)Application.Current!.Resources["SecondaryButton"],
                Command = picker.SelectCommand,
                CommandParameter = option,
                LineBreakMode = LineBreakMode.WordWrap,
                FontSize = 15,
                BorderWidth = 2.5,
            };
            SemanticProperties.SetDescription(button, $"{option.Name} mode. {option.WinCondition}");
            _grid.Add(button, i);
            option.PropertyChanged += OnOptionChanged;
            _buttons.Add((option, button));
        }
        Refresh();
    }

    private void OnOptionChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        foreach (var (o, b) in _buttons)
        {
            b.BorderColor = o.IsSelected ? Palette.Gold : Color.FromArgb("#3D2875");
            b.TextColor = o.IsSelected ? Palette.Gold : Palette.Ivory;
        }
    }
}
