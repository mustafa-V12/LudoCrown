namespace LudoCrown.Mobile.WinUI;

/// <summary>Windows desktop host — lets you run and test the game on your PC from Visual Studio.</summary>
public partial class App : MauiWinUIApplication
{
    public App() => InitializeComponent();

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
