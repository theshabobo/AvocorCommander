using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace AvocorCommander.Core;

/// <summary>
/// Attached property that makes any ItemsControl auto-scroll to the last item
/// whenever its ItemsSource collection changes.
/// Usage: core:AutoScrollBehavior.Enabled="True"
/// </summary>
public static class AutoScrollBehavior
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(AutoScrollBehavior),
            new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject obj)  => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl control) return;

        if ((bool)e.NewValue)
            control.ItemContainerGenerator.ItemsChanged += (_, _) => ScrollToEnd(control);
        // (unsubscribe on false not needed for typical lifetime)
    }

    private static void ScrollToEnd(ItemsControl control)
    {
        if (control.Items.Count == 0) return;

        // Works for both ListView and DataGrid
        control.Dispatcher.InvokeAsync(() =>
        {
            var last = control.Items[^1];
            switch (control)
            {
                case DataGrid dg:
                    dg.ScrollIntoView(last);
                    break;
                case ListBox lb:
                    lb.ScrollIntoView(last);
                    break;
                default:
                    // Generic fallback via ScrollViewer
                    if (FindScrollViewer(control) is ScrollViewer sv)
                        sv.ScrollToEnd();
                    break;
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
            if (child is ScrollViewer sv) return sv;
            var found = FindScrollViewer(child);
            if (found != null) return found;
        }
        return null;
    }
}
