using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ClaudeSessions;

public sealed class TimelineTemplateSelector : DataTemplateSelector
{
    public DataTemplate? User { get; set; }
    public DataTemplate? Assistant { get; set; }
    public DataTemplate? Thinking { get; set; }
    public DataTemplate? Tool { get; set; }
    public DataTemplate? Notice { get; set; }
    public DataTemplate? Day { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) => item switch
    {
        UserItem => User,
        AssistantItem => Assistant,
        ThinkingItem => Thinking,
        ToolItem => Tool,
        NoticeItem => Notice,
        DayItem => Day,
        _ => base.SelectTemplate(item, container),
    };
}

/// <summary>Decodes an inline base64 screenshot into something WPF can show.</summary>
public sealed class Base64ImageConverter : IValueConverter
{
    // List virtualization re-runs the converter every time a row scrolls back into view;
    // decoding a multi-megabyte screenshot each time is far too slow. Key by reference so
    // we never hash the payload, and let entries die with the timeline they belong to.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, BitmapImage> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string b64 || b64.Length == 0) return null;
        if (Cache.TryGetValue(b64, out var cached)) return cached;
        try
        {
            var bytes = System.Convert.FromBase64String(b64);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            Cache.AddOrUpdate(b64, image);
            return image;
        }
        catch { return null; }
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v is not true;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => v is not true;
}

/// <summary>Collapses an element when the bound string is null or blank.</summary>
public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c)
        => string.IsNullOrWhiteSpace(v as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public static class Format
{
    public static string Bytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        _ => $"{b / (1024.0 * 1024):0.##} MB",
    };

    public static string Tokens(long n) => n switch
    {
        < 1000 => n.ToString("N0"),
        < 1_000_000 => $"{n / 1000.0:0.#}k",
        _ => $"{n / 1_000_000.0:0.##}M",
    };

    public static string Ago(DateTimeOffset? when)
    {
        if (when is not { } t) return "";
        var d = DateTimeOffset.Now - t;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        return d switch
        {
            { TotalSeconds: < 45 } => "just now",
            { TotalMinutes: < 60 } => $"{(int)d.TotalMinutes}m ago",
            { TotalHours: < 24 } => $"{(int)d.TotalHours}h ago",
            { TotalDays: < 7 } => $"{(int)d.TotalDays}d ago",
            _ => t.ToLocalTime().ToString("d MMM yyyy"),
        };
    }

    public static string Duration(DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is not { } a || to is not { } b || b < a) return "—";
        var d = b - a;
        if (d.TotalMinutes < 1) return $"{d.TotalSeconds:0}s";
        if (d.TotalHours < 1) return $"{d.TotalMinutes:0}m";
        return $"{(int)d.TotalHours}h {d.Minutes}m";
    }
}
