using System.Text;
using System.Text.Json;

namespace ClaudeSessions;

/// <summary>One content block inside a message (text / thinking / tool_use / tool_result).</summary>
public sealed class ContentBlock
{
    public string Kind = "";
    public string? Text;
    public string? ToolName;
    public string? ToolUseId;
    public string? InputJson;
    public string? InputPreview;
    public bool IsError;
    public List<string> Images = new();
}

public sealed class Usage
{
    public long Input, Output, CacheCreate, CacheRead;
    public long Total => Input + Output + CacheCreate + CacheRead;

    public void Add(Usage o)
    {
        Input += o.Input; Output += o.Output;
        CacheCreate += o.CacheCreate; CacheRead += o.CacheRead;
    }

    /// <summary>Null when the model isn't in the pricing table — never guess a rate.</summary>
    public double? EstimateCostUsd(string? model)
    {
        if (ModelPricing.Find(model) is not { } rate) return null;
        return Input / 1_000_000.0 * rate.InputPerMTok
             + Output / 1_000_000.0 * rate.OutputPerMTok
             + CacheCreate / 1_000_000.0 * rate.InputPerMTok * ModelPricing.CacheWriteMultiplier
             + CacheRead / 1_000_000.0 * rate.InputPerMTok * ModelPricing.CacheReadMultiplier;
    }
}

/// <summary>
/// Hardcoded USD-per-million-token rates, keyed by model-id prefix (snapshot dates and
/// dateless aliases both start with the family prefix). Cache write/read costs aren't tracked
/// per model — Anthropic applies the same 1.25x / 0.1x multiplier on input price across the
/// catalog, so they're derived rather than duplicated. Snapshot: platform.claude.com pricing,
/// August 2026. Update this table when new models ship.
/// </summary>
public static class ModelPricing
{
    public const double CacheWriteMultiplier = 1.25;
    public const double CacheReadMultiplier = 0.1;

    /// <summary>Static USD→AUD rate — not live, update occasionally.</summary>
    public const double UsdToAud = 1.55;

    private static readonly (string Prefix, double InputPerMTok, double OutputPerMTok)[] Rates =
        new (string Prefix, double InputPerMTok, double OutputPerMTok)[]
        {
            ("claude-fable-5", 10, 50),
            ("claude-mythos-5", 10, 50),
            ("claude-mythos-preview", 10, 50),
            ("claude-opus-5", 5, 25),
            ("claude-opus-4-8", 5, 25),
            ("claude-opus-4-7", 5, 25),
            ("claude-opus-4-6", 5, 25),
            ("claude-opus-4-5", 5, 25),
            ("claude-opus-4-1", 15, 75),
            ("claude-opus-4-", 15, 75),      // dated Opus 4 snapshot, e.g. claude-opus-4-20250514
            ("claude-3-opus", 15, 75),
            ("claude-sonnet-5", 2, 10),
            ("claude-sonnet-4-6", 3, 15),
            ("claude-sonnet-4-5", 3, 15),
            ("claude-sonnet-4-", 3, 15),     // dated Sonnet 4 snapshot
            ("claude-3-7-sonnet", 3, 15),
            ("claude-3-5-sonnet", 3, 15),
            ("claude-3-sonnet", 3, 15),
            ("claude-haiku-4-5", 1, 5),
            ("claude-3-5-haiku", 0.8, 4),
            ("claude-3-haiku", 0.25, 1.25),
        }.OrderByDescending(r => r.Prefix.Length).ToArray();

    public static (double InputPerMTok, double OutputPerMTok)? Find(string? model)
    {
        if (string.IsNullOrEmpty(model)) return null;
        foreach (var r in Rates)
            if (model.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase))
                return (r.InputPerMTok, r.OutputPerMTok);
        return null;
    }
}

/// <summary>A single parsed line of a session .jsonl transcript.</summary>
public sealed class Entry
{
    public string Type = "";
    public string? Uuid, ParentUuid, SessionId, Cwd, GitBranch, Version;
    public string? Model, Role, Subtype, Effort, Slug, Title, LastPrompt, ErrorCode;
    public DateTimeOffset? Timestamp;
    public bool IsSidechain, IsMeta, IsApiError;
    public int? DurationMs;
    public Usage? Usage;
    public List<ContentBlock> Blocks = new();

    public bool IsConversational => Type is "user" or "assistant";
}

public static class EntryParser
{
    public static Entry? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            return FromRoot(doc.RootElement);
        }
        catch (JsonException)
        {
            return null; // torn / partial line — the tailer will re-read it once complete
        }
    }

    private static Entry FromRoot(JsonElement r)
    {
        var e = new Entry
        {
            Type = Str(r, "type") ?? "",
            Uuid = Str(r, "uuid"),
            ParentUuid = Str(r, "parentUuid"),
            SessionId = Str(r, "sessionId") ?? Str(r, "session_id"),
            Cwd = Str(r, "cwd"),
            GitBranch = Str(r, "gitBranch"),
            Version = Str(r, "version"),
            Subtype = Str(r, "subtype"),
            Effort = Str(r, "effort"),
            Slug = Str(r, "slug"),
            Title = Str(r, "aiTitle"),
            LastPrompt = Str(r, "lastPrompt"),
            ErrorCode = Str(r, "error"),
            IsSidechain = Bool(r, "isSidechain"),
            IsMeta = Bool(r, "isMeta"),
            IsApiError = Bool(r, "isApiErrorMessage"),
        };

        if (r.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(ts.GetString(), out var when))
            e.Timestamp = when;

        if (r.TryGetProperty("durationMs", out var dm) && dm.TryGetInt32(out var ms))
            e.DurationMs = ms;

        // system-level notices carry their text at the top level
        if (r.TryGetProperty("content", out var topContent) && topContent.ValueKind == JsonValueKind.String)
            e.Blocks.Add(new ContentBlock { Kind = "text", Text = topContent.GetString() });

        if (r.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            e.Role = Str(m, "role");
            e.Model = Str(m, "model");
            if (m.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                e.Usage = ReadUsage(u);

            if (m.TryGetProperty("content", out var c))
            {
                if (c.ValueKind == JsonValueKind.String)
                    e.Blocks.Add(new ContentBlock { Kind = "text", Text = c.GetString() });
                else if (c.ValueKind == JsonValueKind.Array)
                    foreach (var p in c.EnumerateArray())
                        if (ReadBlock(p) is { } b) e.Blocks.Add(b);
            }
        }

        return e;
    }

    private static Usage ReadUsage(JsonElement u) => new()
    {
        Input = Num(u, "input_tokens"),
        Output = Num(u, "output_tokens"),
        CacheCreate = Num(u, "cache_creation_input_tokens"),
        CacheRead = Num(u, "cache_read_input_tokens"),
    };

    private static ContentBlock? ReadBlock(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return null;
        var kind = Str(p, "type") ?? "";

        switch (kind)
        {
            case "text":
                return new ContentBlock { Kind = "text", Text = Str(p, "text") };

            case "thinking":
                var think = Str(p, "thinking");
                return string.IsNullOrWhiteSpace(think) ? null : new ContentBlock { Kind = "thinking", Text = think };

            case "tool_use":
                var blk = new ContentBlock
                {
                    Kind = "tool_use",
                    ToolName = Str(p, "name") ?? "tool",
                    ToolUseId = Str(p, "id"),
                };
                if (p.TryGetProperty("input", out var input))
                {
                    blk.InputJson = Pretty(input);
                    blk.InputPreview = PreviewOf(blk.ToolName, input);
                }
                return blk;

            case "tool_result":
                var res = new ContentBlock
                {
                    Kind = "tool_result",
                    ToolUseId = Str(p, "tool_use_id"),
                    IsError = Bool(p, "is_error"),
                };
                if (p.TryGetProperty("content", out var rc))
                    res.Text = FlattenResult(rc, res.Images);
                return res;

            case "image":
                var img = new ContentBlock { Kind = "image" };
                if (TryImage(p, out var data)) img.Images.Add(data);
                return img;

            default:
                return new ContentBlock { Kind = kind, Text = Pretty(p) };
        }
    }

    /// <summary>tool_result content is either a plain string or an array of typed parts.</summary>
    private static string FlattenResult(JsonElement rc, List<string> images)
    {
        if (rc.ValueKind == JsonValueKind.String) return rc.GetString() ?? "";
        if (rc.ValueKind != JsonValueKind.Array) return Pretty(rc);

        var sb = new StringBuilder();
        foreach (var part in rc.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) { sb.AppendLine(part.ToString()); continue; }
            switch (Str(part, "type"))
            {
                case "text":
                    sb.AppendLine(Str(part, "text"));
                    break;
                case "image":
                    if (TryImage(part, out var data)) images.Add(data);
                    else sb.AppendLine("[image]");
                    break;
                case "tool_reference":
                    sb.AppendLine($"[tool reference: {Str(part, "name") ?? "?"}]");
                    break;
                default:
                    sb.AppendLine(Pretty(part));
                    break;
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static bool TryImage(JsonElement p, out string base64)
    {
        base64 = "";
        if (!p.TryGetProperty("source", out var s) || s.ValueKind != JsonValueKind.Object) return false;
        if (Str(s, "type") != "base64") return false;
        var d = Str(s, "data");
        if (string.IsNullOrEmpty(d)) return false;
        base64 = d;
        return true;
    }

    /// <summary>A one-line gist of a tool call, so the collapsed row is still informative.</summary>
    private static string PreviewOf(string? tool, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return Single(input.ToString());

        foreach (var key in new[] { "command", "file_path", "pattern", "path", "url", "query", "prompt", "description", "skill" })
            if (input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var text = v.GetString() ?? "";
                if (text.Length > 0) return Single(text);
            }

        var parts = new List<string>();
        foreach (var prop in input.EnumerateObject())
        {
            parts.Add($"{prop.Name}={Single(prop.Value.ToString())}");
            if (parts.Count == 3) break;
        }
        return string.Join("  ", parts);
    }

    private static string Single(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length > 200 ? s[..200] + "…" : s;
    }

    private static string Pretty(JsonElement e)
    {
        try { return JsonSerializer.Serialize(e, PrettyOptions); }
        catch { return e.ToString(); }
    }

    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    private static string? Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static long Num(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
