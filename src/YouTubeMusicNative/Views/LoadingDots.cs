using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace YouTubeMusicNative.Views;

/// <summary>
/// Three dots that hop and brighten one after another ("waiting…"). Like <see cref="Spinner"/>, the animation
/// only runs while the element is visible.
/// </summary>
public sealed class LoadingDots : StackPanel
{
    private const double Period = 1.2, Stagger = 0.15, Hop = 0.3, Rise = 8, Dim = 0.35;

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(LoadingDots), new PropertyMetadata(Brushes.White,
            (d, e) => { foreach (var dot in ((LoadingDots)d).Children.OfType<Ellipse>()) dot.Fill = (Brush)e.NewValue; }));

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public LoadingDots()
    {
        Orientation = Orientation.Horizontal;
        Height = 9 + Rise;
        IsHitTestVisible = false;
        for (int i = 0; i < 3; i++)
        {
            Children.Add(new Ellipse
            {
                Width = 9,
                Height = 9,
                Margin = new Thickness(5, 0, 5, 0),
                Fill = Fill,
                Opacity = Dim,
                VerticalAlignment = VerticalAlignment.Bottom,
                RenderTransform = new TranslateTransform(),
            });
        }
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    private void UpdateAnimation()
    {
        for (int i = 0; i < Children.Count; i++)
        {
            var dot = (Ellipse)Children[i];
            var move = (TranslateTransform)dot.RenderTransform;
            if (!IsVisible)
            {
                move.BeginAnimation(TranslateTransform.YProperty, null);
                dot.BeginAnimation(OpacityProperty, null);
                continue;
            }
            double start = i * Stagger;
            move.BeginAnimation(TranslateTransform.YProperty, Cycle(start, 0, -Rise));
            dot.BeginAnimation(OpacityProperty, Cycle(start, Dim, 1));
        }
    }

    /// <summary>One repeating hop: rest, ease up to <paramref name="peak"/>, ease back down, rest until the cycle ends.</summary>
    private static DoubleAnimationUsingKeyFrames Cycle(double start, double rest, double peak)
    {
        var up = new SineEase { EasingMode = EasingMode.EaseOut };
        var down = new SineEase { EasingMode = EasingMode.EaseIn };
        var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(Period) };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(rest, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(rest, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(start))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(start + Hop)), up));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(rest, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(start + 2 * Hop)), down));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(rest, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Period))));
        anim.Freeze();
        return anim;
    }
}
