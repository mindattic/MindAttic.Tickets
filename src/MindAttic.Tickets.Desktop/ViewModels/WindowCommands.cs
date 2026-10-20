using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>Keyboard commands the window handles itself (they touch focus, not data).</summary>
public static class WindowCommands
{
    public static readonly RoutedCommand FocusSearch = new(nameof(FocusSearch), typeof(WindowCommands));
    public static readonly RoutedCommand Escape = new(nameof(Escape), typeof(WindowCommands));
    public static readonly RoutedCommand ShowQueue = new(nameof(ShowQueue), typeof(WindowCommands));
    public static readonly RoutedCommand ShowTickets = new(nameof(ShowTickets), typeof(WindowCommands));
    public static readonly RoutedCommand Help = new(nameof(Help), typeof(WindowCommands));
}

public static class Converters
{
    public static readonly IValueConverter EmptyToCollapsed = new Converter(v => string.IsNullOrEmpty(v as string) ? Visibility.Collapsed : Visibility.Visible);
    public static readonly IValueConverter ZeroToVisible = new Converter(v => v is 0 ? Visibility.Visible : Visibility.Collapsed);

    private sealed class Converter(Func<object?, object> convert) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => convert(value);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
