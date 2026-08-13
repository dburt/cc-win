using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ClaudeSessions;

/// <summary>
/// A selectable, read-only view of a message body with just enough Markdown to keep
/// Claude's output legible: fenced code, headings, lists, quotes, and inline emphasis.
/// </summary>
public class MarkdownBlock : RichTextBox
{
    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.Register(nameof(Markdown), typeof(string), typeof(MarkdownBlock),
            new PropertyMetadata(null, (d, _) => ((MarkdownBlock)d).Rebuild()));

    public string? Markdown
    {
        get => (string?)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>Term to highlight in the rendered text; driven by the transcript search box.</summary>
    public static readonly DependencyProperty HighlightProperty =
        DependencyProperty.Register(nameof(Highlight), typeof(string), typeof(MarkdownBlock),
            new PropertyMetadata(null, (d, _) => ((MarkdownBlock)d).Rebuild()));

    public string? Highlight
    {
        get => (string?)GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    /// <summary>
    /// The document bakes in the current foreground and font metrics, so it has to be rebuilt
    /// when those arrive. They usually arrive *after* Markdown: XAML applies attributes in
    /// document order, and a freshly constructed RichTextBox still has the default black
    /// foreground at the moment the Markdown binding first fires.
    /// </summary>
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ForegroundProperty || e.Property == FontSizeProperty ||
            e.Property == FontFamilyProperty || e.Property == FontStyleProperty)
            Rebuild();
    }

    public MarkdownBlock()
    {
        IsReadOnly = true;
        IsDocumentEnabled = false;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(0);
        SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        SpellCheck.IsEnabled = false;
        AcceptsReturn = false;
        AcceptsTab = false;
    }

    private FontFamily Mono =>
        TryFindResource("MonoFont") as FontFamily ?? new FontFamily("Consolas");

    /// <summary>
    /// Themed document brushes are attached as resource references, not resolved instances: a
    /// theme switch replaces the dictionary entries, and anything baked in here would keep the
    /// old colour. It also sidesteps an ordering trap — swapping the foreground rebuilds this
    /// document synchronously, part-way through the swap, when the rest is still stale.
    /// </summary>
    private static T Themed<T>(T element, DependencyProperty property, string key)
        where T : FrameworkContentElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    private object? _builtWith;

    private void Rebuild()
    {
        // Cheap guard so the property-change fixups above cost at most one real parse.
        var key = (Markdown, Highlight, Foreground, FontFamily, FontSize, FontStyle);
        if (Equals(_builtWith, key)) return;
        _builtWith = key;

        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = FontFamily,
            FontSize = FontSize,
            Foreground = Foreground,
            LineHeight = FontSize * 1.68,
        };

        if (!string.IsNullOrEmpty(Markdown))
            foreach (var block in BuildBlocks(Markdown))
                doc.Blocks.Add(block);

        Document = doc;
        ApplyHighlight(doc);
    }

    /// <summary>
    /// Paints the search term wherever it lands in the finished document. Done over text
    /// pointers rather than during parsing so it works across inline formatting, and the
    /// ranges are collected before any are applied — applying splits runs and would
    /// invalidate pointers mid-walk.
    /// </summary>
    private void ApplyHighlight(FlowDocument doc)
    {
        var needle = Highlight?.Trim();
        if (string.IsNullOrEmpty(needle)) return;

        var brush = TryFindResource("MatchBrush") as Brush;
        if (brush is null) return;

        var hits = new List<TextRange>();
        var at = doc.ContentStart;

        while (at is not null && at.CompareTo(doc.ContentEnd) < 0)
        {
            if (at.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text)
            {
                at = at.GetNextContextPosition(LogicalDirection.Forward);
                continue;
            }

            var run = at.GetTextInRun(LogicalDirection.Forward);
            var idx = run.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var from = at.GetPositionAtOffset(idx);
                var to = from?.GetPositionAtOffset(needle.Length);
                if (from is null || to is null) break;
                hits.Add(new TextRange(from, to));
                at = to;
            }
            else
            {
                at = at.GetPositionAtOffset(run.Length) ?? at.GetNextContextPosition(LogicalDirection.Forward);
            }
        }

        foreach (var hit in hits)
            hit.ApplyPropertyValue(TextElement.BackgroundProperty, brush);
    }

    private IEnumerable<Block> BuildBlocks(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var fence = new List<string>();
                var lang = trimmed[3..].Trim();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    fence.Add(lines[i++]);
                if (i < lines.Length) i++;                 // consume closing fence
                yield return CodeBlock(string.Join("\n", fence), lang);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            if (Regex.Match(trimmed, @"^(#{1,6})\s+(.*)$") is { Success: true } h)
            {
                yield return Heading(h.Groups[2].Value, h.Groups[1].Value.Length);
                i++;
                continue;
            }

            if (Regex.IsMatch(trimmed, @"^([-*_])(\s*\1){2,}$"))
            {
                yield return Rule();
                i++;
                continue;
            }

            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal))
                    quote.Add(lines[i++].TrimStart().TrimStart('>').TrimStart());
                yield return Quote(string.Join("\n", quote));
                continue;
            }

            if (IsTableRow(line) && i + 1 < lines.Length && IsTableDivider(lines[i + 1]))
            {
                var rows = new List<string[]> { SplitRow(line) };
                i += 2;                                   // header + divider
                while (i < lines.Length && IsTableRow(lines[i])) rows.Add(SplitRow(lines[i++]));
                yield return TableBlock(rows);
                continue;
            }

            if (ListMarker(line) is { } marker)
            {
                yield return ListLine(marker);
                i++;
                continue;
            }

            var para = new List<string>();
            while (i < lines.Length
                   && !string.IsNullOrWhiteSpace(lines[i])
                   && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)
                   && ListMarker(lines[i]) is null
                   && !Regex.IsMatch(lines[i].TrimStart(), @"^#{1,6}\s")
                   && !(IsTableRow(lines[i]) && i + 1 < lines.Length && IsTableDivider(lines[i + 1])))
                para.Add(lines[i++]);

            yield return Paragraph(string.Join("\n", para), new Thickness(0, 0, 0, FontSize * 0.7));
        }
    }

    // ---- pipe tables ----

    private static bool IsTableRow(string line)
    {
        var t = line.Trim();
        return t.Contains('|') && t.Length > 1;
    }

    private static bool IsTableDivider(string line)
        => Regex.IsMatch(line.Trim(), @"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$");

    private static string[] SplitRow(string line)
    {
        var t = line.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];
        return t.Split('|').Select(c => c.Trim()).ToArray();
    }

    private Block TableBlock(List<string[]> rows)
    {
        var columns = rows.Max(r => r.Length);
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 12) };
        for (var c = 0; c < columns; c++) table.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        for (var r = 0; r < rows.Count; r++)
        {
            var isHeader = r == 0;
            var row = new TableRow();
            for (var c = 0; c < columns; c++)
            {
                var para = new Paragraph { Margin = new Thickness(0), FontSize = Math.Max(11, FontSize - 1) };
                AddInlines(para, c < rows[r].Length ? rows[r][c] : "");
                row.Cells.Add(Themed(new TableCell(para)
                {
                    Padding = new Thickness(9, 5, 9, 5),
                    BorderThickness = new Thickness(0, 0, 0, isHeader ? 1 : 0.4),
                    FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal,
                }, TableCell.BorderBrushProperty, "MutedBrush"));
            }
            group.Rows.Add(row);
        }

        table.RowGroups.Add(group);
        return table;
    }

    private record Marker(string Glyph, string Content, int Depth);

    private static Marker? ListMarker(string line)
    {
        var m = Regex.Match(line, @"^(\s*)([-*+]|\d+[.)])\s+(.*)$");
        if (!m.Success) return null;
        var depth = m.Groups[1].Value.Replace("\t", "  ").Length / 2;
        var raw = m.Groups[2].Value;
        var glyph = raw is "-" or "*" or "+" ? "•" : raw;
        return new Marker(glyph, m.Groups[3].Value, Math.Min(depth, 6));
    }

    private Block ListLine(Marker marker)
    {
        var p = new Paragraph
        {
            Margin = new Thickness(14 + marker.Depth * 20, 0, 0, FontSize * 0.5),
            TextIndent = -14,
        };
        p.Inlines.Add(Themed(new Run(marker.Glyph + "  "), TextElement.ForegroundProperty, "LinkBrush"));
        AddInlines(p, marker.Content);
        return p;
    }

    private Block Heading(string content, int level)
    {
        var p = new Paragraph
        {
            Margin = new Thickness(0, FontSize * (level <= 2 ? 1.15 : 0.9), 0, FontSize * 0.45),
            FontSize = FontSize + (level <= 1 ? 5 : level == 2 ? 3 : 1),
            FontWeight = FontWeights.Bold,
        };
        AddInlines(p, content);
        return p;
    }

    private Block Quote(string content)
    {
        var p = Paragraph(content, new Thickness(0, 2, 0, FontSize * 0.7));
        p.BorderThickness = new Thickness(3, 0, 0, 0);
        p.Padding = new Thickness(10, 2, 0, 2);
        Themed(p, Block.BorderBrushProperty, "MutedBrush");
        Themed(p, TextElement.ForegroundProperty, "MutedBrush");
        return p;
    }

    private Block Rule() => Themed(new Paragraph
    {
        Margin = new Thickness(0, 6, 0, 10),
        BorderThickness = new Thickness(0, 0, 0, 1),
        FontSize = 1,
    }, Block.BorderBrushProperty, "MutedBrush");

    private Block CodeBlock(string code, string lang)
    {
        var p = new Paragraph(new Run(code))
        {
            FontFamily = Mono,
            FontSize = Math.Max(11, FontSize - 1),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 2, 0, 10),
            LineHeight = FontSize * 1.35,
        };
        Themed(p, TextElement.BackgroundProperty, "CodeBackgroundBrush");
        if (!string.IsNullOrWhiteSpace(lang))
            p.Inlines.InsertBefore(p.Inlines.FirstInline,
                Themed(new Run(lang + "\n") { FontSize = 10 }, TextElement.ForegroundProperty, "MutedBrush"));
        return p;
    }

    private Paragraph Paragraph(string content, Thickness margin)
    {
        var p = new Paragraph { Margin = margin };
        AddInlines(p, content);
        return p;
    }

    private static readonly Regex Inline = new(
        @"(?<code>`[^`\n]+`)" +
        @"|(?<bold>\*\*(?!\s)[^\n]+?\*\*)" +
        @"|(?<ital>(?<![\w*])\*(?!\s)[^*\n]+?\*(?![\w*]))" +
        @"|(?<link>\[[^\]\n]*\]\([^)\s]+\))",
        RegexOptions.Compiled);

    private void AddInlines(Paragraph p, string content)
    {
        var pos = 0;
        foreach (Match m in Inline.Matches(content))
        {
            if (m.Index > pos) p.Inlines.Add(new Run(content[pos..m.Index]));

            if (m.Groups["code"].Success)
            {
                p.Inlines.Add(Themed(new Run(m.Value.Trim('`'))
                {
                    FontFamily = Mono,
                    FontSize = Math.Max(11, FontSize - 1),
                }, TextElement.BackgroundProperty, "CodeBackgroundBrush"));
            }
            else if (m.Groups["bold"].Success)
            {
                p.Inlines.Add(new Bold(new Run(m.Value[2..^2])));
            }
            else if (m.Groups["ital"].Success)
            {
                p.Inlines.Add(new Italic(new Run(m.Value[1..^1])));
            }
            else if (m.Groups["link"].Success)
            {
                var split = m.Value.IndexOf("](", StringComparison.Ordinal);
                var label = m.Value[1..split];
                var url = m.Value[(split + 2)..^1];
                p.Inlines.Add(Themed(new Run(string.IsNullOrEmpty(label) ? url : label),
                                     TextElement.ForegroundProperty, "LinkBrush"));
                if (!string.IsNullOrEmpty(label))
                    p.Inlines.Add(Themed(new Run($" ({url})") { FontSize = Math.Max(10, FontSize - 2) },
                                         TextElement.ForegroundProperty, "MutedBrush"));
            }

            pos = m.Index + m.Length;
        }

        if (pos < content.Length) p.Inlines.Add(new Run(content[pos..]));
    }
}
