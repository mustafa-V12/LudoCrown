using LudoCrown.App.ViewModels;

namespace LudoCrown.Mobile.Views;

public partial class HomePage : BasePage
{
    private readonly HomeViewModel _vm;
    private int _players = 2;

    public HomePage(HomeViewModel vm) : base(vm)
    {
        _vm = vm;
        InitializeComponent();
    }

    private void OnTwoPlayers(object? sender, EventArgs e) => OpenModePanel(2);
    private void OnFourPlayers(object? sender, EventArgs e) => OpenModePanel(4);

    private void OpenModePanel(int players)
    {
        _players = players;
        ModeTitle.Text = $"{players} Players";
        ModeOverlay.StyleId = "mode-panel";
        PrivateOverlay.IsVisible = false;
        ShowOverlay(ModeOverlay);
    }

    private void OnPrivateTable(object? sender, EventArgs e)
    {
        ModeOverlay.IsVisible = false;
        PrivateOverlay.StyleId = "private-panel";
        ShowOverlay(PrivateOverlay);
    }

    private static async void ShowOverlay(VisualElement overlay)
    {
        overlay.Opacity = 0;
        overlay.Scale = 0.94;
        overlay.IsVisible = true;
        await Task.WhenAll(overlay.FadeTo(1, 140), overlay.ScaleTo(1, 180, Easing.CubicOut));
        Services.DevShots.Capture(overlay.StyleId ?? "panel", 500);
    }

    private void OnCloseOverlay(object? sender, EventArgs e)
    {
        ModeOverlay.IsVisible = false;
        PrivateOverlay.IsVisible = false;
    }

    private void OnPlayOnline(object? sender, EventArgs e)
    {
        OnCloseOverlay(sender, e);
        if (_vm.PlayOnlineCommand.CanExecute(_players.ToString())) _vm.PlayOnlineCommand.Execute(_players.ToString());
    }

    private void OnCreateTable(object? sender, EventArgs e)
    {
        OnCloseOverlay(sender, e);
        if (_vm.CreateTableCommand.CanExecute(null)) _vm.CreateTableCommand.Execute(null);
    }

    private void OnJoinTable(object? sender, EventArgs e)
    {
        OnCloseOverlay(sender, e);
        if (_vm.JoinTableCommand.CanExecute(null)) _vm.JoinTableCommand.Execute(null);
    }

    protected override bool OnBackButtonPressed()
    {
        if (ModeOverlay.IsVisible || PrivateOverlay.IsVisible)
        {
            OnCloseOverlay(null, EventArgs.Empty);
            return true;
        }
        return base.OnBackButtonPressed();
    }
}
