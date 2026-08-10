using System.Text;
using System.Windows;
using System.Windows.Input;

namespace ClaudeSessions;

public enum ItemKind { User, Assistant, Thinking, Tool, Notice, Day }

public abstract class TimelineItem : Observable
{
    public DateTimeOffset? Time { get; init; }
    public bool IsSidechain { get; init; }
    public abstract ItemKind Kind { get; }

    public string TimeText => Time is { } t ? t.ToLocalTime().ToString("HH:mm:ss") : "";
    public string TimeTooltip => Time is { } t ? t.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm:ss") : "";

    /// <summary>Lower-cased haystack used by the in-transcript filter.</summary>
    public string Search { get; protected set; } = "";

    public virtual string CopyText => "";

    public static ICommand CopyCommand { get; } = new RelayCommand(p =>
    {
        if (p is TimelineItem item && !string.IsNullOrEmpty(item.CopyText))
        {
            try { Clipboard.SetText(item.CopyText); } catch { /* clipboard busy */ }
        }
    });

    protected static string Cap(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + $"\n\n… truncated for display ({s.Length - max:N0} more characters — use Copy for the full text)";
    }
}

public sealed class UserItem : TimelineItem
{
    public override ItemKind Kind => ItemKind.User;
    public string Text { get; init; } = "";
    public List<string> Images { get; init; } = new();
    public bool HasImages => Images.Count > 0;
    public override string CopyText => Text;
    public UserItem Seal() { Search = Text.ToLowerInvariant(); return this; }
}

public sealed class AssistantItem : TimelineItem
{
    public override ItemKind Kind => ItemKind.Assistant;
    public string Text { get; init; } = "";
    public string? Model { get; init; }
    public override string CopyText => Text;
    public AssistantItem Seal() { Search = Text.ToLowerInvariant(); return this; }
}

public sealed class ThinkingItem : TimelineItem
{
    private bool _expanded;
    public override ItemKind Kind => ItemKind.Thinking;
    public string Text { get; init; } = "";
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
    public string Preview { get; init; } = "";
    public override string CopyText => Text;
    public ThinkingItem Seal() { Search = Text.ToLowerInvariant(); return this; }
}

public sealed class ToolItem : TimelineItem
{
    private bool _expanded;
    private string _result = "";
    private bool _isError, _pending = true;
    private List<string> _images = new();

    public override ItemKind Kind => ItemKind.Tool;
    public string Name { get; init; } = "";
    public string Preview { get; init; } = "";
    public string InputJson { get; init; } = "";
    public string? ToolUseId { get; init; }

    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
    public string Result { get => _result; private set => Set(ref _result, value); }
    public bool IsError { get => _isError; private set { if (Set(ref _isError, value)) Raise(nameof(StatusGlyph)); } }
    public bool IsPending { get => _pending; private set { if (Set(ref _pending, value)) Raise(nameof(StatusGlyph)); } }
    public List<string> Images { get => _images; private set { Set(ref _images, value); Raise(nameof(HasImages)); } }
    public bool HasImages => Images.Count > 0;

    public string StatusGlyph => IsPending ? "…" : IsError ? "✕" : "✓";

    public void Complete(ContentBlock result)
    {
        Result = Cap(result.Text, 20_000);
        IsError = result.IsError;
        Images = result.Images;
        IsPending = false;
        Search = $"{Name} {Preview} {InputJson} {Result}".ToLowerInvariant();
    }

    public ToolItem Seal() { Search = $"{Name} {Preview} {InputJson}".ToLowerInvariant(); return this; }

    public override string CopyText =>
        $"$ {Name}\n{InputJson}\n\n--- result ---\n{Result}";
}

/// <summary>Date divider, emitted when the conversation crosses into a new day.</summary>
public sealed class DayItem : TimelineItem
{
    public override ItemKind Kind => ItemKind.Day;
    public string Text { get; init; } = "";
}

public sealed class NoticeItem : TimelineItem
{
    public override ItemKind Kind => ItemKind.Notice;
    public string Text { get; init; } = "";
    public string Label { get; init; } = "system";
    public bool IsError { get; init; }
    public override string CopyText => Text;
    public NoticeItem Seal() { Search = (Label + " " + Text).ToLowerInvariant(); return this; }
}

/// <summary>
/// Turns the flat record stream into renderable items, pairing each tool_use with the
/// tool_result that arrives in a later record. Stateful so it can be fed incrementally.
/// </summary>
public sealed class TimelineBuilder
{
    private readonly Dictionary<string, ToolItem> _openTools = new(StringComparer.Ordinal);

    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "mode", "permission-mode", "file-history-snapshot", "file-history-delta",
        "attachment", "queue-operation", "last-prompt", "ai-title", "summary",
    };

    private DateTime? _lastDay;

    public IEnumerable<TimelineItem> Consume(Entry e)
    {
        var produced = Render(e).ToList();
        if (produced.Count == 0) return produced;

        // Timestamps only show the time, so mark where the day rolls over.
        var day = e.Timestamp?.ToLocalTime().Date;
        if (day is { } d && d != _lastDay)
        {
            _lastDay = d;
            produced.Insert(0, new DayItem { Time = e.Timestamp, Text = DayLabel(d) });
        }

        return produced;
    }

    private static string DayLabel(DateTime day)
    {
        var today = DateTime.Today;
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        return day.Year == today.Year
            ? day.ToString("dddd d MMMM")
            : day.ToString("dddd d MMMM yyyy");
    }

    private IEnumerable<TimelineItem> Render(Entry e)
    {
        if (Skipped.Contains(e.Type)) yield break;

        switch (e.Type)
        {
            case "user":
                foreach (var item in FromUser(e)) yield return item;
                break;

            case "assistant":
                foreach (var item in FromAssistant(e)) yield return item;
                break;

            case "system":
                var text = e.Blocks.FirstOrDefault(b => b.Kind == "text")?.Text;
                if (!string.IsNullOrWhiteSpace(text))
                    yield return new NoticeItem
                    {
                        Time = e.Timestamp,
                        IsSidechain = e.IsSidechain,
                        Text = text!,
                        Label = e.Subtype ?? "system",
                    }.Seal();
                break;
        }
    }

    private IEnumerable<TimelineItem> FromUser(Entry e)
    {
        // Attach any tool results to the call they belong to before deciding what to show.
        var results = e.Blocks.Where(b => b.Kind == "tool_result").ToList();
        foreach (var r in results)
        {
            if (r.ToolUseId is { } id && _openTools.TryGetValue(id, out var tool))
            {
                tool.Complete(r);
                _openTools.Remove(id);
            }
        }

        if (TranscriptRules.IsNoise(e))
        {
            // Surface injected context as a notice rather than as something the user said.
            var injected = e.Blocks.FirstOrDefault(b => b.Kind == "text")?.Text;
            if (!string.IsNullOrWhiteSpace(injected) && results.Count == 0)
                yield return new NoticeItem
                {
                    Time = e.Timestamp,
                    IsSidechain = e.IsSidechain,
                    Text = Cap(injected, 8_000),
                    Label = "context",
                }.Seal();
            yield break;
        }

        var sb = new StringBuilder();
        var images = new List<string>();
        foreach (var b in e.Blocks)
        {
            if (b.Kind == "text" && !string.IsNullOrEmpty(b.Text)) sb.AppendLine(b.Text);
            images.AddRange(b.Images);
        }

        var body = sb.ToString().TrimEnd();
        if (body.Length == 0 && images.Count == 0) yield break;

        yield return new UserItem
        {
            Time = e.Timestamp,
            IsSidechain = e.IsSidechain,
            Text = Cap(body, 100_000),
            Images = images,
        }.Seal();
    }

    private IEnumerable<TimelineItem> FromAssistant(Entry e)
    {
        if (e.IsApiError)
        {
            var msg = e.Blocks.FirstOrDefault(b => b.Kind == "text")?.Text ?? e.ErrorCode ?? "API error";
            yield return new NoticeItem
            {
                Time = e.Timestamp,
                IsSidechain = e.IsSidechain,
                Text = msg,
                Label = e.ErrorCode ?? "error",
                IsError = true,
            }.Seal();
            yield break;
        }

        foreach (var b in e.Blocks)
        {
            switch (b.Kind)
            {
                case "text":
                    if (string.IsNullOrWhiteSpace(b.Text)) break;
                    yield return new AssistantItem
                    {
                        Time = e.Timestamp,
                        IsSidechain = e.IsSidechain,
                        Text = Cap(b.Text, 100_000),
                        Model = e.Model,
                    }.Seal();
                    break;

                case "thinking":
                    if (string.IsNullOrWhiteSpace(b.Text)) break;
                    yield return new ThinkingItem
                    {
                        Time = e.Timestamp,
                        IsSidechain = e.IsSidechain,
                        Text = Cap(b.Text, 60_000),
                        Preview = FirstLine(b.Text!),
                    }.Seal();
                    break;

                case "tool_use":
                    var tool = new ToolItem
                    {
                        Time = e.Timestamp,
                        IsSidechain = e.IsSidechain,
                        Name = b.ToolName ?? "tool",
                        Preview = b.InputPreview ?? "",
                        InputJson = Cap(b.InputJson, 20_000),
                        ToolUseId = b.ToolUseId,
                    }.Seal();
                    if (b.ToolUseId is { } id) _openTools[id] = tool;
                    yield return tool;
                    break;
            }
        }
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
        return line.Length > 120 ? line[..120] + "…" : line;
    }

    private static string Cap(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + $"\n\n… truncated for display ({s.Length - max:N0} more characters)";
    }
}
