using LudoCrown.App.ViewModels;

namespace LudoCrown.Mobile.Views;

public partial class PrivateRoomPage : BasePage
{
    public PrivateRoomPage(PrivateRoomViewModel vm) : base(vm)
    {
        InitializeComponent();
        Header.BackCommand = vm.LeaveCommand; // backing out leaves the table so the seat frees up
    }

    protected override bool OnBackButtonPressed()
    {
        ((PrivateRoomViewModel)BindingContext).LeaveCommand.Execute(null);
        return true;
    }
}
