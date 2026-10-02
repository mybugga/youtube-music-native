using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace YouTubeMusicNative.Views;

/// <summary>
/// Small attached behaviours for lists:
///  ActivateCommand    – runs with the item's data on double-click or Enter (skips non-focusable header rows).
///  EndReachedCommand  – runs when the list is scrolled near the bottom (infinite scroll).
///  OpenMenuOnClick    – a button that opens its own ContextMenu on left click ("…" more buttons).
///  BubbleWheel        – a horizontal-only scroller passes vertical wheel input to the page behind it.
///  ScrollTarget       – a button that pages the named element's ScrollViewer left (-1) or right (+1).
///  FollowItem         – keeps the bound item (the playing song) scrolled to the top of the list.
/// </summary>
public static class ListBehaviors
{
    // ---- ActivateCommand ----------------------------------------------------------------------

    public static readonly DependencyProperty ActivateCommandProperty = DependencyProperty.RegisterAttached(
        "ActivateCommand", typeof(ICommand), typeof(ListBehaviors), new PropertyMetadata(null, OnActivateCommandChanged));

    public static ICommand? GetActivateCommand(DependencyObject d) => (ICommand?)d.GetValue(ActivateCommandProperty);
    public static void SetActivateCommand(DependencyObject d, ICommand? value) => d.SetValue(ActivateCommandProperty, value);

    private static void OnActivateCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl list) return;
        list.MouseDoubleClick -= OnDoubleClick;
        list.KeyDown -= OnKeyDown;
        if (e.NewValue is null) return;
        list.MouseDoubleClick += OnDoubleClick;
        list.KeyDown += OnKeyDown;
    }

    private static void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container is null || !container.Focusable) return; // scrollbar, empty space or header row
        if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null) return; // a button inside the row
        Execute(GetActivateCommand((DependencyObject)sender), container.DataContext);
        e.Handled = true;
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not ListBox list || list.SelectedItem is null) return;
        if (list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) is ListBoxItem { Focusable: false }) return;
        Execute(GetActivateCommand(list), list.SelectedItem);
        e.Handled = true;
    }

    // ---- EndReachedCommand --------------------------------------------------------------------

    public static readonly DependencyProperty EndReachedCommandProperty = DependencyProperty.RegisterAttached(
        "EndReachedCommand", typeof(ICommand), typeof(ListBehaviors), new PropertyMetadata(null, OnEndReachedCommandChanged));

    public static ICommand? GetEndReachedCommand(DependencyObject d) => (ICommand?)d.GetValue(EndReachedCommandProperty);
    public static void SetEndReachedCommand(DependencyObject d, ICommand? value) => d.SetValue(EndReachedCommandProperty, value);

    private static void OnEndReachedCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.RemoveHandler(ScrollViewer.ScrollChangedEvent, (ScrollChangedEventHandler)OnScrollChanged);
        if (e.NewValue is not null)
            el.AddHandler(ScrollViewer.ScrollChangedEvent, (ScrollChangedEventHandler)OnScrollChanged);
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer sv || sv.ScrollableHeight <= 0 || e.VerticalChange <= 0) return;
        if (sv.VerticalOffset >= sv.ScrollableHeight - Math.Max(3, sv.ViewportHeight * 0.5))
            Execute(GetEndReachedCommand((DependencyObject)sender), null);
    }

    // ---- OpenMenuOnClick ----------------------------------------------------------------------

    public static readonly DependencyProperty OpenMenuOnClickProperty = DependencyProperty.RegisterAttached(
        "OpenMenuOnClick", typeof(bool), typeof(ListBehaviors), new PropertyMetadata(false, OnOpenMenuOnClickChanged));

    public static bool GetOpenMenuOnClick(DependencyObject d) => (bool)d.GetValue(OpenMenuOnClickProperty);
    public static void SetOpenMenuOnClick(DependencyObject d, bool value) => d.SetValue(OpenMenuOnClickProperty, value);

    private static void OnOpenMenuOnClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button) return;
        button.Click -= OnMenuButtonClick;
        if (e.NewValue is true) button.Click += OnMenuButtonClick;
    }

    private static void OnMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } el) return;
        menu.PlacementTarget = el;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    // ---- BubbleWheel --------------------------------------------------------------------------

    public static readonly DependencyProperty BubbleWheelProperty = DependencyProperty.RegisterAttached(
        "BubbleWheel", typeof(bool), typeof(ListBehaviors), new PropertyMetadata(false, OnBubbleWheelChanged));

    public static bool GetBubbleWheel(DependencyObject d) => (bool)d.GetValue(BubbleWheelProperty);
    public static void SetBubbleWheel(DependencyObject d, bool value) => d.SetValue(BubbleWheelProperty, value);

    private static void OnBubbleWheelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        el.PreviewMouseWheel -= OnPreviewMouseWheel;
        if (e.NewValue is true) el.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject d) return;
        // Shift+wheel scrolls the row sideways; plain wheel goes to the page.
        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            if (FindChild<ScrollViewer>(d) is { } own)
            {
                Motion.GlideHorizontally(own, -e.Delta * 1.5);
                e.Handled = true;
            }
            return;
        }
        if (VisualTreeHelper.GetParent(d) is not UIElement parent) return;
        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender,
        });
    }

    // ---- ScrollTarget (paging buttons) --------------------------------------------------------

    public static readonly DependencyProperty ScrollTargetProperty = DependencyProperty.RegisterAttached(
        "ScrollTarget", typeof(FrameworkElement), typeof(ListBehaviors), new PropertyMetadata(null, OnScrollTargetChanged));

    public static FrameworkElement? GetScrollTarget(DependencyObject d) => (FrameworkElement?)d.GetValue(ScrollTargetProperty);
    public static void SetScrollTarget(DependencyObject d, FrameworkElement? value) => d.SetValue(ScrollTargetProperty, value);

    public static readonly DependencyProperty ScrollDirectionProperty = DependencyProperty.RegisterAttached(
        "ScrollDirection", typeof(int), typeof(ListBehaviors), new PropertyMetadata(1));

    public static int GetScrollDirection(DependencyObject d) => (int)d.GetValue(ScrollDirectionProperty);
    public static void SetScrollDirection(DependencyObject d, int value) => d.SetValue(ScrollDirectionProperty, value);

    private static void OnScrollTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button) return;
        button.Click -= OnScrollButtonClick;
        if (e.NewValue is not null) button.Click += OnScrollButtonClick;
    }

    private static void OnScrollButtonClick(object sender, RoutedEventArgs e)
    {
        var button = (DependencyObject)sender;
        if (GetScrollTarget(button) is not { } target || FindChild<ScrollViewer>(target) is not { } sv) return;
        double step = Math.Max(200, sv.ViewportWidth * 0.8) * GetScrollDirection(button);
        Motion.GlideHorizontally(sv, step);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static void Execute(ICommand? command, object? parameter)
    {
        if (command?.CanExecute(parameter) == true) command.Execute(parameter);
    }

    // ---- FollowItem ---------------------------------------------------------------------------

    /// <summary>
    /// Keeps the bound item (the playing song) at the top of the list: glides there when it changes,
    /// and jumps there whenever the list is shown again.
    /// </summary>
    public static readonly DependencyProperty FollowItemProperty = DependencyProperty.RegisterAttached(
        "FollowItem", typeof(object), typeof(ListBehaviors), new PropertyMetadata(null, OnFollowItemChanged));

    public static object? GetFollowItem(DependencyObject d) => d.GetValue(FollowItemProperty);
    public static void SetFollowItem(DependencyObject d, object? value) => d.SetValue(FollowItemProperty, value);

    private static void OnFollowItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list) return;
        list.IsVisibleChanged -= OnFollowListVisibleChanged;
        list.IsVisibleChanged += OnFollowListVisibleChanged;
        BringToTop(list, animate: true);
    }

    private static void OnFollowListVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) BringToTop((ListBox)sender, animate: false);
    }

    private static void BringToTop(ListBox list, bool animate)
    {
        if (!list.IsVisible || GetFollowItem(list) is not { } item) return;
        // After layout, so a just-shown list or a just-changed queue has its rows in place.
        list.Dispatcher.BeginInvoke(() =>
        {
            if (!list.IsVisible || !ReferenceEquals(GetFollowItem(list), item) || FindChild<ScrollViewer>(list) is not { } viewer) return;
            if (list.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement row)
            {
                // Far away (not built yet by the virtualizing list): jump near it first, then line it up.
                list.ScrollIntoView(item);
                list.UpdateLayout();
                if (list.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement built) return;
                row = built;
                animate = false;
            }
            double top = viewer.VerticalOffset + row.TransformToAncestor(viewer).Transform(new Point()).Y;
            if (animate) Motion.GlideVerticallyTo(viewer, top);
            else viewer.ScrollToVerticalOffset(Math.Clamp(top, 0, viewer.ScrollableHeight));
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    public static T? FindChild<T>(DependencyObject d) where T : DependencyObject
    {
        if (d is T self) return self;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindChild<T>(VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }
}

/// <summary>Gives header rows (anything that isn't a track) a plain, non-selectable container.</summary>
public sealed class TrackContainerStyleSelector : StyleSelector
{
    public Style? TrackStyle { get; set; }
    public Style? HeaderStyle { get; set; }

    public override Style? SelectStyle(object item, DependencyObject container) =>
        item is Api.Track ? TrackStyle : HeaderStyle;
}
