using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClaudeSessions;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private bool _suppressFollowChange;
    private int? _openHit;

    public MainWindow()
    {
        var argv = Environment.GetCommandLineArgs();
        var sessionArg = Array.IndexOf(argv, "--session");
        if (sessionArg >= 0 && sessionArg + 1 < argv.Length)
            _vm.StartupSessionQuery = argv[sessionArg + 1];

        for (var a = 0; a < argv.Length - 1; a++)
            if (argv[a] == "--root")
                _vm.ExtraRoots.Add(argv[a + 1]);

        var findArg = Array.IndexOf(argv, "--find");
        if (findArg >= 0 && findArg + 1 < argv.Length)
            _vm.TranscriptFilter = argv[findArg + 1];

        var searchArg = Array.IndexOf(argv, "--search");
        if (searchArg >= 0 && searchArg + 1 < argv.Length)
            _vm.SessionFilter = argv[searchArg + 1];

        // "--open <n>" jumps straight to the nth result once the search has run.
        var openArg = Array.IndexOf(argv, "--open");
        if (openArg >= 0 && openArg + 1 < argv.Length && int.TryParse(argv[openArg + 1], out var nth))
            _openHit = nth;

        InitializeComponent();
        DataContext = _vm;
        _vm.ScrollToEndRequested += (_, _) => ScrollToEnd();
        _vm.ScrollToItemRequested += (_, ordinal) => ScrollToOrdinal(ordinal);
        Loaded += (_, _) => _vm.Start();

        if (_openHit is { } index)
            _vm.SearchResults.CollectionChanged += (_, _) =>
            {
                if (_openHit is null || index >= _vm.SearchResults.Count) return;
                _openHit = null;
                _vm.JumpCommand.Execute(_vm.SearchResults[index]);
            };
        Closed += (_, _) => _vm.Stop();
        MaybeSelfCapture();
    }

    /// <summary>Dev aid: "--shot &lt;path&gt; [seconds]" renders the window to a PNG and exits.</summary>
    private void MaybeSelfCapture()
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, "--shot");
        if (i < 0 || i + 1 >= args.Length) return;

        var path = args[i + 1];
        var delay = i + 2 < args.Length && double.TryParse(args[i + 2], out var d) ? d : 6;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                if (Array.IndexOf(args, "--expand") >= 0)
                {
                    _vm.Follow = false;
                    _vm.ExpandAllToolsCommand.Execute(null);
                    var target = _vm.Timeline.FirstOrDefault(t => t is ToolItem { HasImages: true })
                                 ?? _vm.Timeline.FirstOrDefault();
                    if (target is not null) TranscriptList.ScrollIntoView(target);
                    UpdateLayout();
                }

                var dpi = VisualTreeHelper.GetDpi(this);
                var bmp = new RenderTargetBitmap(
                    (int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bmp.Render(this);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using var fs = System.IO.File.Create(path);
                enc.Save(fs);
                App.Log($"self-capture written to {path}");
            }
            catch (Exception ex) { App.Log("self-capture failed: " + ex); }
            Application.Current.Shutdown();
        };
        timer.Start();
    }

    private void OnSessionSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is SessionVM session) _vm.Selected = session;
    }

    private void ScrollToEnd()
    {
        // Wait for the new items to be measured before jumping to the bottom.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            var count = TranscriptList.Items.Count;
            if (count == 0) return;
            _suppressFollowChange = true;
            try { TranscriptList.ScrollIntoView(TranscriptList.Items[count - 1]); }
            catch { }
            finally { _suppressFollowChange = false; }
        });
    }

    private void OnResultActivated(object sender, MouseButtonEventArgs e)
    {
        if (((ListBox)sender).SelectedItem is SearchHit hit) _vm.JumpCommand.Execute(hit);
    }

    private void OnResultKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (((ListBox)sender).SelectedItem is SearchHit hit) _vm.JumpCommand.Execute(hit);
        e.Handled = true;
    }

    /// <summary>Brings a search hit into view and parks it near the top, not scrolled off-screen.</summary>
    private void ScrollToOrdinal(int ordinal)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (ordinal < 0 || ordinal >= _vm.Timeline.Count) return;
            var item = _vm.Timeline[ordinal];

            _suppressFollowChange = true;
            try
            {
                TranscriptList.ScrollIntoView(item);
                TranscriptList.UpdateLayout();

                // ScrollIntoView stops as soon as the item is merely visible; nudge it up so
                // the surrounding conversation is readable.
                if (FindScroller() is { } scroller
                    && TranscriptList.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement container)
                {
                    var offset = container.TransformToAncestor(scroller).Transform(default).Y;
                    scroller.ScrollToVerticalOffset(scroller.VerticalOffset + offset - 90);
                }
            }
            catch (InvalidOperationException) { /* container recycled mid-scroll */ }
            finally { _suppressFollowChange = false; }
        });
    }

    private ScrollViewer? FindScroller()
    {
        DependencyObject node = TranscriptList;
        while (node is not null)
        {
            if (VisualTreeHelper.GetChildrenCount(node) == 0) return null;
            node = VisualTreeHelper.GetChild(node, 0);
            if (node is ScrollViewer sv) return sv;
        }
        return null;
    }

    /// <summary>
    /// A tool result with its own scroller otherwise swallows the wheel and the transcript
    /// stops moving. Hand the gesture back to the list once the inner view hits its limit.
    /// </summary>
    private void OnInnerScroll(object sender, MouseWheelEventArgs e)
    {
        var inner = (ScrollViewer)sender;
        var stuckAtTop = e.Delta > 0 && inner.VerticalOffset <= 0;
        var stuckAtBottom = e.Delta < 0 && inner.VerticalOffset >= inner.ScrollableHeight;

        if (inner.ScrollableHeight > 0 && !stuckAtTop && !stuckAtBottom) return;

        e.Handled = true;
        TranscriptList.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = inner,
        });
    }

    /// <summary>Scrolling up releases Follow; scrolling back to the bottom re-arms it.</summary>
    private void OnTranscriptScroll(object sender, ScrollChangedEventArgs e)
    {
        if (_suppressFollowChange) return;
        if (e.ExtentHeightChange != 0) return;      // content grew — not a user gesture
        if (e.VerticalChange == 0) return;

        var atBottom = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 24;
        _vm.Follow = atBottom;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            TranscriptSearch.Focus();
            TranscriptSearch.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SessionSearch.Focus();
            SessionSearch.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (TranscriptSearch.IsKeyboardFocusWithin) _vm.TranscriptFilter = "";
            else if (SessionSearch.IsKeyboardFocusWithin) _vm.SessionFilter = "";
        }

        base.OnPreviewKeyDown(e);
    }
}
