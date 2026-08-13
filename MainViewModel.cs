using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace ClaudeSessions;

public sealed record FileFact(string Path, string ProjectDir, string Root, long Length, DateTime LastWriteUtc);

public sealed class SessionVM : Observable
{
    private readonly SessionTail _tail;
    private bool _visible = true;

    public SessionVM(FileFact fact, ProjectVM project)
    {
        Path = fact.Path;
        Project = project;
        RootLabel = Discovery.DescribeRoot(fact.Root);
        SessionId = System.IO.Path.GetFileNameWithoutExtension(fact.Path);
        Length = fact.Length;
        LastWriteUtc = fact.LastWriteUtc;
        _tail = new SessionTail(Path);
    }

    public string Path { get; }
    public string SessionId { get; }
    public string RootLabel { get; }
    public ProjectVM Project { get; }
    public SessionDigest Digest { get; private set; } = new();

    public long Length { get; private set; }
    public DateTime LastWriteUtc { get; private set; }

    public bool IsVisible { get => _visible; set => Set(ref _visible, value); }

    /// <summary>Leaf nodes never expand — present only so the shared TreeViewItem style can bind.</summary>
    public bool IsExpanded { get; set; }

    public string Title => Digest.DisplayTitle;
    public string ShortId => SessionId.Length >= 8 ? SessionId[..8] : SessionId;
    public string Ago => Format.Ago(LastActivity);
    public string SizeText => Format.Bytes(Length);
    public string TurnsText => $"{Digest.UserTurns} turn{(Digest.UserTurns == 1 ? "" : "s")}";
    public string ModelText => Digest.Model ?? "";
    public string TokensText => Format.Tokens(Digest.Usage.Total);
    public string BranchText => Digest.GitBranch ?? "";
    public string CwdText => Digest.Cwd is { } c ? Discovery.PrettyPath(c) : Project.DisplayPath;

    public DateTimeOffset? LastActivity => Digest.LastSeen ?? new DateTimeOffset(LastWriteUtc, TimeSpan.Zero);

    /// <summary>Written to within the last two minutes — i.e. a session that is probably running now.</summary>
    public bool IsLive => (DateTime.UtcNow - LastWriteUtc).TotalSeconds < 120;

    public string Haystack => $"{Title} {SessionId} {CwdText} {BranchText} {ModelText}".ToLowerInvariant();

    /// <summary>Reads any newly appended records into the digest. Safe to call off the UI thread.</summary>
    public bool Pump(FileFact? fact = null)
    {
        if (fact is not null)
        {
            Length = fact.Length;
            LastWriteUtc = fact.LastWriteUtc;
        }

        var entries = _tail.ReadNew();
        if (entries.Count == 0) return false;

        foreach (var e in entries) Digest.Apply(e);
        return true;
    }

    public void ResetDigest()
    {
        _tail.Rewind();
        Digest = new SessionDigest();
    }

    public void NotifyChanged()
    {
        Raise(nameof(Title)); Raise(nameof(Ago)); Raise(nameof(SizeText));
        Raise(nameof(TurnsText)); Raise(nameof(ModelText)); Raise(nameof(TokensText));
        Raise(nameof(BranchText)); Raise(nameof(CwdText)); Raise(nameof(IsLive));
        Raise(nameof(LastActivity)); Raise(nameof(Digest));
    }
}

public sealed class ProjectVM : Observable
{
    private bool _visible = true;
    private bool _expanded = true;

    public ProjectVM(string dir, string root)
    {
        Dir = dir;
        Root = root;
        RootLabel = Discovery.DescribeRoot(root);
        FolderName = System.IO.Path.GetFileName(dir);
        DisplayPath = Discovery.PrettyPath(Discovery.DecodeProjectFolder(FolderName));
    }

    public string Dir { get; }
    public string Root { get; }
    public string RootLabel { get; }
    public string FolderName { get; }
    public string DisplayPath { get; private set; }

    public ObservableCollection<SessionVM> Sessions { get; } = new();

    public bool IsVisible { get => _visible; set => Set(ref _visible, value); }
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }

    public string Name => System.IO.Path.GetFileName(DisplayPath.TrimEnd('/', '\\')) is { Length: > 0 } n ? n : DisplayPath;
    public string CountText => $"{Sessions.Count(s => s.IsVisible)}";
    public bool HasLive => Sessions.Any(s => s.IsLive);
    public DateTime Newest => Sessions.Count == 0 ? DateTime.MinValue : Sessions.Max(s => s.LastWriteUtc);

    /// <summary>Prefer the cwd recorded inside the transcripts over the lossy folder-name encoding.</summary>
    public void AdoptRealPath()
    {
        var cwd = Sessions.Select(s => s.Digest.Cwd).FirstOrDefault(c => !string.IsNullOrEmpty(c));
        if (cwd is null) return;
        var pretty = Discovery.PrettyPath(cwd);
        if (pretty == DisplayPath) return;
        DisplayPath = pretty;
        Raise(nameof(DisplayPath));
        Raise(nameof(Name));
    }

    public void NotifyChanged()
    {
        Raise(nameof(CountText));
        Raise(nameof(HasLive));
    }
}

public sealed class MainViewModel : Observable
{
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, ProjectVM> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SessionVM> _sessions = new(StringComparer.OrdinalIgnoreCase);

    private SessionTail? _transcriptTail;
    private TimelineBuilder _builder = new();
    private bool _polling;

    private readonly SearchEngine _search = new();
    private readonly DispatcherTimer _searchDebounce;
    private CancellationTokenSource? _searchCancel;
    private HashSet<string> _contentMatches = new(StringComparer.OrdinalIgnoreCase);
    private bool _searching;
    private string _searchSummary = "";
    private Task _load = Task.CompletedTask;

    private SessionVM? _selected;
    private string _sessionFilter = "";
    private string _transcriptFilter = "";
    private bool _showThinking = true, _showTools = true, _showNotices, _follow = true;
    private string _status = "Starting up…";
    private bool _autoSelected;

    public MainViewModel()
    {
        Timeline = new ObservableCollection<TimelineItem>();
        TimelineView = BuildView(Timeline);

        RefreshCommand = new RelayCommand(() => _ = PollAsync(force: true));
        RevealCommand = new RelayCommand(Reveal, _ => Selected is not null);
        CopyTextCommand = new RelayCommand(
            p => { try { Clipboard.SetText((string)p!); } catch { } },
            p => p is string { Length: > 0 });
        ExportCommand = new RelayCommand(Export, _ => Selected is not null);
        ExpandAllToolsCommand = new RelayCommand(() => SetToolExpansion(true));
        CollapseAllToolsCommand = new RelayCommand(() => SetToolExpansion(false));

        JumpCommand = new RelayCommand(p => { if (p is SearchHit hit) _ = JumpAsync(hit); });
        ClearSearchCommand = new RelayCommand(() => SessionFilter = "");
        CycleThemeCommand = new RelayCommand(() => Theme = Theme switch
        {
            ThemePreference.System => ThemePreference.Light,
            ThemePreference.Light => ThemePreference.Dark,
            _ => ThemePreference.System,
        });

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => _ = PollAsync();

        // Typing filters the session list instantly; scanning transcript bodies waits for a pause.
        _searchDebounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(280) };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); _ = RunContentSearchAsync(); };

        SearchResults.CollectionChanged += (_, _) => Raise(nameof(HasSearchResults));
    }

    public ObservableCollection<ProjectVM> Projects { get; } = new();
    public ObservableCollection<TimelineItem> Timeline { get; private set; }
    public ICollectionView TimelineView { get; private set; }

    public ICommand RefreshCommand { get; }
    public ICommand RevealCommand { get; }
    /// <summary>Copies its parameter to the clipboard; used by the detail panel's copy icons.</summary>
    public ICommand CopyTextCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ExpandAllToolsCommand { get; }
    public ICommand CollapseAllToolsCommand { get; }
    public ICommand JumpCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand CycleThemeCommand { get; }

    public event EventHandler? ScrollToEndRequested;
    public event EventHandler<int>? ScrollToItemRequested;

    /// <summary>Optional "--session &lt;text&gt;" filter used to pick the session opened at startup.</summary>
    public string? StartupSessionQuery { get; set; }

    /// <summary>Extra "--root &lt;dir&gt;" folders to scan, e.g. an archived or copied history tree.</summary>
    public List<string> ExtraRoots { get; } = new();

    public string Status { get => _status; private set => Set(ref _status, value); }

    public SessionVM? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(HasSelection));
            Raise(nameof(SelectedDetail));
            _load = LoadTranscriptAsync();
        }
    }

    public bool HasSelection => Selected is not null;
    public SessionDetail? SelectedDetail => Selected is { } s ? new SessionDetail(s) : null;

    public string SessionFilter
    {
        get => _sessionFilter;
        set
        {
            if (!Set(ref _sessionFilter, value)) return;
            ApplySessionFilter();
            Raise(nameof(HighlightTerm));
            QueueContentSearch();
        }
    }

    public string TranscriptFilter
    {
        get => _transcriptFilter;
        set
        {
            if (!Set(ref _transcriptFilter, value)) return;
            TimelineView.Refresh();
            Raise(nameof(VisibleCountText));
            Raise(nameof(HighlightTerm));
        }
    }

    /// <summary>
    /// What the transcript paints as a match. The in-transcript filter wins; otherwise a
    /// content search term is highlighted too, so a jumped-to hit is visible on arrival.
    /// </summary>
    public string HighlightTerm =>
        !string.IsNullOrWhiteSpace(TranscriptFilter) ? TranscriptFilter
        : SessionFilter.Trim().Length >= SearchEngine.MinQueryLength ? SessionFilter
        : "";

    public ObservableCollection<SearchHit> SearchResults { get; } = new();

    public bool HasSearchResults => SearchResults.Count > 0;
    public bool IsSearching { get => _searching; private set => Set(ref _searching, value); }
    public string SearchSummary { get => _searchSummary; private set => Set(ref _searchSummary, value); }

    public bool ShowThinking { get => _showThinking; set { if (Set(ref _showThinking, value)) RefreshTimelineView(); } }
    public bool ShowTools { get => _showTools; set { if (Set(ref _showTools, value)) RefreshTimelineView(); } }
    public bool ShowNotices { get => _showNotices; set { if (Set(ref _showNotices, value)) RefreshTimelineView(); } }
    public bool Follow { get => _follow; set { if (Set(ref _follow, value) && value) ScrollToEndRequested?.Invoke(this, EventArgs.Empty); } }

    public ThemePreference Theme
    {
        get => AppSettings.Current.Theme;
        set
        {
            if (AppSettings.Current.Theme == value) return;
            AppSettings.Current.Theme = value;
            AppSettings.Current.Save();
            ThemeManager.Apply(value);
            Raise(nameof(Theme));
            Raise(nameof(ThemeLabel));
        }
    }

    public string ThemeLabel => Theme switch
    {
        ThemePreference.Light => "Light",
        ThemePreference.Dark => "Dark",
        _ => "Auto",
    };

    public string VisibleCountText
    {
        get
        {
            var shown = TimelineView.Cast<object>().Count();
            return shown == Timeline.Count ? $"{shown} items" : $"{shown} of {Timeline.Count} items";
        }
    }

    public void Start()
    {
        _ = PollAsync(force: true);
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    // ---------- discovery + polling ----------

    private static List<FileFact> Scan(IEnumerable<string> extraRoots)
    {
        var facts = new List<FileFact>();
        foreach (var root in Discovery.FindRoots(extraRoots))
        {
            IEnumerable<string> projectDirs;
            try { projectDirs = Directory.EnumerateDirectories(root); }
            catch { continue; }

            foreach (var dir in projectDirs)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl"))
                    {
                        var info = new FileInfo(file);
                        if (info.Length == 0) continue;
                        facts.Add(new FileFact(file, dir, root, info.Length, info.LastWriteTimeUtc));
                    }
                }
                catch { /* a distro may be shutting down mid-scan */ }
            }
        }
        return facts;
    }

    private async Task PollAsync(bool force = false)
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var roots = ExtraRoots.ToList();
            var facts = await Task.Run(() => Scan(roots));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new List<(SessionVM Session, FileFact Fact)>();
            var addedProject = false;
            var rosterChanged = false;

            foreach (var fact in facts)
            {
                seen.Add(fact.Path);

                if (!_projects.TryGetValue(fact.ProjectDir, out var project))
                {
                    project = new ProjectVM(fact.ProjectDir, fact.Root);
                    _projects[fact.ProjectDir] = project;
                    Projects.Add(project);
                    addedProject = true;
                }

                if (!_sessions.TryGetValue(fact.Path, out var session))
                {
                    session = new SessionVM(fact, project);
                    _sessions[fact.Path] = session;
                    project.Sessions.Add(session);
                    pending.Add((session, fact));
                    rosterChanged = true;
                }
                else if (force || fact.Length != session.Length || fact.LastWriteUtc != session.LastWriteUtc)
                {
                    pending.Add((session, fact));
                }
            }

            foreach (var gone in _sessions.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                var session = _sessions[gone];
                session.Project.Sessions.Remove(session);
                _sessions.Remove(gone);
                _search.Forget(gone);
                rosterChanged = true;
                if (ReferenceEquals(Selected, session)) Selected = null;
            }

            if (pending.Count > 0)
                await Task.Run(() =>
                {
                    foreach (var (session, fact) in pending) session.Pump(fact);
                });

            foreach (var (session, _) in pending)
            {
                session.NotifyChanged();
                session.Project.AdoptRealPath();
            }

            foreach (var project in Projects)
            {
                project.NotifyChanged();
                Sorting.Sort(project.Sessions, SessionOrder);
            }
            if (addedProject || pending.Count > 0) Sorting.Sort(Projects, ProjectOrder);

            ApplySessionFilter();
            UpdateStatus(facts.Count);

            // Sessions appearing or disappearing changes what a live query should match. Growth
            // alone deliberately does not re-run it, so results stay still while you click them.
            if (rosterChanged && SessionFilter.Trim().Length >= SearchEngine.MinQueryLength)
                QueueContentSearch();

            if (!_autoSelected && Projects.Count > 0)
            {
                _autoSelected = true;
                Selected = StartupSessionQuery is { Length: > 0 } q
                    ? _sessions.Values.FirstOrDefault(s => s.Haystack.Contains(q.ToLowerInvariant()))
                      ?? _sessions.Values.OrderByDescending(s => s.LastWriteUtc).First()
                    : _sessions.Values.OrderByDescending(s => s.LastWriteUtc).FirstOrDefault();
            }

            await AppendLiveTranscriptAsync();
        }
        catch (Exception ex)
        {
            Status = "Scan failed: " + ex.Message;
        }
        finally
        {
            _polling = false;
        }
    }

    private static readonly Comparison<SessionVM> SessionOrder =
        (a, b) => b.LastWriteUtc.CompareTo(a.LastWriteUtc);

    private static readonly Comparison<ProjectVM> ProjectOrder =
        (a, b) => b.Newest.CompareTo(a.Newest);

    private void UpdateStatus(int fileCount)
    {
        var live = _sessions.Values.Count(s => s.IsLive);
        var roots = Projects.Select(p => p.RootLabel).Distinct().Count();
        Status = fileCount == 0
            ? "No Claude Code transcripts found. Checked ~/.claude/projects on Windows and in every WSL distro."
            : $"{fileCount} sessions across {Projects.Count} projects, {roots} location{(roots == 1 ? "" : "s")}"
              + (live > 0 ? $" · {live} active now" : "");
    }

    // ---------- content search ----------

    private const int MaxHits = 300;

    private void QueueContentSearch()
    {
        _searchCancel?.Cancel();
        _searchDebounce.Stop();

        if (SessionFilter.Trim().Length < SearchEngine.MinQueryLength)
        {
            SearchResults.Clear();
            SearchSummary = "";
            IsSearching = false;
            if (_contentMatches.Count > 0)
            {
                _contentMatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ApplySessionFilter();
            }
            return;
        }

        IsSearching = true;
        _searchDebounce.Start();
    }

    private async Task RunContentSearchAsync()
    {
        var query = SessionFilter.Trim();
        if (query.Length < SearchEngine.MinQueryLength) { IsSearching = false; return; }

        _searchCancel?.Cancel();
        var cancel = _searchCancel = new CancellationTokenSource();
        var token = cancel.Token;

        // Newest first, so the most relevant sessions fill the result cap.
        var targets = _sessions.Values.OrderByDescending(s => s.LastWriteUtc).ToList();

        SearchOutcome outcome;
        try
        {
            outcome = await Task.Run(() => _search.Search(query, targets, MaxHits, token), token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            IsSearching = false;
            SearchSummary = "Search failed: " + ex.Message;
            return;
        }

        if (token.IsCancellationRequested || !string.Equals(query, SessionFilter.Trim(), StringComparison.Ordinal))
            return;

        SearchResults.Clear();
        foreach (var hit in outcome.Hits) SearchResults.Add(hit);

        _contentMatches = outcome.MatchedPaths;
        ApplySessionFilter();

        IsSearching = false;
        SearchSummary = outcome.TotalMatches == 0
            ? "No messages match"
            : outcome.Truncated
                ? $"Showing first {outcome.Hits.Count} of {outcome.TotalMatches:N0} matches in {outcome.SessionsMatched} sessions"
                : $"{outcome.TotalMatches:N0} match{(outcome.TotalMatches == 1 ? "" : "es")} in {outcome.SessionsMatched} session{(outcome.SessionsMatched == 1 ? "" : "s")}";
    }

    private async Task JumpAsync(SearchHit hit)
    {
        if (!ReferenceEquals(Selected, hit.Session)) Selected = hit.Session;
        await _load;

        // A hit is useless if its category is switched off or filtered away.
        switch (hit.Kind)
        {
            case ItemKind.Thinking: ShowThinking = true; break;
            case ItemKind.Tool: ShowTools = true; break;
            case ItemKind.Notice: ShowNotices = true; break;
        }
        if (!string.IsNullOrEmpty(TranscriptFilter)) TranscriptFilter = "";

        Follow = false;
        ScrollToItemRequested?.Invoke(this, hit.Ordinal);
    }

    // ---------- transcript ----------

    private async Task LoadTranscriptAsync()
    {
        _builder = new TimelineBuilder();
        _transcriptTail = null;

        var session = Selected;
        if (session is null)
        {
            SwapTimeline(new ObservableCollection<TimelineItem>());
            return;
        }

        var tail = new SessionTail(session.Path);
        var entries = await Task.Run(() => tail.ReadNew());

        // A different session may have been picked while we were reading.
        if (!ReferenceEquals(session, Selected)) return;

        _transcriptTail = tail;
        var items = new ObservableCollection<TimelineItem>();
        foreach (var entry in entries)
            foreach (var item in _builder.Consume(entry))
                items.Add(item);

        SwapTimeline(items);
        if (Follow) ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task AppendLiveTranscriptAsync()
    {
        if (_transcriptTail is null || Selected is null) return;

        var tail = _transcriptTail;
        var entries = await Task.Run(() => tail.ReadNew());
        if (entries.Count == 0 || !ReferenceEquals(tail, _transcriptTail)) return;

        var added = 0;
        foreach (var entry in entries)
            foreach (var item in _builder.Consume(entry))
            {
                Timeline.Add(item);
                added++;
            }

        if (added == 0) return;
        Raise(nameof(VisibleCountText));
        if (Follow) ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SwapTimeline(ObservableCollection<TimelineItem> items)
    {
        Timeline = items;
        TimelineView = BuildView(items);
        Raise(nameof(Timeline));
        Raise(nameof(TimelineView));
        Raise(nameof(VisibleCountText));
    }

    private ICollectionView BuildView(ObservableCollection<TimelineItem> items)
    {
        var view = new CollectionViewSource { Source = items }.View;
        view.Filter = o => o is TimelineItem item && PassesFilter(item);
        return view;
    }

    private bool PassesFilter(TimelineItem item)
    {
        switch (item.Kind)
        {
            case ItemKind.Thinking when !ShowThinking: return false;
            case ItemKind.Tool when !ShowTools: return false;
            // A date divider only means something in an unfiltered, chronological view.
            case ItemKind.Day: return string.IsNullOrWhiteSpace(TranscriptFilter);
            // API errors stay visible regardless — they are part of what happened, not noise.
            case ItemKind.Notice when !ShowNotices && item is not NoticeItem { IsError: true }: return false;
        }

        var needle = TranscriptFilter;
        return string.IsNullOrWhiteSpace(needle)
            || item.Search.Contains(needle.Trim().ToLowerInvariant(), StringComparison.Ordinal);
    }

    private void RefreshTimelineView()
    {
        TimelineView.Refresh();
        Raise(nameof(VisibleCountText));
    }

    private void SetToolExpansion(bool expanded)
    {
        foreach (var item in Timeline)
        {
            if (item is ToolItem t) t.IsExpanded = expanded;
            else if (item is ThinkingItem k) k.IsExpanded = expanded;
        }
    }

    private void ApplySessionFilter()
    {
        var needle = SessionFilter.Trim().ToLowerInvariant();

        foreach (var project in Projects)
        {
            var any = false;
            foreach (var session in project.Sessions)
            {
                // Metadata match or a message inside it matched — otherwise searching for a
                // phrase you only ever said mid-conversation empties the whole session list.
                var hit = needle.Length == 0
                          || session.Haystack.Contains(needle, StringComparison.Ordinal)
                          || _contentMatches.Contains(session.Path);
                session.IsVisible = hit;
                any |= hit;
            }
            project.IsVisible = any;
            if (needle.Length > 0 && any) project.IsExpanded = true;
            project.NotifyChanged();
        }
    }

    // ---------- commands ----------

    private void Reveal(object? _)
    {
        if (Selected is not { } s) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{s.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = "Could not open Explorer: " + ex.Message;
        }
    }

    private void Export(object? _)
    {
        if (Selected is not { } session) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Markdown|*.md|All files|*.*",
            FileName = Sanitize(session.Title) + ".md",
        };
        if (dialog.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine($"# {session.Title}").AppendLine();
        sb.AppendLine($"- Session: `{session.SessionId}`");
        sb.AppendLine($"- Directory: `{session.CwdText}`");
        if (session.Digest.Model is { } m) sb.AppendLine($"- Model: `{m}`");
        sb.AppendLine($"- Started: {session.Digest.FirstSeen?.ToLocalTime():f}").AppendLine();

        foreach (var item in Timeline)
        {
            switch (item)
            {
                case UserItem u:
                    sb.AppendLine("## User").AppendLine().AppendLine(u.Text).AppendLine();
                    break;
                case AssistantItem a:
                    sb.AppendLine("## Claude").AppendLine().AppendLine(a.Text).AppendLine();
                    break;
                case ThinkingItem t when ShowThinking:
                    sb.AppendLine("<details><summary>Thinking</summary>").AppendLine()
                      .AppendLine(t.Text).AppendLine().AppendLine("</details>").AppendLine();
                    break;
                case ToolItem tool when ShowTools:
                    sb.AppendLine($"### {tool.Name} — {tool.Preview}").AppendLine();
                    sb.AppendLine("```json").AppendLine(tool.InputJson).AppendLine("```").AppendLine();
                    if (!string.IsNullOrWhiteSpace(tool.Result))
                        sb.AppendLine("```").AppendLine(tool.Result).AppendLine("```").AppendLine();
                    break;
            }
        }

        try
        {
            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(false));
            Status = "Exported to " + dialog.FileName;
        }
        catch (Exception ex)
        {
            Status = "Export failed: " + ex.Message;
        }
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
        return name.Length > 60 ? name[..60].TrimEnd() : name;
    }
}

/// <summary>Flat, bindable snapshot of the selected session for the details pane.</summary>
public sealed class SessionDetail
{
    public SessionDetail(SessionVM s)
    {
        var d = s.Digest;
        Title = s.Title;
        SessionId = s.SessionId;
        Path = s.Path;
        Location = s.RootLabel;
        Cwd = s.CwdText;
        Branch = string.IsNullOrEmpty(d.GitBranch) ? "—" : d.GitBranch!;
        Model = string.IsNullOrEmpty(d.Model) ? "—" : d.Model!;
        Effort = string.IsNullOrEmpty(d.Effort) ? "—" : d.Effort!;
        Version = string.IsNullOrEmpty(d.Version) ? "—" : "v" + d.Version;
        Started = d.FirstSeen?.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm") ?? "—";
        Ended = d.LastSeen?.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm") ?? "—";
        Elapsed = Format.Duration(d.FirstSeen, d.LastSeen);
        Turns = $"{d.UserTurns} user · {d.AssistantTurns} assistant";
        ToolCalls = d.ToolCalls.ToString("N0");
        Records = d.Records.ToString("N0");
        Size = Format.Bytes(s.Length);
        Errors = d.Errors == 0 ? "none" : d.Errors.ToString();
        Tools = d.Tools.Count == 0 ? "—" : string.Join(", ", d.Tools.OrderBy(t => t));

        InputTokens = Format.Tokens(d.Usage.Input);
        OutputTokens = Format.Tokens(d.Usage.Output);
        CacheWrite = Format.Tokens(d.Usage.CacheCreate);
        CacheRead = Format.Tokens(d.Usage.CacheRead);
        TotalTokens = Format.Tokens(d.Usage.Total);
        Cost = d.Usage.EstimateCostUsd(d.Model) is { } cost ? Format.AudCost(cost) : "—";
    }

    public string Title { get; }
    public string SessionId { get; }
    public string Path { get; }
    public string Location { get; }
    public string Cwd { get; }
    public string Branch { get; }
    public string Model { get; }
    public string Effort { get; }
    public string Version { get; }
    public string Started { get; }
    public string Ended { get; }
    public string Elapsed { get; }
    public string Turns { get; }
    public string ToolCalls { get; }
    public string Records { get; }
    public string Size { get; }
    public string Errors { get; }
    public string Tools { get; }
    public string InputTokens { get; }
    public string OutputTokens { get; }
    public string CacheWrite { get; }
    public string CacheRead { get; }
    public string TotalTokens { get; }
    public string Cost { get; }
}

/// <summary>In-place sort that reorders by Move, so bound selection survives a re-sort.</summary>
public static class Sorting
{
    public static void Sort<T>(ObservableCollection<T> list, Comparison<T> compare)
    {
        for (var i = 1; i < list.Count; i++)
        {
            var item = list[i];
            var j = i - 1;
            while (j >= 0 && compare(list[j], item) > 0) j--;
            if (j + 1 != i) list.Move(i, j + 1);
        }
    }
}
