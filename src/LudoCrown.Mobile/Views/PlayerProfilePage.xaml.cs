using LudoCrown.App.ViewModels;

namespace LudoCrown.Mobile.Views;

public partial class PlayerProfilePage : BasePage
{
    public PlayerProfilePage(PlayerProfileViewModel vm) : base(vm) => InitializeComponent();
}
