using LudoCrown.App.Mvvm;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;

namespace LudoCrown.Mobile.Views;

/// <summary>Binds a page to its view model and forwards appearing/disappearing. Keeps content inside iPhone safe areas.</summary>
public class BasePage : ContentPage
{
    protected BasePage() : this(null) { }

    protected BasePage(ViewModelBase? viewModel)
    {
        BindingContext = viewModel;
#pragma warning disable CS0618 // UseSafeArea is still honoured; newer SafeAreaEdges API can replace it later.
        On<iOS>().SetUseSafeArea(true);
#pragma warning restore CS0618
        Shell.SetNavBarIsVisible(this, false);
    }

    protected ViewModelBase? ViewModel => BindingContext as ViewModelBase;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (ViewModel is not null) await ViewModel.OnAppearingAsync();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        if (ViewModel is not null) await ViewModel.OnDisappearingAsync();
    }
}

/// <summary>Top bar for pushed screens: a large back button and the screen title.</summary>
public sealed class PageHeader : ContentView
{
    public static readonly BindableProperty TitleProperty = BindableProperty.Create(nameof(Title), typeof(string), typeof(PageHeader), "", propertyChanged: (b, _, n) => ((PageHeader)b)._title.Text = (string)n);
    public static readonly BindableProperty ShowBackProperty = BindableProperty.Create(nameof(ShowBack), typeof(bool), typeof(PageHeader), true, propertyChanged: (b, _, n) => ((PageHeader)b)._back.IsVisible = (bool)n);

    private readonly Label _title = new() { FontSize = 20, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Center };
    private readonly Button _back = new() { Text = "‹ Back", Style = (Style)Application.Current!.Resources["LinkButton"], HorizontalOptions = LayoutOptions.Start };

    public PageHeader()
    {
        SemanticProperties.SetHeadingLevel(_title, SemanticHeadingLevel.Level1);
        SemanticProperties.SetDescription(_back, "Back");
        _back.Clicked += async (_, _) => { if (BackCommand is null) await Shell.Current.GoToAsync(".."); else BackCommand.Execute(null); };
        Content = new Grid
        {
            Padding = new Thickness(8, 4),
            MinimumHeightRequest = 52,
            Children = { _title, _back },
        };
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public bool ShowBack { get => (bool)GetValue(ShowBackProperty); set => SetValue(ShowBackProperty, value); }
    /// <summary>Optional custom back behaviour (e.g. confirm before leaving a game).</summary>
    public System.Windows.Input.ICommand? BackCommand { get; set; }
}
