using System.IO;
using System.Text;

namespace ClaudeSessions;

/// <summary>One indexed line of a transcript, positioned so the UI can scroll straight to it.</summary>
public sealed class IndexRecord
{
    public IndexRecord(int ordinal, ItemKind kind, DateTimeOffset? time, string text)
    {
        Ordinal = ordinal;
        Kind = kind;
        Time = time;
        SetText(text);
    }

    /// <summary>Index into the session's full timeline — the same ordinal the transcript uses.</summary>
    public int Ordinal { get; }
    public ItemKind Kind { get; }
    public DateTimeOffset? Time { get; }
    public string Text { get; private set; } = "";
    public string Lower { get; private set; } = "";

    public void SetText(string text)
    {
        Text = text ?? "";
        Lower = Text.ToLowerInvariant();
    }
}

/// <summary>
/// A searchable projection of one session, kept in step with the file as it grows.
/// Uses the same tail reader and timeline builder as the transcript view, so record
/// ordinals line up exactly with the items the transcript renders.
/// </summary>
public sealed class SessionIndex
{
    private readonly SessionTail _tail;
    private readonly TimelineBuilder _builder = new();
    private readonly List<IndexRecord> _records = new();
    private readonly Dictionary<ToolItem, int> _pendingTools = new();

    public SessionIndex(string path)
    {
        _tail = new SessionTail(path);
        _builder.ToolCompleted += OnToolCompleted;
    }

    public IReadOnlyList<IndexRecord> Records => _records;
    public long Chars { get; private set; }
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Reads whatever has been appended since the last call. Cheap when nothing changed.</summary>
    public void Pump()
    {
        foreach (var entry in _tail.ReadNew())
            foreach (var item in _builder.Consume(entry))
            {
                var record = new IndexRecord(_records.Count, item.Kind, item.Time, TextOf(item));
                _records.Add(record);
                Chars += record.Text.Length;

                // Hold the tool briefly so its result can be folded in; dropping the reference
                // afterwards matters, because a completed tool may carry base64 screenshots.
                if (item is ToolItem tool) _pendingTools[tool] = record.Ordinal;
            }
    }

    private void OnToolCompleted(ToolItem tool)
    {
        if (!_pendingTools.TryGetValue(tool, out var ordinal)) return;
        _pendingTools.Remove(tool);

        var record = _records[ordinal];
        Chars -= record.Text.Length;
        record.SetText($"{tool.Name} {tool.Preview}\n{tool.InputJson}\n{tool.Result}");
        Chars += record.Text.Length;
    }

    private static string TextOf(TimelineItem item) => item switch
    {
        UserItem u => u.Text,
        AssistantItem a => a.Text,
        ThinkingItem t => t.Text,
        NoticeItem n => $"{n.Label} {n.Text}",
        ToolItem tool => $"{tool.Name} {tool.Preview}\n{tool.InputJson}",
        _ => "",
    };
}

public sealed class SearchHit
{
    public required SessionVM Session { get; init; }
    public required int Ordinal { get; init; }
    public required ItemKind Kind { get; init; }
    public DateTimeOffset? Time { get; init; }

    // Split so the row can render the match inline without re-parsing anything.
    public required string Before { get; init; }
    public required string Match { get; init; }
    public required string After { get; init; }

    public string SessionTitle => Session.Title;
    public string ProjectName => Session.Project.Name;
    public string TimeText => Time is { } t ? t.ToLocalTime().ToString("d MMM HH:mm") : "";

    public string KindLabel => Kind switch
    {
        ItemKind.User => "you",
        ItemKind.Assistant => "claude",
        ItemKind.Thinking => "thinking",
        ItemKind.Tool => "tool",
        _ => "system",
    };
}

/// <summary>
/// MatchedPaths covers every session containing the term, including ones whose hits fell
/// past the display cap — the session list uses it, so it must not be derived from Hits.
/// </summary>
public sealed record SearchOutcome(
    List<SearchHit> Hits,
    int TotalMatches,
    HashSet<string> MatchedPaths,
    bool Truncated)
{
    public int SessionsMatched => MatchedPaths.Count;
}

/// <summary>
/// Content search across every discovered session. Indexes lazily on first search and then
/// only reads the bytes each file has grown by, so repeat searches stay cheap.
/// </summary>
public sealed class SearchEngine
{
    private readonly Dictionary<string, SessionIndex> _indexes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rough ceiling on retained transcript text before the coldest sessions are dropped.</summary>
    private const long CharBudget = 32_000_000;

    public const int MinQueryLength = 2;

    public SearchOutcome Search(string query, IReadOnlyList<SessionVM> sessions, int max, CancellationToken ct)
    {
        var hits = new List<SearchHit>();
        var needle = query.Trim();
        var total = 0;
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (needle.Length < MinQueryLength) return new SearchOutcome(hits, 0, matched, false);
        var lower = needle.ToLowerInvariant();

        foreach (var session in sessions)
        {
            ct.ThrowIfCancellationRequested();

            SessionIndex index;
            try
            {
                if (!_indexes.TryGetValue(session.Path, out index!))
                    _indexes[session.Path] = index = new SessionIndex(session.Path);
                index.LastUsedUtc = DateTime.UtcNow;
                index.Pump();
            }
            catch (IOException) { continue; }

            foreach (var record in index.Records)
            {
                var at = record.Lower.IndexOf(lower, StringComparison.Ordinal);
                if (at < 0) continue;

                total++;
                matched.Add(session.Path);
                if (hits.Count < max) hits.Add(Build(session, record, at, needle.Length));
            }
        }

        Evict();
        return new SearchOutcome(hits, total, matched, total > hits.Count);
    }

    /// <summary>Forget a session's index when its file disappears.</summary>
    public void Forget(string path) => _indexes.Remove(path);

    private void Evict()
    {
        var total = _indexes.Values.Sum(i => i.Chars);
        if (total <= CharBudget) return;

        foreach (var entry in _indexes.OrderBy(e => e.Value.LastUsedUtc).ToList())
        {
            if (total <= CharBudget) break;
            total -= entry.Value.Chars;
            _indexes.Remove(entry.Key);
        }
    }

    private static SearchHit Build(SessionVM session, IndexRecord record, int at, int length)
    {
        const int lead = 40;
        const int trail = 86;

        var text = record.Text;
        var from = Math.Max(0, at - lead);
        var to = Math.Min(text.Length, at + length + trail);

        var before = Flatten(text[from..at]);
        var match = text.Substring(at, length);
        var after = Flatten(text[(at + length)..to]);

        return new SearchHit
        {
            Session = session,
            Ordinal = record.Ordinal,
            Kind = record.Kind,
            Time = record.Time,
            Before = (from > 0 ? "…" : "") + before,
            Match = match,
            After = after + (to < text.Length ? "…" : ""),
        };
    }

    private static string Flatten(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!space && sb.Length > 0) sb.Append(' ');
                space = true;
            }
            else
            {
                sb.Append(c);
                space = false;
            }
        }
        return sb.ToString();
    }
}
