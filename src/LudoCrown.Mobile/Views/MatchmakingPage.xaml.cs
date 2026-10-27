using LudoCrown.App.ViewModels;

namespace LudoCrown.Mobile.Views;

public partial class MatchmakingPage : BasePage
{
    private bool _animating;

    public MatchmakingPage(MatchmakingViewModel vm) : base(vm) => InitializeComponent();

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _animating = true;
        _ = PulseAsync();
    }

    protected override void OnDisappearing()
    {
        _animating = false;
        base.OnDisappearing();
    }

    private async Task PulseAsync()
    {
        while (_animating)
        {
            await Crown.ScaleTo(1.08, 700, Easing.SinInOut);
            await Crown.ScaleTo(1.0, 700, Easing.SinInOut);
        }
    }

    // Hardware/gesture back cancels matchmaking instead of leaving a ticket in the queue.
    protected override bool OnBackButtonPressed()
    {
        ((MatchmakingViewModel)BindingContext).CancelCommand.Execute(null);
        return true;
    }
}
