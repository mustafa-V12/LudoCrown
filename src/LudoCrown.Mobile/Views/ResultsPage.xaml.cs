using LudoCrown.App.ViewModels;

namespace LudoCrown.Mobile.Views;

public partial class ResultsPage : BasePage
{
    public ResultsPage(ResultsViewModel vm) : base(vm) => InitializeComponent();

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Crown.Scale = 0.6;
        Crown.Rotation = -8;
        await Task.WhenAll(Crown.ScaleTo(1, 450, Easing.SpringOut), Crown.RotateTo(0, 450, Easing.SpringOut));
    }

    protected override bool OnBackButtonPressed()
    {
        ((ResultsViewModel)BindingContext).HomeCommand.Execute(null);
        return true;
    }
}
