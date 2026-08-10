using System.IO;
using System.Text;

namespace ClaudeSessions;

/// <summary>
/// Incremental reader over an append-only .jsonl transcript. Keeps a byte offset and an
/// unterminated-line buffer so a partially flushed record is never handed out half-parsed.
/// </summary>
public sealed class SessionTail
{
    private long _offset;
    private readonly List<byte> _pending = new();

    public SessionTail(string path) => Path = path;

    public string Path { get; }
    public long Offset => _offset;

    public void Rewind()
    {
        _offset = 0;
        _pending.Clear();
    }

    /// <summary>Reads everything appended since the last call. Never throws on a locked file.</summary>
    public List<Entry> ReadNew()
    {
        var result = new List<Entry>();
        byte[] chunk;

        try
        {
            using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _offset) { Rewind(); }      // file was replaced or truncated
            var available = fs.Length - _offset;
            if (available <= 0) return result;

            fs.Seek(_offset, SeekOrigin.Begin);
            chunk = new byte[available];
            var got = fs.ReadAtLeast(chunk, chunk.Length, throwOnEndOfStream: false);
            if (got < chunk.Length) Array.Resize(ref chunk, got);
            _offset += got;
        }
        catch (IOException) { return result; }
        catch (UnauthorizedAccessException) { return result; }

        _pending.AddRange(chunk);

        var start = 0;
        for (var i = 0; i < _pending.Count; i++)
        {
            if (_pending[i] != (byte)'\n') continue;
            var len = i - start;
            if (len > 0)
            {
                var line = Encoding.UTF8.GetString(CollectionsMarshalSpan(_pending, start, len));
                if (EntryParser.Parse(line) is { } entry) result.Add(entry);
            }
            start = i + 1;
        }

        if (start > 0) _pending.RemoveRange(0, start);
        return result;
    }

    private static ReadOnlySpan<byte> CollectionsMarshalSpan(List<byte> list, int start, int len)
        => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list).Slice(start, len);
}

/// <summary>Rolling summary of a session, updated one entry at a time as the file grows.</summary>
public sealed class SessionDigest
{
    public string? AiTitle, Cwd, GitBranch, Version, Model, LastPrompt, FirstPrompt, Effort;
    public DateTimeOffset? FirstSeen, LastSeen;
    public int UserTurns, AssistantTurns, ToolCalls, Records, Errors;
    public Usage Usage { get; } = new();
    public HashSet<string> Tools { get; } = new(StringComparer.Ordinal);

    public string DisplayTitle =>
        !string.IsNullOrWhiteSpace(AiTitle) ? AiTitle! :
        !string.IsNullOrWhiteSpace(FirstPrompt) ? Trim(FirstPrompt!, 90) :
        !string.IsNullOrWhiteSpace(LastPrompt) ? Trim(LastPrompt!, 90) :
        "(empty session)";

    public void Apply(Entry e)
    {
        Records++;

        if (e.Timestamp is { } t)
        {
            FirstSeen ??= t;
            if (LastSeen is null || t > LastSeen) LastSeen = t;
        }

        if (!string.IsNullOrEmpty(e.Cwd)) Cwd = e.Cwd;
        if (!string.IsNullOrEmpty(e.GitBranch)) GitBranch = e.GitBranch;
        if (!string.IsNullOrEmpty(e.Version)) Version = e.Version;
        if (!string.IsNullOrEmpty(e.Effort)) Effort = e.Effort;

        switch (e.Type)
        {
            case "ai-title":
                if (!string.IsNullOrWhiteSpace(e.Title)) AiTitle = e.Title;
                return;

            case "last-prompt":
                if (!string.IsNullOrWhiteSpace(e.LastPrompt)) LastPrompt = e.LastPrompt;
                return;

            case "user":
                if (!TranscriptRules.IsNoise(e))
                {
                    UserTurns++;
                    if (FirstPrompt is null)
                    {
                        var text = e.Blocks.FirstOrDefault(b => b.Kind == "text")?.Text;
                        if (!string.IsNullOrWhiteSpace(text)) FirstPrompt = text;
                    }
                }
                return;

            case "assistant":
                AssistantTurns++;
                if (e.IsApiError) Errors++;
                if (!string.IsNullOrEmpty(e.Model) && e.Model != "<synthetic>") Model = e.Model;
                if (e.Usage is { } u) Usage.Add(u);
                foreach (var b in e.Blocks)
                    if (b.Kind == "tool_use")
                    {
                        ToolCalls++;
                        if (b.ToolName is { } n) Tools.Add(n);
                    }
                return;
        }
    }

    private static string Trim(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length <= max ? s : s[..max] + "…";
    }
}

public static class TranscriptRules
{
    /// <summary>
    /// User records that are plumbing rather than something the person typed: tool results,
    /// injected reminders, and the harness's own meta turns.
    /// </summary>
    public static bool IsNoise(Entry e)
    {
        if (e.IsMeta) return true;
        if (e.Blocks.Count == 0) return true;
        if (e.Blocks.All(b => b.Kind == "tool_result")) return true;

        var text = e.Blocks.FirstOrDefault(b => b.Kind == "text")?.Text?.TrimStart();
        if (text is null) return false;

        return text.StartsWith("<system-reminder>", StringComparison.Ordinal)
            || text.StartsWith("Caveat:", StringComparison.Ordinal)
            || text.StartsWith("<local-command-stdout>", StringComparison.Ordinal)
            || text.StartsWith("<task-notification>", StringComparison.Ordinal);
    }
}
