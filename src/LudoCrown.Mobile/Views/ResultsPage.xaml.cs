using LudoCrown.App.ViewModels;

namespace LudoCrown.Mobile.Views;

public partial class ResultsPage : BasePage
{
    public ResultsPage(ResultsViewModel vm) : base(vm) => InitializeComponent();

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var vm = (ResultsViewModel)BindingContext;
        // The view model fills its texts in OnAppearingAsync; give it a moment before reading Won.
        await Task.Delay(50);
        RibbonLabel.Text = vm.Won ? "Victory" : "Game over";
        Crown.Scale = 0.6;
        Crown.Rotation = -8;
        if (vm.Won) Confetti.Burst();
        Services.DevShots.Capture(vm.Won ? "victory" : "results", 900);
        await Task.WhenAll(Crown.ScaleTo(1, 450, Easing.SpringOut), Crown.RotateTo(0, 450, Easing.SpringOut));
    }

    protected override bool OnBackButtonPressed()
    {
        ((ResultsViewModel)BindingContext).HomeCommand.Execute(null);
        return true;
    }
}
