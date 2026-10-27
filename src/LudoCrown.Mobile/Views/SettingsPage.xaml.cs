using LudoCrown.App.ViewModels;

namespace LudoCrown.Mobile.Views;

public partial class SettingsPage : BasePage
{
    public SettingsPage(SettingsViewModel vm) : base(vm)
    {
        vm.VersionText = $"Ludo Crown {AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";
        InitializeComponent();
    }
}
