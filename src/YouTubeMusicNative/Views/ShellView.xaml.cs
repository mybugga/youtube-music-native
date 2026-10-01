using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using YouTubeMusicNative.ViewModels;

namespace YouTubeMusicNative.Views;

public partial class ShellView : UserControl
{
    public ShellView()
    {
        ClearSearchCommand = new RelayCommand(ClearSearch);
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.FocusSearchRequested -= FocusSearch;
            if (e.NewValue is MainViewModel vm) vm.FocusSearchRequested += FocusSearch;
        };
    }

    /// <summary>Clicking the dimmed area around a dialog closes it.</summary>
    private void OnDialogBackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.CloseDialogCommand.Execute(null);
    }

    private void OnDialogCardClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    public ICommand ClearSearchCommand { get; }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnClearSearch(object sender, RoutedEventArgs e) => ClearSearch();

    /// <summary>Clicking into an empty search box shows the Search page with recent searches.</summary>
    private void OnSearchFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is MainViewModel { SearchText.Length: 0 } vm && vm.CurrentPageKind != AppPage.Search)
            vm.NavigateCommand.Execute(AppPage.Search);
    }

    private void ClearSearch()
    {
        if (DataContext is MainViewModel vm) vm.SearchText = "";
        SearchBox.Focus();
    }
}

/// <summary>Clips an element to a rounded rectangle so children (gradients, images) respect its corners.</summary>
public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundedClip), new PropertyMetadata(0.0, OnRadiusChanged));

    public static double GetRadius(DependencyObject d) => (double)d.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject d, double value) => d.SetValue(RadiusProperty, value);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.SizeChanged -= OnSizeChanged;
        el.SizeChanged += OnSizeChanged;
        Apply(el);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement el)
    {
        var r = GetRadius(el);
        var clip = new System.Windows.Media.RectangleGeometry(new Rect(0, 0, el.ActualWidth, el.ActualHeight), r, r);
        clip.Freeze();
        el.Clip = clip;
    }
}
