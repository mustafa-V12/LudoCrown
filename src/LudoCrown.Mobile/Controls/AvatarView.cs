using LudoCrown.Core.Model;

namespace LudoCrown.Mobile.Controls;

/// <summary>Round avatar badge with an optional coloured ring (seat colour) and "active turn" glow.</summary>
public sealed class AvatarView : ContentView
{
    public static readonly BindableProperty AvatarIdProperty = BindableProperty.Create(nameof(AvatarId), typeof(string), typeof(AvatarView), "crown", propertyChanged: (b, _, _) => ((AvatarView)b).Update());
    public static readonly BindableProperty RingColorProperty = BindableProperty.Create(nameof(RingColor), typeof(Color), typeof(AvatarView), Palette.Gold, propertyChanged: (b, _, _) => ((AvatarView)b).Update());
    public static readonly BindableProperty SizeProperty = BindableProperty.Create(nameof(Size), typeof(double), typeof(AvatarView), 48.0, propertyChanged: (b, _, _) => ((AvatarView)b).Update());
    public static readonly BindableProperty IsActiveProperty = BindableProperty.Create(nameof(IsActive), typeof(bool), typeof(AvatarView), false, propertyChanged: (b, _, _) => ((AvatarView)b).Update());

    private readonly Border _ring = new() { StrokeThickness = 3, Padding = 2 };
    private readonly Image _image = new() { Aspect = Aspect.AspectFill };

    public AvatarView()
    {
        _ring.Content = _image;
        Content = _ring;
        Update();
    }

    public string AvatarId { get => (string)GetValue(AvatarIdProperty); set => SetValue(AvatarIdProperty, value); }
    public Color RingColor { get => (Color)GetValue(RingColorProperty); set => SetValue(RingColorProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    public static string ImageFor(string? avatarId) =>
        Core.Protocol.Avatars.IsValid(avatarId) ? $"avatar_{avatarId}.png" : "avatar_crown.png";

    private void Update()
    {
        _image.Source = ImageFor(AvatarId);
        _ring.WidthRequest = _ring.HeightRequest = Size;
        _ring.StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(Size / 2) };
        _ring.Stroke = IsActive ? Palette.Gold : RingColor;
        _ring.StrokeThickness = IsActive ? 4 : 2.5;
        _ring.Shadow = IsActive ? new Shadow { Brush = Palette.Gold, Radius = 14, Opacity = 0.9f, Offset = new Point(0, 0) } : null!;
        SemanticProperties.SetDescription(this, $"Avatar {AvatarId}");
    }

    public static Color ColorFor(PlayerColor c) => Palette.Of(c);
}
