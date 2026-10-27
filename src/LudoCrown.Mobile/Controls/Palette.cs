using LudoCrown.Core.Model;

namespace LudoCrown.Mobile.Controls;

/// <summary>Ludo Crown's "royal board-game night" palette. Also mirrored in Resources/Styles/Colors.xaml.</summary>
public static class Palette
{
    public static readonly Color Night = Color.FromArgb("#140C2E");
    public static readonly Color Velvet = Color.FromArgb("#21154A");
    public static readonly Color Plum = Color.FromArgb("#2E1B5C");
    public static readonly Color Gold = Color.FromArgb("#E9B949");
    public static readonly Color GoldDeep = Color.FromArgb("#B8862B");
    public static readonly Color Ivory = Color.FromArgb("#FBF3E4");
    public static readonly Color Parchment = Color.FromArgb("#F1E4C8");
    public static readonly Color Line = Color.FromArgb("#D2BF98");
    public static readonly Color Ink = Color.FromArgb("#1A1230");

    public static Color Of(PlayerColor c) => c switch
    {
        PlayerColor.Ruby => Color.FromArgb("#D7263D"),
        PlayerColor.Emerald => Color.FromArgb("#17A673"),
        PlayerColor.Topaz => Color.FromArgb("#F2A71B"),
        PlayerColor.Sapphire => Color.FromArgb("#2F6FE0"),
        _ => Colors.Gray,
    };

    public static Color Light(PlayerColor c) => Of(c).WithLuminosity(Math.Min(0.82f, Of(c).GetLuminosity() + 0.30f));
    public static Color Dark(PlayerColor c) => Of(c).WithLuminosity(Math.Max(0.18f, Of(c).GetLuminosity() - 0.18f));
}
