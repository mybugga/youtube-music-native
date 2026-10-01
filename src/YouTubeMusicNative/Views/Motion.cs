using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace YouTubeMusicNative.Views;

/// <summary>
/// The "Smooth animations" setting: eased wheel scrolling, page / dialog transitions, fading hover highlights and
/// a cover ease-in when the song changes. Everything here is a no-op (instant) while <see cref="Enabled"/> is off.
/// </summary>
public static class Motion
{
    public static bool Enabled { get; set; }

    private static readonly IEasingFunction Ease = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    /// <summary>Lets WPF animations run at the monitor's refresh rate (it caps them at 60 fps otherwise). Call before any window.</summary>
    public static void UseDisplayFrameRate()
    {
        var mode = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
        int hz = EnumDisplaySettings(null, -1, ref mode) ? mode.dmDisplayFrequency : 60;
        if (hz > 60) Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata { DefaultValue = (int?)Math.Min(hz, 240) });
    }

    private static void Animate(UIElement target, DependencyProperty property, double? from, double to, int ms)
    {
        if (!Enabled)
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, to);
            return;
        }
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease };
        if (from is { } f) animation.From = f;
        target.BeginAnimation(property, animation);
    }

    private static void Animate(Animatable target, DependencyProperty property, double from, double to, int ms) =>
        target.BeginAnimation(property, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease });

    // ---- hover highlight ---------------------------------------------------------------------

    /// <summary>
    /// On a highlight layer inside a control template: fades it in while the pointer is over the templated control
    /// (instant when animations are off). Replaces an IsMouseOver trigger.
    /// </summary>
    public static readonly DependencyProperty HoverOverlayProperty = DependencyProperty.RegisterAttached(
        "HoverOverlay", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnHoverOverlayChanged));

    public static bool GetHoverOverlay(DependencyObject d) => (bool)d.GetValue(HoverOverlayProperty);
    public static void SetHoverOverlay(DependencyObject d, bool value) => d.SetValue(HoverOverlayProperty, value);

    private static void OnHoverOverlayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement layer || e.NewValue is not true) return;
        layer.Opacity = 0;
        layer.IsHitTestVisible = false;
        MouseEventHandler enter = (_, _) => Animate(layer, UIElement.OpacityProperty, null, 1, 120);
        MouseEventHandler leave = (_, _) => Animate(layer, UIElement.OpacityProperty, null, 0, 220);
        UIElement? host = null;
        layer.Loaded += (_, _) =>
        {
            if (host is not null || layer.TemplatedParent is not UIElement parent) return;
            host = parent;
            host.MouseEnter += enter;
            host.MouseLeave += leave;
            layer.BeginAnimation(UIElement.OpacityProperty, null);
            layer.Opacity = host.IsMouseOver ? 1 : 0;
        };
        layer.Unloaded += (_, _) =>
        {
            if (host is null) return;
            host.MouseEnter -= enter;
            host.MouseLeave -= leave;
            host = null;
        };
    }

    // ---- fade in / out ------------------------------------------------------------------------

    /// <summary>Bind to a bool: the element fades to fully visible when true and to transparent when false.</summary>
    public static readonly DependencyProperty ShowProperty = DependencyProperty.RegisterAttached(
        "Show", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnShowChanged));

    public static bool GetShow(DependencyObject d) => (bool)d.GetValue(ShowProperty);
    public static void SetShow(DependencyObject d, bool value) => d.SetValue(ShowProperty, value);

    private static void OnShowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        bool show = e.NewValue is true;
        if (!element.IsLoaded)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = show ? 1 : 0;
            return;
        }
        Animate(element, UIElement.OpacityProperty, null, show ? 1 : 0, show ? 140 : 260);
    }

    /// <summary>Fades and slides an element down into place each time it becomes visible (the mini player's drawer).</summary>
    public static readonly DependencyProperty SlideInProperty = DependencyProperty.RegisterAttached(
        "SlideIn", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnSlideInChanged));

    public static bool GetSlideIn(DependencyObject d) => (bool)d.GetValue(SlideInProperty);
    public static void SetSlideIn(DependencyObject d, bool value) => d.SetValue(SlideInProperty, value);

    private static void OnSlideInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true) return;
        var shift = new TranslateTransform();
        element.RenderTransform = shift;
        element.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is not true || !Enabled) return;
            Animate(element, UIElement.OpacityProperty, 0, 1, 260);
            Animate(shift, TranslateTransform.YProperty, -16, 0, 340);
        };
    }

    /// <summary>Fades the element in each time it becomes visible (a window's content when the window is shown).</summary>
    public static readonly DependencyProperty FadeInProperty = DependencyProperty.RegisterAttached(
        "FadeIn", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnFadeInChanged));

    public static bool GetFadeIn(DependencyObject d) => (bool)d.GetValue(FadeInProperty);
    public static void SetFadeIn(DependencyObject d, bool value) => d.SetValue(FadeInProperty, value);

    private static void OnFadeInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element || e.NewValue is not true) return;
        element.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true && Enabled) Animate(element, UIElement.OpacityProperty, 0, 1, 240);
        };
    }

    /// <summary>Eases a 0..1 progress: cubic normally, a softer quartic tail with smooth animations on.</summary>
    public static double EaseOut(double p) => Enabled ? 1 - Math.Pow(1 - p, 4) : 1 - Math.Pow(1 - p, 3);

    /// <summary>Stretches a duration a little with smooth animations on (gentler motion, more frames).</summary>
    public static double Duration(double ms) => Enabled ? ms * 1.45 : ms;

    // ---- page transition ---------------------------------------------------------------------

    /// <summary>On the shell's page host: each new page fades in and rises slightly.</summary>
    public static readonly DependencyProperty PageTransitionProperty = DependencyProperty.RegisterAttached(
        "PageTransition", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnPageTransitionChanged));

    public static bool GetPageTransition(DependencyObject d) => (bool)d.GetValue(PageTransitionProperty);
    public static void SetPageTransition(DependencyObject d, bool value) => d.SetValue(PageTransitionProperty, value);

    private static void OnPageTransitionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContentControl host || e.NewValue is not true) return;
        var shift = new TranslateTransform();
        host.RenderTransform = shift;
        DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(ContentControl))
            .AddValueChanged(host, (_, _) =>
            {
                if (!Enabled || !host.IsVisible) return;
                Animate(host, UIElement.OpacityProperty, 0, 1, 200);
                Animate(shift, TranslateTransform.YProperty, 14, 0, 280);
            });
    }

    // ---- pop in ------------------------------------------------------------------------------

    /// <summary>Fades and scales an element in each time it becomes visible (dialogs).</summary>
    public static readonly DependencyProperty PopInProperty = DependencyProperty.RegisterAttached(
        "PopIn", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnPopInChanged));

    public static bool GetPopIn(DependencyObject d) => (bool)d.GetValue(PopInProperty);
    public static void SetPopIn(DependencyObject d, bool value) => d.SetValue(PopInProperty, value);

    private static void OnPopInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true) return;
        var scale = new ScaleTransform(1, 1);
        element.RenderTransform = scale;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is not true || !Enabled) return;
            Animate(element, UIElement.OpacityProperty, 0, 1, 180);
            Animate(scale, ScaleTransform.ScaleXProperty, 0.94, 1, 240);
            Animate(scale, ScaleTransform.ScaleYProperty, 0.94, 1, 240);
        };
    }

    // ---- change cue --------------------------------------------------------------------------

    /// <summary>Bind to a value (the playing song): when it changes, the element eases in (new cover art).</summary>
    public static readonly DependencyProperty AnimateOnChangeProperty = DependencyProperty.RegisterAttached(
        "AnimateOnChange", typeof(object), typeof(Motion), new PropertyMetadata(null, OnAnimateOnChange));

    public static object? GetAnimateOnChange(DependencyObject d) => d.GetValue(AnimateOnChangeProperty);
    public static void SetAnimateOnChange(DependencyObject d, object? value) => d.SetValue(AnimateOnChangeProperty, value);

    private static void OnAnimateOnChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (!Enabled || d is not FrameworkElement element || !element.IsLoaded || e.OldValue is null || e.NewValue is null) return;
        if (element.RenderTransform is not ScaleTransform scale || scale.IsFrozen)
        {
            scale = new ScaleTransform(1, 1);
            element.RenderTransform = scale;
            element.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        Animate(element, UIElement.OpacityProperty, 0.2, 1, 320);
        Animate(scale, ScaleTransform.ScaleXProperty, 0.96, 1, 360);
        Animate(scale, ScaleTransform.ScaleYProperty, 0.96, 1, 360);
    }

    // ---- smooth wheel scrolling --------------------------------------------------------------

    /// <summary>Eased mouse-wheel scrolling (set on every ScrollViewer by an implicit style).</summary>
    public static readonly DependencyProperty SmoothScrollProperty = DependencyProperty.RegisterAttached(
        "SmoothScroll", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnSmoothScrollChanged));

    public static bool GetSmoothScroll(DependencyObject d) => (bool)d.GetValue(SmoothScrollProperty);
    public static void SetSmoothScroll(DependencyObject d, bool value) => d.SetValue(SmoothScrollProperty, value);

    private sealed class ScrollState
    {
        public double Target;
        public double LastSet = double.NaN;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, ScrollState> States = new();
    private static readonly HashSet<ScrollViewer> Scrolling = [];
    private static TimeSpan _lastFrame;

    private static void OnSmoothScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer) return;
        viewer.PreviewMouseWheel -= OnWheel;
        if (e.NewValue is true) viewer.PreviewMouseWheel += OnWheel;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Enabled || sender is not ScrollViewer viewer || viewer.ScrollableHeight <= 0) return;
        // Let an inner scrollable area (a list inside the page, a multi-line text box) take its own wheel.
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != viewer; node = Parent(node))
        {
            if (node is ScrollViewer inner && inner.ScrollableHeight > 0) return;
            if (node is TextBox { AcceptsReturn: true }) return;
        }

        // Virtualized lists that scroll by item (not pixel) count in items.
        bool byItem = viewer.CanContentScroll && viewer.TemplatedParent is ItemsControl list
                      && VirtualizingPanel.GetScrollUnit(list) == ScrollUnit.Item;
        double step = byItem ? -e.Delta / 120.0 * 3 : -e.Delta * 0.9;

        var state = States.GetOrCreateValue(viewer);
        if (!Scrolling.Contains(viewer)) state.Target = viewer.VerticalOffset;
        state.Target = Math.Clamp(state.Target + step, 0, viewer.ScrollableHeight);
        e.Handled = true;

        if (Scrolling.Add(viewer) && Scrolling.Count == 1)
        {
            _lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private static void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.05);
        if (now == _lastFrame) return; // same frame reported twice
        _lastFrame = now;

        foreach (var viewer in Scrolling.ToList())
        {
            var state = States.GetOrCreateValue(viewer);
            double current = viewer.VerticalOffset;
            // The user grabbed the scrollbar or jumped with the keyboard: stop following our target.
            if (!double.IsNaN(state.LastSet) && Math.Abs(current - state.LastSet) > 2)
            {
                state.LastSet = double.NaN;
                Scrolling.Remove(viewer);
                continue;
            }
            double target = Math.Clamp(state.Target, 0, viewer.ScrollableHeight);
            double next = current + (target - current) * (1 - Math.Exp(-dt * 16));
            if (Math.Abs(target - next) < 0.5 || !viewer.IsLoaded)
            {
                viewer.ScrollToVerticalOffset(target);
                state.LastSet = double.NaN;
                Scrolling.Remove(viewer);
                continue;
            }
            viewer.ScrollToVerticalOffset(next);
            state.LastSet = next;
        }
        if (Scrolling.Count == 0) CompositionTarget.Rendering -= OnFrame;
    }

    // ---- display refresh rate ----------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DevMode devMode);
}
