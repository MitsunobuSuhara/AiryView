using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Text;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using System.Security.Cryptography;
using SkiaSharp;
using Svg.Skia;

namespace AiryView;

public partial class MainWindow : Window
{
    private sealed class TabState(PdfDocument document)
    {
        public PdfDocument Document = document;
        public int Page;
        public double Zoom = 1;

        public double ScrollOffset;
    }
    private sealed class TextTabState(string path, System.Windows.Documents.FlowDocument document, string text, Encoding encoding, bool editable)
    {
        public string Path = path;
        public System.Windows.Documents.FlowDocument Document = document;
        public string Text = text;
        public string LiveText = text;
        public Encoding Encoding = encoding;
        public bool Editable = editable;
        public bool IsMarkdown => new[] { ".md", ".markdown" }.Contains(System.IO.Path.GetExtension(Path), StringComparer.OrdinalIgnoreCase);
        public bool SourceMode;
        public bool Wrap;
        public bool CanEdit => Editable || IsMarkdown;
        public bool ShowEditor => Editable || IsMarkdown && SourceMode;
        public bool Dirty;
        public double Zoom = 1;
        public byte[]? FileHash;
    }
    private TabState? Current => (Tabs.SelectedItem as TabItem)?.Tag as TabState;
    private TextTabState? CurrentText => (Tabs.SelectedItem as TabItem)?.Tag as TextTabState;
    private sealed class ImageTabState(string path, BitmapSource image)
    {
        public string Path = path; public BitmapSource Image = image;
        public double Zoom = 1;
        public int Rotation;
        public bool InitialFitComplete;
        public byte[]? SvgBytes;
        public readonly int DisplayWidth = image.PixelWidth, DisplayHeight = image.PixelHeight;
    }
    private ImageTabState? CurrentImage => (Tabs.SelectedItem as TabItem)?.Tag as ImageTabState;
    private readonly DispatcherTimer svgTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool svgRendering;
    private int svgRequestVersion;
    private readonly DispatcherTimer zoomTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(140) };
    private sealed class PageView
    {
        public Grid Surface = new() { Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 24), UseLayoutRounding = true, SnapsToDevicePixels = true };
        public Image Image = new() { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        public Canvas Selection = new() { Background = Brushes.Transparent, Cursor = Cursors.IBeam };
        public IReadOnlyList<PdfTextCharacter>? Characters;
        public int Page;
        public int RenderWidth;
        public int RenderHeight;
        public Task<BitmapSource>? PendingRender;
        public int PendingWidth, PendingHeight;
    }
    private readonly List<PageView> pageViews = [];
    private Grid PageSurface => pageViews[Current!.Page].Surface;
    private object? displayedState;
    private bool displayedEditor;
    private bool windowClosed;
    private sealed class ReadingPosition
    {
        public double X, Y;
        public int SelectionStart, SelectionLength;
    }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, ReadingPosition> readingPositions = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, ReadingPosition> previewPositions = new();

    private void SaveReadingPosition()
    {
        if (displayedState is ImageTabState)
        {
            var position = readingPositions.GetOrCreateValue(displayedState);
            position.X = ImageViewer.HorizontalOffset; position.Y = ImageViewer.VerticalOffset;
        }
        else if (displayedState is TextTabState)
        {
            var position = (displayedEditor ? readingPositions : previewPositions).GetOrCreateValue(displayedState);
            if (displayedEditor)
            {
                position.X = TextEditor.HorizontalOffset; position.Y = TextEditor.VerticalOffset;
                position.SelectionStart = TextEditor.SelectionStart; position.SelectionLength = TextEditor.SelectionLength;
            }
            else if (FindVisualChild<ScrollViewer>(MarkdownViewer) is { } viewer)
            {
                position.X = viewer.HorizontalOffset; position.Y = viewer.VerticalOffset;
            }
        }
    }
    private void RestoreReadingPosition(object? state, bool editor)
    {
        UpdateLayout();
        if (state is ImageTabState image && image.InitialFitComplete)
        {
            var position = readingPositions.GetOrCreateValue(state);
            ImageViewer.ScrollToHorizontalOffset(position.X); ImageViewer.ScrollToVerticalOffset(position.Y);
        }
        else if (state is TextTabState)
        {
            var position = (editor ? readingPositions : previewPositions).GetOrCreateValue(state);
            if (editor)
            {
                TextEditor.Select(Math.Min(position.SelectionStart, TextEditor.Text.Length),
                    Math.Min(position.SelectionLength, Math.Max(0, TextEditor.Text.Length - position.SelectionStart)));
                TextEditor.ScrollToHorizontalOffset(position.X); TextEditor.ScrollToVerticalOffset(position.Y);
            }
            else if (FindVisualChild<ScrollViewer>(MarkdownViewer) is { } viewer)
            {
                viewer.ScrollToHorizontalOffset(position.X); viewer.ScrollToVerticalOffset(position.Y);
            }
        }
        UpdateLayout();
    }
    private bool changingLayout;
    private int renderVersion;
    private bool opening;
    private long zoomPauseUntil;
    private readonly List<System.Windows.Documents.Run> textMatches = [];
    private readonly List<int> textEditorMatches = [];
    private int textEditorQueryLength;
    private int textMatchIndex = -1;
    private PageView? selectingPage;
    private int selectionAnchor = -1;
    private string selectedPdfText = "";
    private readonly List<string> closedPaths = [];
    internal static bool SuppressRecentFilesForTest;
    internal static string? LastRecentFileForTest;
    internal RecentFileHistory RecentHistory { get; set; } = RecentFileHistory.Shared;
    private const long MaxTextBytes = 64L * 1024 * 1024;
    private const long MaxImageBytes = 256L * 1024 * 1024;
    private const long MaxImagePixels = 200_000_000;
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".gif", ".ico", ".webp", ".svg"];

    public MainWindow() : this(showWelcome: true) { }
    internal MainWindow(bool showWelcome)
    {
        InitializeComponent();
        suppressWelcomeUntilOpen = !showWelcome;
        UpdateWelcome();
        Closed += (_, _) => { windowClosed = true; svgTimer.Stop(); zoomTimer.Stop(); ++svgRequestVersion; ++renderVersion; };
        Loaded += (_, _) => { CompleteInitialImageFit(); QueueInitialDisplay(); };
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) QueueInitialDisplay(); };
        ImageViewer.SizeChanged += (_, _) => { if (CurrentImage is { InitialFitComplete: false }) QueueInitialDisplay(); };
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        WindowPreferences.Restore(this);
        svgTimer.Tick += async (_, _) => await RefreshSvgAsync();
        zoomTimer.Tick += async (_, _) => { zoomTimer.Stop(); await RenderVisible(); };
        DpiChanged += (_, _) => { zoomTimer.Stop(); zoomTimer.Start(); if (CurrentImage is { SvgBytes: not null } image) ApplyImageLayout(image); };
    }
    private bool suppressWelcomeUntilOpen;
    private int fileOpenRequests;
    internal void PrepareFileOpen()
    {
        // Showより前に確定させ、最初のフレームに案内を混ぜない。
        suppressWelcomeUntilOpen = true;
        UpdateWelcome();
    }
    private void UpdateWelcome()
    {
        bool show = !suppressWelcomeUntilOpen && fileOpenRequests == 0 && Tabs.Items.Count == 0;
        Welcome.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && WelcomeCrane.Source == null)
        {
            // ファイル起動では使わない案内用の画像も読み込まない。
            var icon = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/icon.ico"), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            WelcomeCrane.Source = icon.Frames.OrderByDescending(frame => frame.PixelWidth).First();
        }
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private void ApplyDarkTitleBar()
    {
        int enabled = 1;
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
    }
    private void ShowDialogOwner()
    {
        if (!windowClosed && !IsVisible) Show();
    }
    private void Error(Exception ex)
    {
        ShowDialogOwner();
        MessageBox.Show(this, ex.Message, "AiryView", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    public async void NewText() => await NewTextAsync();
    private void NewTextClick(object s, RoutedEventArgs e) => NewText();
    private TabItem CreateTab(string title, string toolTip, object state)
    {
        var tab = new TabItem { ToolTip = toolTip, Tag = state, Padding = new Thickness(12, 3, 7, 3), MinHeight = 34 };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, FontSize = 15 });
        var close = new Button { Content = "×", Tag = tab, ToolTip = "タブを閉じる / Close tab  Ctrl+W", FontSize = 17, FontWeight = FontWeights.SemiBold, Width = 28, Height = 24, Padding = new Thickness(0, -2, 0, 1), Margin = new Thickness(8, 0, -2, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        close.Click += CloseTabClick; header.Children.Add(close); tab.Header = header;
        return tab;
    }
    private static void SetTabTitle(TabItem tab, string title)
    {
        if (tab.Header is StackPanel header && header.Children.OfType<TextBlock>().FirstOrDefault() is { } label) label.Text = title;
    }
    private static string? StatePath(object? state) => state switch
    {
        TabState pdf => pdf.Document.Path,
        TextTabState text when !string.IsNullOrEmpty(text.Path) => text.Path,
        ImageTabState image => image.Path,
        _ => null
    };
    private TabItem? FindOpenTab(string path) => Tabs.Items.Cast<TabItem>().FirstOrDefault(tab =>
        StatePath(tab.Tag) is { } openPath && string.Equals(System.IO.Path.GetFullPath(openPath), path, StringComparison.OrdinalIgnoreCase));
    internal string? CurrentPathForTest => StatePath((Tabs.SelectedItem as TabItem)?.Tag);
    internal int TabCountForTest => Tabs.Items.Count;
    internal int PdfRenderCountForTest { get; private set; }
    internal Task RenderVisibleForTest() => RenderVisible();
    internal async Task NewTextAsync()
    {
        var state = new TextTabState("", LightweightTextRenderer.BuildPlain(""), "", new UTF8Encoding(false), true);
        var tab = CreateTab("無題.txt", "新しいテキスト", state);
        opening = true; Tabs.Items.Add(tab); Tabs.SelectedItem = tab; opening = false;
        await RenderCurrent(); TextEditor.Focus();
    }
    private void AddRecentFile(string path)
    {
        LastRecentFileForTest = path;
        if (SuppressRecentFilesForTest && ReferenceEquals(RecentHistory, RecentFileHistory.Shared)) return;
        try
        {
            RecentHistory.Enqueue(path);
        }
        catch { }
    }
    public async void OpenPaths(IEnumerable<string> paths) => await OpenPathsAsync(paths);
    internal async Task ShowFilesAsync(IEnumerable<string> paths)
    {
        // HWNDだけを作り、実際のモニターのDPIで最初の内容を準備する。
        // 読込中の空の枠を見せず、固定時間の待機やアニメーションは追加しない。
        new WindowInteropHelper(this).EnsureHandle();
        string[] requested = paths.ToArray();
        await OpenPathsAsync(requested.Take(1));
        if (windowClosed) return;
        if (!IsVisible && Current is { } state && pageViews.Count > 0)
        {
            int version = ++renderVersion;
            var view = pageViews[state.Page];
            double factor = Math.Min(VisualTreeHelper.GetDpi(this).DpiScaleX,
                Math.Sqrt(12_000_000.0 / (view.Surface.Width * view.Surface.Height)));
            int w = Math.Max(1, (int)Math.Round(view.Surface.Width * factor));
            int h = Math.Max(1, (int)Math.Round(view.Surface.Height * factor));
            try
            {
                ++PdfRenderCountForTest;
                var bitmap = await Task.Run(() => state.Document.Render(view.Page, w, h, lcdText: true));
                if (!windowClosed && version == renderVersion && Current == state)
                {
                    view.Image.Source = bitmap; view.RenderWidth = w; view.RenderHeight = h;
                }
            }
            catch (ObjectDisposedException) when (windowClosed) { }
            catch (Exception ex) { if (!windowClosed) Error(ex); }
        }
        if (!windowClosed && !IsVisible) Show();
        if (!windowClosed && requested.Length > 1) await OpenPathsAsync(requested.Skip(1));
    }
    public async Task OpenPathsAsync(IEnumerable<string> paths)
    {
        ++fileOpenRequests;
        suppressWelcomeUntilOpen = false;
        UpdateWelcome();
        try { await OpenPathsCoreAsync(paths); }
        finally
        {
            --fileOpenRequests;
            if (!windowClosed) UpdateWelcome();
        }
    }
    private async Task OpenPathsCoreAsync(IEnumerable<string> paths)
    {
        foreach (string requestedPath in paths)
        {
            try
            {
                string path = System.IO.Path.GetFullPath(requestedPath);
                if (FindOpenTab(path) is { } existing)
                {
                    await SelectOpenTabAsync(existing);
                    AddRecentFile(path);
                    continue;
                }
                string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (new[] { ".md", ".markdown", ".txt" }.Contains(extension))
                {
                    var loaded = await ReadTextAsync(path);
                    string text = loaded.Text;
                    // TXTはTextBoxで表示するため、印刷用の全行FlowDocumentを先に作らない。
                    var document = extension == ".txt" ? LightweightTextRenderer.BuildPlain("") : LightweightTextRenderer.Build(text);
                    var reader = new TextTabState(path, document, text, loaded.Encoding, extension == ".txt") { FileHash = loaded.Hash };
                    var readerTab = CreateTab(System.IO.Path.GetFileName(path), path, reader);
                    opening = true; Tabs.Items.Add(readerTab); Tabs.SelectedItem = readerTab; opening = false;
                    await RenderCurrent(); AddRecentFile(path); continue;
                }
                if (ImageExtensions.Contains(extension))
                {
                    if (new FileInfo(path).Length > MaxImageBytes) throw new IOException("画像が大きすぎます（上限256MB）。");
                    byte[] bytes = await File.ReadAllBytesAsync(path);
                    BitmapSource bitmap = await DecodeImageAsync(bytes, extension);
                    if ((long)bitmap.PixelWidth * bitmap.PixelHeight > MaxImagePixels) throw new IOException("画像の画素数が大きすぎます（上限2億画素）。");
                    if (windowClosed) return;
                    if (FindOpenTab(path) is { } loadedTab)
                    {
                        await SelectOpenTabAsync(loadedTab);
                        continue;
                    }
                    var image = new ImageTabState(path, bitmap) { SvgBytes = extension == ".svg" ? bytes : null };
                    var imageTab = CreateTab(System.IO.Path.GetFileName(path), path, image);
                    opening = true; Tabs.Items.Add(imageTab); Tabs.SelectedItem = imageTab; opening = false;
                    await RenderCurrent(); AddRecentFile(path); continue;
                }
                Status.Text = "PDFを読み込んでいます…";
                PdfDocument doc;
                string? password = null;
                while (true)
                {
                    try { doc = await Task.Run(() => new PdfDocument(path, password)); break; }
                    catch (PdfPasswordException)
                    {
                        ShowDialogOwner();
                        password = TextPrompt.Ask(this, "PDFのパスワード", password == null ? "開くためのパスワードを入力してください。" : "パスワードが正しくありません。再入力してください。", true);
                        if (password == null) return;
                    }
                }
                var state = new TabState(doc);
                var tab = CreateTab(System.IO.Path.GetFileName(path), path, state);
                opening = true; Tabs.Items.Add(tab); Tabs.SelectedItem = tab; opening = false;
                await RenderCurrent();
                AddRecentFile(path);
            }
            catch (Exception ex) { Error(ex); Status.Text = "ファイルを開けませんでした。"; }
        }
    }
    private async Task SelectOpenTabAsync(TabItem tab)
    {
        if (ReferenceEquals(Tabs.SelectedItem, tab))
        {
            // 表示中の内容・選択・編集位置を保ち、未完了の描画だけ補う。
            CompleteInitialImageFit();
            if (Current != null) await RenderVisible();
            return;
        }
        opening = true;
        try { Tabs.SelectedItem = tab; }
        finally { opening = false; }
        // SelectionChangedと呼出元の両方からページを作り直さない。
        await RenderCurrent();
    }
    internal static Task<BitmapSource> DecodeImageAsync(byte[] bytes, string extension) =>
        Task.Run(() => extension == ".svg" ? LoadSvgBitmap(bytes) : extension == ".webp" ? LoadSkiaBitmap(bytes) : LoadWpfBitmap(bytes));

    private static BitmapSource LoadWpfBitmap(byte[] bytes)
    {
        using var probe = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(probe, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > MaxImagePixels) throw new IOException("画像の画素数が大きすぎます（上限2億画素）。");
        // ICC付き画像はWPF標準の色補正を維持する。
        if (frame.ColorContexts is { Count: > 0 })
        {
            var corrected = new BitmapImage(); corrected.BeginInit(); corrected.CacheOption = BitmapCacheOption.OnLoad;
            using (var stream = new MemoryStream(bytes)) { corrected.StreamSource = stream; corrected.EndInit(); }
            corrected.Freeze(); return corrected;
        }
        // 寸法確認に使ったデコーダーを再利用し、同じ画像を開き直さない。
        var bitmap = new CachedBitmap(frame, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        bitmap.Freeze(); return bitmap;
    }
    private bool initialDisplayQueued;
    private void QueueInitialDisplay()
    {
        if (windowClosed || initialDisplayQueued) return;
        initialDisplayQueued = true;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            initialDisplayQueued = false;
            if (windowClosed || !IsLoaded || WindowState == WindowState.Minimized) return;
            CompleteInitialImageFit();
            if (Current != null) await RenderVisible();
        }, DispatcherPriority.Loaded);
    }
    private void CompleteInitialImageFit()
    {
        if (CurrentImage is not { InitialFitComplete: false } image || !IsLoaded || WindowState == WindowState.Minimized) return;
        image.InitialFitComplete = FitImageForInitialDisplay(image);
        if (!image.InitialFitComplete) return;
        if (!ZoomText.IsKeyboardFocusWithin) ZoomText.Text = (image.Zoom * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        UpdateNonPdfStatus();
    }
    private static BitmapSource LoadSkiaBitmap(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new IOException("画像形式を読み込めませんでした。");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxImagePixels) throw new IOException("画像の画素数が大きすぎます（上限2億画素）。");
        using var bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success) throw new IOException("画像の読み込みに失敗しました。");
        return CopySkiaBitmap(bitmap);
    }
    private static BitmapSource CopySkiaBitmap(SKBitmap bitmap)
    {
        // PNGへの圧縮・再展開を挟まず、同じ画素をWPFへコピーする。
        var result = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Pbgra32, null,
            bitmap.GetPixels(), checked(bitmap.RowBytes * bitmap.Height), bitmap.RowBytes);
        result.Freeze(); return result;
    }
    internal static (int Width, int Height) SvgRasterSize(double width, double height, double requestedScale = 4)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(requestedScale) || requestedScale <= 0 || width <= 0 || height <= 0)
            throw new IOException("SVGのサイズが正しくありません。");
        // 丸め後も上限を超えず、極端に細長い図形でも巨大なバッファを作らない。
        const double maxPixels = 16_000_000;
        const double maxSide = 8192;
        double scale = Math.Min(requestedScale, Math.Min(maxSide / Math.Max(width, height), Math.Sqrt(maxPixels / width / height)));
        int w = Math.Max(1, (int)Math.Floor(width * scale));
        int h = Math.Max(1, (int)Math.Floor(height * scale));
        return (w, h);
    }
    private static BitmapSource LoadSvgBitmap(byte[] bytes, double? requestedWidth = null)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var svg = new SKSvg(); svg.Load(stream);
        var picture = svg.Picture ?? throw new IOException("SVGの図形を読み込めませんでした。");
        SKRect bounds = picture.CullRect;
        var (width, height) = SvgRasterSize(bounds.Width, bounds.Height, requestedWidth.HasValue ? requestedWidth.Value / bounds.Width : 4);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(width / bounds.Width, height / bounds.Height);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(picture);
        canvas.Flush();
        return CopySkiaBitmap(bitmap);
    }
    private static async Task<(string Text, Encoding Encoding, byte[] Hash)> ReadTextAsync(string path)
    {
        if (new FileInfo(path).Length > MaxTextBytes) throw new IOException("文章ファイルが大きすぎます（上限64MB）。");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        return await Task.Run(() =>
        {
            var decoded = DecodeText(bytes);
            return (decoded.Text, decoded.Encoding, SHA256.HashData(bytes));
        });
    }
    private static (string Text, Encoding Encoding) DecodeText(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return (new UTF8Encoding(true, true).GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(true));
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 })) return (new UTF32Encoding(false, true, true).GetString(bytes, 4, bytes.Length - 4), new UTF32Encoding(false, true));
        if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF })) return (new UTF32Encoding(true, true, true).GetString(bytes, 4, bytes.Length - 4), new UTF32Encoding(true, true));
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return (new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(false, true));
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return (new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(true, true));
        try { var encoding = new UTF8Encoding(false, true); return (encoding.GetString(bytes), new UTF8Encoding(false)); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var encoding = Encoding.GetEncoding(932); return (encoding.GetString(bytes), encoding);
        }
    }
    private void OpenClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "対応ファイル|*.pdf;*.md;*.markdown;*.txt;*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp;*.gif;*.ico;*.webp;*.svg|PDF|*.pdf|文章|*.md;*.markdown;*.txt|画像|*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp;*.gif;*.ico;*.webp;*.svg", Multiselect = true };
        if (dialog.ShowDialog(this) == true) OpenPaths(dialog.FileNames);
    }
    private void FilesDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) OpenPaths(files.Where(x => new[] { ".pdf", ".md", ".markdown", ".txt" }.Contains(System.IO.Path.GetExtension(x), StringComparer.OrdinalIgnoreCase) || ImageExtensions.Contains(System.IO.Path.GetExtension(x), StringComparer.OrdinalIgnoreCase)));
    }
    private async void TabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == Tabs && !opening) await RenderCurrent();
    }
    private async Task RenderCurrent()
    {
        SaveReadingPosition();
        ++svgRequestVersion; svgTimer.Stop();
        ++renderVersion;
        var state = Current;
        var textDocument = CurrentText;
        var image = CurrentImage;
        displayedState = (object?)image ?? textDocument;
        displayedEditor = textDocument?.ShowEditor == true;
        changingLayout = true;
        ClearPdfSelection();
        PagesHost.Children.Clear(); pageViews.Clear();
        ClearTextSearch();
        TextSearchBar.Visibility = Visibility.Collapsed;
        UpdateWelcome();
        Viewer.Visibility = state != null ? Visibility.Visible : Visibility.Collapsed;
        MarkdownViewer.Visibility = textDocument?.IsMarkdown == true && !textDocument.SourceMode ? Visibility.Visible : Visibility.Collapsed;
        TextEditorArea.Visibility = textDocument?.ShowEditor == true ? Visibility.Visible : Visibility.Collapsed;
        TextSelectionBadge.Visibility = Visibility.Collapsed;
        SaveTextButton.Visibility = textDocument?.CanEdit == true ? Visibility.Visible : Visibility.Collapsed;
        MarkdownModeButton.Visibility = textDocument?.IsMarkdown == true ? Visibility.Visible : Visibility.Collapsed;
        if (textDocument?.IsMarkdown == true) MarkdownModeButton.ToolTip = textDocument.SourceMode ? "整形表示へ / Switch to Preview\nCtrl+Shift+M" : "編集表示へ / Switch to Source\nCtrl+Shift+M";
        ImageViewer.Visibility = image != null ? Visibility.Visible : Visibility.Collapsed;
        DocumentToolbar.Visibility = Visibility.Visible;
        PageControls.Visibility = state != null ? Visibility.Visible : Visibility.Collapsed;
        RotationControls.Visibility = state != null || image != null ? Visibility.Visible : Visibility.Collapsed;
        FitWidthButton.Visibility = state != null || image != null ? Visibility.Visible : Visibility.Collapsed;
        PrintButton.Visibility = state != null || textDocument?.Editable == true ? Visibility.Visible : Visibility.Collapsed;
        ContentGrid.Background = textDocument != null || image != null ? Brushes.White : new SolidColorBrush(Color.FromRgb(188, 195, 204));
        MarkdownViewer.Document = textDocument?.IsMarkdown == true && !textDocument.SourceMode ? textDocument.Document : null;
        if (textDocument?.ShowEditor == true && TextEditor.Text != textDocument.LiveText) { opening = true; TextEditor.Text = textDocument.LiveText; opening = false; }
        TextEditor.TextWrapping = textDocument?.Wrap == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
        TextEditor.HorizontalScrollBarVisibility = textDocument?.Wrap == true ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        if (textDocument?.ShowEditor == true) UpdateTextSmartStatus();
        else if (textDocument?.IsMarkdown == true) UpdateMarkdownPreviewStatus();
        ReaderImage.Source = image?.Image;
        if (textDocument != null) { MarkdownViewer.Zoom = textDocument.Zoom * 100; TextEditor.FontSize = 15 * textDocument.Zoom; }
        if (image != null)
        {
            ApplyImageLayout(image);
            if (!image.InitialFitComplete)
            {
                // 表示可能なウィンドウでは最初のフレームまでに倍率を確定する。
                CompleteInitialImageFit();
                if (!image.InitialFitComplete) QueueInitialDisplay();
            }
        }
        if (!ZoomText.IsKeyboardFocusWithin && (state != null || textDocument != null || image != null)) ZoomText.Text = (ActiveZoom * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            if (textDocument != null) { RestoreReadingPosition(textDocument, displayedEditor); UpdateNonPdfStatus(); return; }
            if (image != null) { RestoreReadingPosition(image, false); UpdateNonPdfStatus(); return; }
            if (state == null) return;
            for (int i = 0; i < state.Document.Count; i++)
            {
                var view = new PageView { Page = i };
                RenderOptions.SetBitmapScalingMode(view.Image, BitmapScalingMode.NearestNeighbor);
                view.Surface.Tag = i; view.Selection.Tag = view;
                view.Selection.MouseLeftButtonDown += PdfSelectionStarted;
                view.Selection.MouseMove += PdfSelectionMoved;
                view.Selection.MouseLeftButtonUp += PdfSelectionEnded;
                view.Surface.Children.Add(view.Image); view.Surface.Children.Add(view.Selection);
                pageViews.Add(view); PagesHost.Children.Add(view.Surface);
            }
            SizePages(); Viewer.UpdateLayout();
            Viewer.ScrollToVerticalOffset(state.ScrollOffset);
            Viewer.UpdateLayout(); UpdatePageInfo();
        }
        finally { changingLayout = false; }
        await RenderVisible();
    }
    private void SizePages()
    {
        if (Current is not { } state) return;
        for (int i = 0; i < pageViews.Count; i++)
        {
            Size mm = state.Document.SizeMm(i);
            pageViews[i].Surface.Width = mm.Width * 96 / 25.4 * state.Zoom;
            pageViews[i].Surface.Height = mm.Height * 96 / 25.4 * state.Zoom;
        }
    }
    private static string PdfPageLabel(Size size)
    {
        double shortSide = Math.Min(size.Width, size.Height), longSide = Math.Max(size.Width, size.Height);
        (string Name, double Short, double Long)[] standards =
        [
            ("A3", 297, 420), ("A4", 210, 297), ("A5", 148, 210),
            ("B4", 257, 364), ("B5", 182, 257)
        ];
        string? name = standards.FirstOrDefault(p => Math.Abs(shortSide - p.Short) <= 1 && Math.Abs(longSide - p.Long) <= 1).Name;
        if (name == null) return $"{size.Width:0.#} × {size.Height:0.#} mm";
        return size.Width > size.Height ? name + " 横 / Landscape" : name + " 縦 / Portrait";
    }
    private void UpdatePdfSmartStatus()
    {
        if (Current is not { } state)
        {
            TextSelectionBadge.Visibility = Visibility.Collapsed;
            return;
        }
        string mode = state.Document.LastPrintMode switch
        {
            PrintMode.Scale when Math.Abs(state.Document.PrintPercent - 100) < .001 => "原寸 / Actual size",
            PrintMode.Scale => "指定倍率 / Scale",
            PrintMode.Fit => "用紙に合わせる / Fit to paper",
            PrintMode.TwoUp => "2ページ / 2-up",
            PrintMode.FourUp => "4ページ / 4-up",
            PrintMode.Booklet => "小冊子 / Booklet",
            PrintMode.Poster => "ポスター / Poster",
            _ => "印刷 / Print"
        };
        string percent = state.Document.LastPrintMode is PrintMode.Scale or PrintMode.Poster ? $"  •  {state.Document.PrintPercent:0.##}%" : "";
        TextSelectionInfo.Text = $"{PdfPageLabel(state.Document.SizeMm(state.Page))}  •  {mode}{percent}";
        TextSelectionBadge.ToolTip = "現在ページの原本サイズと印刷モードです。";
        TextSelectionBadge.Visibility = Visibility.Visible;
    }    private void UpdatePageInfo()
    {
        if (Current is not { } state) return;
        Size mm = state.Document.SizeMm(state.Page);
        PageNumber.Text = (state.Page + 1).ToString(); PageCount.Text = $"/ {state.Document.Count}";
        if (!ZoomText.IsKeyboardFocusWithin) ZoomText.Text = (state.Zoom * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        Status.Text = $"{System.IO.Path.GetFileName(state.Document.Path)}  ·  {mm.Width:F1} × {mm.Height:F1} mm  ·  印刷倍率 {state.Document.PrintPercent:0.##}%";

        UpdatePdfSmartStatus();
    }
    private void ViewerScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (changingLayout || Current is not { } state || pageViews.Count == 0) return;
        state.ScrollOffset = Viewer.VerticalOffset;
        if (!changingLayout)
        {
            double marker = Math.Min(200, Viewer.ViewportHeight / 3);
            int page = pageViews.FindIndex(v => v.Surface.TranslatePoint(new Point(0, v.Surface.Height), Viewer).Y > marker);
            if (page >= 0 && page != state.Page) { state.Page = page; }
            UpdatePageInfo();
        }
        zoomTimer.Stop(); zoomTimer.Start();
    }
    private async Task RenderVisible()
    {
        int version = ++renderVersion;
        if (windowClosed || !IsLoaded || WindowState == WindowState.Minimized || Current is not { } state) return;
        zoomTimer.Stop();
        try
        {
            // 全ページの画像を保持せず、画面付近だけ描画して大きなPDFのメモリを抑える。
            var visible = pageViews.Select((v, i) => (View: v, Page: i))
                .Where(x => { double top = x.View.Surface.TranslatePoint(new Point(), Viewer).Y;
                    return top < Viewer.ViewportHeight + 300 && top + x.View.Surface.Height > -300; }).ToArray();
            foreach (var view in pageViews.Except(visible.Select(x => x.View))) { view.Image.Source = null; view.RenderWidth = 0; }
            foreach (var item in visible)
            {
                var surface = item.View.Surface;
                double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
                double factor = Math.Min(dpi, Math.Sqrt(12_000_000.0 / Math.Max(1, visible.Length) / (surface.ActualWidth * surface.ActualHeight)));
                int w = Math.Max(1, (int)Math.Round(surface.ActualWidth * factor)), h = Math.Max(1, (int)Math.Round(surface.ActualHeight * factor));
                if (item.View.RenderWidth == w && item.View.RenderHeight == h && item.View.Image.Source != null) continue;
                var view = item.View;
                // Loaded・復帰・スクロールが重なっても同じ画素を作り直さない。
                if (view.PendingRender == null || view.PendingWidth != w || view.PendingHeight != h)
                {
                    view.PendingWidth = w; view.PendingHeight = h;
                    ++PdfRenderCountForTest;
                    view.PendingRender = Task.Run(() => state.Document.Render(item.Page, w, h, lcdText: true));
                }
                var pending = view.PendingRender;
                BitmapSource bitmap;
                try { bitmap = await pending; }
                finally { if (ReferenceEquals(view.PendingRender, pending)) view.PendingRender = null; }
                if (version != renderVersion || Current != state) return;
                view.Image.Source = bitmap; view.RenderWidth = w; view.RenderHeight = h;
            }
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { if (version == renderVersion) Error(ex); }
    }
    private void GoPage(int page)
    {
        if (Current is not { } state || pageViews.Count == 0) return;
        state.Page = Math.Clamp(page, 0, state.Document.Count - 1);
        Viewer.ScrollToVerticalOffset(PageSurface.TranslatePoint(new Point(), PagesHost).Y + 24);
        UpdatePageInfo();
    }
    private void PreviousClick(object s, RoutedEventArgs e) => GoPage((Current?.Page ?? 0) - 1);
    private void NextClick(object s, RoutedEventArgs e) => GoPage((Current?.Page ?? 0) + 1);
    private void PageNumberKeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter && int.TryParse(PageNumber.Text, out int n)) GoPage(n - 1); }
    private double ActiveZoom => Current?.Zoom ?? CurrentImage?.Zoom ?? CurrentText?.Zoom ?? 1;
    private void Zoom(double factor, Point? anchor = null)
    {
        if (Current is { } state) { ZoomPdf(state, factor, anchor); return; }
        if (CurrentImage is { } image) { ZoomImage(image, factor, anchor); return; }
        if (CurrentText is { } text)
        {
            text.Zoom = Math.Clamp(text.Zoom * factor, .1, 8);
            if (Math.Abs(text.Zoom - 1) < 1e-10) text.Zoom = 1;
            MarkdownViewer.Zoom = text.Zoom * 100;
            TextEditor.FontSize = 15 * text.Zoom;
            if (!ZoomText.IsKeyboardFocusWithin) ZoomText.Text = (text.Zoom * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            UpdateNonPdfStatus();
        }
    }
    private void ZoomPdf(TabState state, double factor, Point? anchor)
    {
        ClearPdfSelection();
        ++renderVersion;
        zoomPauseUntil = 0;
        double old = state.Zoom; state.Zoom = Math.Clamp(old * factor, .1, 8);
        if (Math.Abs(state.Zoom - 1) < 1e-10) state.Zoom = 1;
        double ratio = state.Zoom / old;
        Point point = anchor ?? new Point(Viewer.ViewportWidth / 2, Viewer.ViewportHeight / 2);
        var anchorView = pageViews.FirstOrDefault(v => v.Surface.TranslatePoint(new Point(0, v.Surface.Height), Viewer).Y > point.Y) ?? pageViews[^1];
        Point pageAnchor = Viewer.TranslatePoint(point, anchorView.Surface);
        changingLayout = true;
        SizePages(); if (!ZoomText.IsKeyboardFocusWithin) ZoomText.Text = (state.Zoom * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        Viewer.UpdateLayout();
        Point moved = anchorView.Surface.TranslatePoint(new Point(pageAnchor.X * ratio, pageAnchor.Y * ratio), Viewer);
        Viewer.ScrollToHorizontalOffset(Viewer.HorizontalOffset + moved.X - point.X);
        Viewer.ScrollToVerticalOffset(Viewer.VerticalOffset + moved.Y - point.Y);
        Viewer.UpdateLayout(); changingLayout = false;
        state.ScrollOffset = Viewer.VerticalOffset;
        zoomTimer.Stop(); zoomTimer.Start();
    }
    private void ZoomImage(ImageTabState state, double factor, Point? anchor)
    {
        double old = state.Zoom; state.Zoom = Math.Clamp(old * factor, .1, 8);
        if (Math.Abs(state.Zoom - 1) < 1e-10) state.Zoom = 1;
        double ratio = state.Zoom / old;
        Point point = anchor ?? new Point(ImageViewer.ViewportWidth / 2, ImageViewer.ViewportHeight / 2);
        Point imageAnchor = ImageViewer.TranslatePoint(point, ReaderImage);
        ApplyImageLayout(state); ImageViewer.UpdateLayout();
        Point moved = ReaderImage.TranslatePoint(new Point(imageAnchor.X * ratio, imageAnchor.Y * ratio), ImageViewer);
        ImageViewer.ScrollToHorizontalOffset(ImageViewer.HorizontalOffset + moved.X - point.X);
        ImageViewer.ScrollToVerticalOffset(ImageViewer.VerticalOffset + moved.Y - point.Y);
        if (!ZoomText.IsKeyboardFocusWithin) ZoomText.Text = (state.Zoom * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        UpdateNonPdfStatus();
    }
    private double ImageFitWidthZoom(ImageTabState state)
    {
        bool side = Math.Abs(state.Rotation) % 180 == 90;
        double width = side ? state.DisplayHeight : state.DisplayWidth;
        ImageViewer.UpdateLayout();
        double viewportWidth = ImageViewer.ViewportWidth > 0 ? ImageViewer.ViewportWidth : ImageViewer.ActualWidth;
        double availableWidth = (viewportWidth > 0 ? viewportWidth : ContentGrid.ActualWidth) - 24;
        return availableWidth > 0 ? Math.Clamp(availableWidth / Math.Max(1, width), .1, 8) : double.NaN;
    }
    private bool FitImageForInitialDisplay(ImageTabState state)
    {
        double target = ImageFitWidthZoom(state);
        if (!double.IsFinite(target)) return false;
        state.Zoom = target;
        ApplyImageLayout(state);
        ImageViewer.ScrollToHorizontalOffset(0);
        ImageViewer.ScrollToVerticalOffset(0);
        return true;
    }
    private async Task RefreshSvgAsync()
    {
        if (svgRendering) return; // 同時に巨大な描画用メモリを確保しない。
        svgTimer.Stop();
        if (windowClosed || CurrentImage is not { SvgBytes: { } bytes } state) return;
        int request = svgRequestVersion;
        var dpi = VisualTreeHelper.GetDpi(this);
        var target = SvgRasterSize(state.DisplayWidth, state.DisplayHeight, state.Zoom * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY));
        if (Math.Abs(state.Image.PixelWidth - target.Width) <= 1) return;
        svgRendering = true;
        try
        {
            var bitmap = await Task.Run(() => LoadSvgBitmap(bytes, target.Width));
            if (windowClosed || request != svgRequestVersion || !ReferenceEquals(CurrentImage, state)) return;
            // 表示サイズ・スクロール位置を変えずに画素だけ入れ替える。
            state.Image = bitmap;
            ReaderImage.Source = bitmap;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            if (!windowClosed && request == svgRequestVersion)
                Status.Text = "SVGの高精細表示を更新できなかったため、現在の画像を保持しています。";
        }
        finally { svgRendering = false; }
    }
    private void ApplyImageLayout(ImageTabState state)
    {
        ReaderImage.Width = state.DisplayWidth * state.Zoom;
        ReaderImage.Height = state.DisplayHeight * state.Zoom;
        ReaderImage.LayoutTransform = new RotateTransform(state.Rotation);
        if (state.SvgBytes != null) { ++svgRequestVersion; svgTimer.Stop(); svgTimer.Start(); }
        RenderOptions.SetBitmapScalingMode(ReaderImage, state.Zoom >= 1 ? BitmapScalingMode.HighQuality : BitmapScalingMode.Fant);
    }
    private void UpdateNonPdfStatus()
    {
        if (CurrentText is { } text)
            Status.Text = text.IsMarkdown ? $"{System.IO.Path.GetFileName(text.Path)}  ·  {(text.SourceMode ? "Source編集" : "Preview")}  ·  Ctrl＋Shift＋Mで切替" : $"{(string.IsNullOrEmpty(text.Path) ? "無題.txt" : System.IO.Path.GetFileName(text.Path))}  ·  編集可能  ·  Ctrl＋Sで保存";
        else if (CurrentImage is { } image) Status.Text = $"{System.IO.Path.GetFileName(image.Path)}  ·  {image.DisplayWidth} × {image.DisplayHeight} px  ·  表示 {image.Zoom * 100:0.##}%";
    }
    private void ToggleWrap(object s, RoutedEventArgs e) => ToggleWrapForTest();
    internal bool ToggleWrapForTest()
    {
        if (CurrentText is not { ShowEditor: true } state) return false;
        state.Wrap = !state.Wrap;
        TextEditor.TextWrapping = state.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        TextEditor.HorizontalScrollBarVisibility = state.Wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        UpdateTextSmartStatus();
        return state.Wrap;
    }
    private async void MarkdownModeClick(object s, RoutedEventArgs e) => await ToggleMarkdownModeAsync();
    internal async Task ToggleMarkdownModeAsync()
    {
        if (CurrentText is not { IsMarkdown: true } state) return;
        if (state.SourceMode)
        {
            state.Document = LightweightTextRenderer.Build(state.LiveText);
            state.SourceMode = false;
        }
        else state.SourceMode = true;
        await RenderCurrent();
        if (state.SourceMode) TextEditor.Focus();
    }
    private void UpdateMarkdownPreviewStatus()
    {
        if (CurrentText is not { IsMarkdown: true, SourceMode: false } state) return;
        TextSelectionInfo.Text = $"プレビュー / Preview  •  MD  •  {EncodingLabel(state.Encoding)}";
        TextSelectionBadge.ToolTip = "整形表示中 / Preview mode。上の </> または Ctrl+Shift+M でソース編集 / Sourceへ切り替えます。";
        TextSelectionBadge.Visibility = Visibility.Visible;
    }
    private void ZoomInputGotFocus(object s, KeyboardFocusChangedEventArgs e) => ZoomText.SelectAll();
    private void ApplyZoomInput()
    {
        if (Current == null && CurrentImage == null && CurrentText == null) return;
        string input = ZoomText.Text.Trim().TrimEnd('%', '％');
        if ((double.TryParse(input, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out double percent) ||
            double.TryParse(input, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out percent)) && double.IsFinite(percent) && percent >= 10 && percent <= 800)
            Zoom(percent / 100 / ActiveZoom);
        else Status.Text = "表示倍率は10～800％で入力してください。";
        ZoomText.Text = (ActiveZoom * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }
    private void ZoomInputKeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) { ApplyZoomInput(); e.Handled = true; } }
    private void ZoomInputLostFocus(object s, KeyboardFocusChangedEventArgs e) => ApplyZoomInput();
    private void ZoomInClick(object s, RoutedEventArgs e) => StepZoom(1.2);
    private void ZoomOutClick(object s, RoutedEventArgs e) => StepZoom(1 / 1.2);
    private void FitClick(object s, RoutedEventArgs e)
    {
        if (Current is { } state && PageSurface.Width > 0)
        {
            double target = Math.Clamp(state.Zoom * (Viewer.ViewportWidth - 56) / PageSurface.Width, .1, 8);
            Zoom(Math.Abs(target - state.Zoom) < .005 ? 1 / state.Zoom : target / state.Zoom);
        }
        else if (CurrentImage is { } image)
        {
            double target = ImageFitWidthZoom(image);
            if (double.IsFinite(target)) Zoom(Math.Abs(target - image.Zoom) < .005 ? 1 / image.Zoom : target / image.Zoom);
        }
    }
    private void StepZoom(double factor, Point? anchor = null)
    {
        if ((Current == null && CurrentImage == null && CurrentText == null) || Environment.TickCount64 < zoomPauseUntil) return;
        double current = ActiveZoom, target = current * factor;
        bool stopAt100 = (current < 1 && target >= 1) || (current > 1 && target <= 1);
        if (stopAt100) target = 1;
        Zoom(target / current, anchor);
        if (stopAt100) zoomPauseUntil = Environment.TickCount64 + 450;
        ZoomText.Text = (ActiveZoom * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }
    internal void ZoomByWheel(int delta, Point? anchor = null)
    {
        if (delta != 0) StepZoom(delta > 0 ? 1.12 : 1 / 1.12, anchor);
    }
    internal double ActiveZoomForTest => ActiveZoom;
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
    internal double MarkdownOffset => FindVisualChild<ScrollViewer>(MarkdownViewer)?.VerticalOffset ?? 0;
    internal void ScrollMarkdownByWheel(int delta)
    {
        if (FindVisualChild<ScrollViewer>(MarkdownViewer) is not { } scroll || delta == 0) return;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset - Math.Sign(delta) * 180);
    }
    private void MarkdownWheel(object s, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) ZoomByWheel(e.Delta);
        else ScrollMarkdownByWheel(e.Delta);
        e.Handled = true;
    }
    private void ImageWheel(object s, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) { ZoomByWheel(e.Delta, e.GetPosition(ImageViewer)); e.Handled = true; }
    }
    private void ImageClick(object s, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && CurrentImage is { } image) { if (Math.Abs(image.Zoom - 1) < .001) FitClick(s, e); else Zoom(1 / image.Zoom, e.GetPosition(ImageViewer)); e.Handled = true; }
    }
    private static int PdfCharacterAt(PageView view, Point point)
    {
        if (view.Characters == null || view.Surface.ActualWidth <= 0 || view.Surface.ActualHeight <= 0) return -1;
        int nearest = -1; double nearestDistance = double.MaxValue;
        for (int i = 0; i < view.Characters.Count; i++)
        {
            Rect relative = view.Characters[i].RelativeBox;
            if (relative.IsEmpty) continue;
            var box = new Rect(relative.X * view.Surface.ActualWidth, relative.Y * view.Surface.ActualHeight,
                Math.Max(1, relative.Width * view.Surface.ActualWidth), Math.Max(1, relative.Height * view.Surface.ActualHeight));
            if (box.Contains(point)) return i;
            double dx = point.X < box.Left ? box.Left - point.X : point.X > box.Right ? point.X - box.Right : 0;
            double dy = point.Y < box.Top ? box.Top - point.Y : point.Y > box.Bottom ? point.Y - box.Bottom : 0;
            double distance = dx * dx + dy * dy;
            if (distance < nearestDistance) { nearestDistance = distance; nearest = i; }
        }
        return nearestDistance <= 40 * 40 ? nearest : -1;
    }
    private void PdfSelectionStarted(object s, MouseButtonEventArgs e)
    {
        if (Current is not { } state || s is not Canvas { Tag: PageView view }) return;
        view.Characters ??= state.Document.TextCharacters(view.Page);
        int hit = PdfCharacterAt(view, e.GetPosition(view.Selection));
        ClearPdfSelection();
        if (hit < 0) return;
        selectingPage = view; selectionAnchor = hit;
        view.Selection.CaptureMouse(); UpdatePdfSelection(view, hit); e.Handled = true;
    }
    private void PdfSelectionMoved(object s, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || selectingPage == null || s != selectingPage.Selection) return;
        int hit = PdfCharacterAt(selectingPage, e.GetPosition(selectingPage.Selection));
        if (hit >= 0) UpdatePdfSelection(selectingPage, hit);
    }
    private void PdfSelectionEnded(object s, MouseButtonEventArgs e)
    {
        if (selectingPage != null) selectingPage.Selection.ReleaseMouseCapture();
        selectingPage = null; selectionAnchor = -1; e.Handled = true;
    }
    private void UpdatePdfSelection(PageView view, int end)
    {
        if (view.Characters == null || selectionAnchor < 0) return;
        int first = Math.Min(selectionAnchor, end), last = Math.Max(selectionAnchor, end);
        selectedPdfText = new string(view.Characters.Skip(first).Take(last - first + 1).Select(x => x.Character).ToArray()).TrimEnd('\0');
        view.Selection.Children.Clear();
        foreach (PdfTextCharacter character in view.Characters.Skip(first).Take(Math.Min(last - first + 1, 10000)))
        {
            Rect box = character.RelativeBox; if (box.IsEmpty || char.IsWhiteSpace(character.Character)) continue;
            var mark = new System.Windows.Shapes.Rectangle { Width = Math.Max(1, box.Width * view.Surface.ActualWidth), Height = Math.Max(1, box.Height * view.Surface.ActualHeight), Fill = new SolidColorBrush(Color.FromArgb(95, 40, 125, 245)), IsHitTestVisible = false };
            Canvas.SetLeft(mark, box.X * view.Surface.ActualWidth); Canvas.SetTop(mark, box.Y * view.Surface.ActualHeight); view.Selection.Children.Add(mark);
        }
        Status.Text = $"{selectedPdfText.Length}文字を選択  ·  Ctrl＋Cでコピー";

        TextSelectionInfo.Text = $"{selectedPdfText.Length}文字選択 / {selectedPdfText.Length} chars selected  •  コピー / Ctrl+C";
        TextSelectionBadge.ToolTip = "選択したPDF文字をコピー / Copy selected PDF text（Ctrl+C）";
        TextSelectionBadge.Visibility = Visibility.Visible;
    }
    private void ClearPdfSelection()
    {
        foreach (PageView view in pageViews) view.Selection.Children.Clear();
        selectedPdfText = ""; selectingPage = null; selectionAnchor = -1;
    }
    internal string SelectPdfTextForTest(int page, int first, int last)
    {
        if (Current is not { } state) return "";
        var characters = state.Document.TextCharacters(page);
        if (characters.Count == 0) return "";
        first = Math.Clamp(first, 0, characters.Count - 1); last = Math.Clamp(last, first, characters.Count - 1);
        pageViews[page].Characters = characters; selectionAnchor = first; UpdatePdfSelection(pageViews[page], last); return selectedPdfText;
    }
    internal bool CopySelectedPdfForTest()
    {
        if (string.IsNullOrEmpty(selectedPdfText)) return false;
        Clipboard.SetText(selectedPdfText); return Clipboard.GetText() == selectedPdfText;
    }

    internal void ScrollPdfByWheel(int delta)
    {
        if (delta == 0) return;
        // PDFは縦に連続して読むため、Windows標準より少し大きい一定量で進める。
        Viewer.ScrollToVerticalOffset(Viewer.VerticalOffset - delta / 120.0 * 120);
    }
    private void ViewerWheel(object s, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) ZoomByWheel(e.Delta, e.GetPosition(Viewer));
        else ScrollPdfByWheel(e.Delta);
        e.Handled = true;
    }
    private async void Rotate(int delta)
    {
        if (CurrentImage is { } image)
        {
            image.Rotation = (image.Rotation + delta * 90) % 360;
            ApplyImageLayout(image); UpdateNonPdfStatus(); return;
        }
        if (Current is not { } state) return;
        try
        {
            state.Document.Rotate(state.Page, delta);
            UpdateTabTitle(state); await RenderCurrent();
        }
        catch (Exception ex) { Error(ex); }
    }
    private void RotateLeftClick(object s, RoutedEventArgs e) => Rotate(-1);
    private void RotateRightClick(object s, RoutedEventArgs e) => Rotate(1);
    private async void ResetRotationClick(object s, RoutedEventArgs e)
    {
        if (CurrentImage is { } image)
        {
            image.Rotation = 0;
            ApplyImageLayout(image);
            UpdateNonPdfStatus();
            return;
        }
        if (Current is not { } state) return;
        try
        {
            state.Document.ResetRotation(state.Page);
            UpdateTabTitle(state);
            await RenderCurrent();
        }
        catch (Exception ex) { Error(ex); }
    }
    private void UpdateTabTitle(TabState state)
    {
        foreach (TabItem tab in Tabs.Items) if (tab.Tag == state) SetTabTitle(tab, System.IO.Path.GetFileName(state.Document.Path) + (state.Document.Dirty ? " *" : ""));
    }
    private bool Save(TabState state)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PDFファイル|*.pdf", FileName = System.IO.Path.GetFileNameWithoutExtension(state.Document.Path) + "_編集.pdf", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return false;
        try { state.Document.SaveCopy(dialog.FileName); UpdateTabTitle(state); Status.Text = $"保存しました: {dialog.FileName}"; return true; }
        catch (Exception ex) { Error(ex); return false; }
    }
    private void SaveClick(object s, RoutedEventArgs e)
    {
        if (Current is { } state) Save(state);
        else if (CurrentText is { CanEdit: true } text) SaveText(text);
    }
    private void SaveTextClick(object s, RoutedEventArgs e) { if (CurrentText is { CanEdit: true } text) SaveText(text); }
    internal bool SaveTextForTest() => CurrentText is { CanEdit: true } text && SaveText(text);
    private bool SaveText(TextTabState state, bool saveAs = false)
    {
        string destination = state.Path;
        if (saveAs || string.IsNullOrEmpty(destination))
        {
            string filter = state.IsMarkdown ? "Markdown|*.md;*.markdown" : "テキストファイル|*.txt";
            string defaultName = state.IsMarkdown ? "無題.md" : "無題.txt";
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = string.IsNullOrEmpty(destination) ? defaultName : System.IO.Path.GetFileName(destination), OverwritePrompt = true };
            if (dialog.ShowDialog(this) != true) return false;
            destination = dialog.FileName;
        }
        return SaveTextToPath(state, destination);
    }
    private static bool HasExternalTextChange(TextTabState state, string destination) =>
        string.Equals(destination, state.Path, StringComparison.OrdinalIgnoreCase) && state.FileHash != null && File.Exists(destination) &&
        !SHA256.HashData(File.ReadAllBytes(destination)).SequenceEqual(state.FileHash);
    internal bool CurrentTextHasExternalChangeForTest() => CurrentText is { CanEdit: true } text && HasExternalTextChange(text, text.Path);
    private bool SaveTextToPath(TextTabState state, string destination)
    {
        try
        {
            if (HasExternalTextChange(state, destination) &&
                MessageBox.Show(this, "このファイルは別のアプリで変更されています。AiryViewの内容で上書きしますか？", "外部で変更されています", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
            string folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destination))!;
            Directory.CreateDirectory(folder);
            string temporary = System.IO.Path.Combine(folder, "." + System.IO.Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, state.LiveText, state.Encoding);
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            state.Path = destination; state.Text = state.LiveText; state.FileHash = SHA256.HashData(File.ReadAllBytes(destination)); state.Dirty = false; UpdateTextTabTitle(state);
            foreach (TabItem tab in Tabs.Items) if (tab.Tag == state) tab.ToolTip = destination;
            Status.Text = $"保存しました: {destination}"; return true;
        }
        catch (Exception ex) { Error(ex); return false; }
    }
    internal bool SaveTextToPathForTest(string destination) => CurrentText is { CanEdit: true } text && SaveTextToPath(text, destination);
    private void TextEditorChanged(object s, TextChangedEventArgs e)
    {
        if (opening || CurrentText is not { ShowEditor: true } state) return;
        state.LiveText = TextEditor.Text;
        state.Dirty = state.LiveText != state.Text; UpdateTextTabTitle(state);
        UpdateTextSmartStatus();
    }
    private void TextEditorSelectionChanged(object s, RoutedEventArgs e) { if (!opening) UpdateTextSmartStatus(); }
    private void UpdateTextSmartStatus()
    {
        if (CurrentText is not { ShowEditor: true } state)
        {
            TextSelectionBadge.Visibility = Visibility.Collapsed;
            return;
        }
        if (TextEditor.SelectionLength > 0)
        {
            var count = CountCharacterWidths(TextEditor.SelectedText);
            string source = state.IsMarkdown ? "  •  ソース / Source" : "";
            TextSelectionInfo.Text = $"選択 {count.Total}字 / {count.Total} chars  •  全角 {count.FullWidth} / Full {count.FullWidth}  •  半角 {count.HalfWidth} / Half {count.HalfWidth}  •  幅 {count.HalfWidthEquivalent} / Width {count.HalfWidthEquivalent}{source}  •  折返し {(state.Wrap ? "ON" : "OFF")} / Wrap {(state.Wrap ? "ON" : "OFF")}";
            TextSelectionBadge.ToolTip = "選択範囲の文字数 / Selected text counts。幅 / Widthは全角を2、半角を1として数えます。";
        }
        else
        {
            // 開いた直後の先頭位置は確定しているので、行番号のために全文をレイアウトしない。
            int line = TextEditor.CaretIndex == 0 ? 0 : Math.Max(0, TextEditor.GetLineIndexFromCharacterIndex(TextEditor.CaretIndex));
            int lineStart = TextEditor.CaretIndex == 0 ? 0 : TextEditor.GetCharacterIndexFromLineIndex(line);
            int column = Math.Max(0, TextEditor.CaretIndex - lineStart);
            string source = state.IsMarkdown ? "  •  ソース / Source" : "";
            TextSelectionInfo.Text = $"行 {line + 1} / Ln {line + 1}  •  列 {column + 1} / Col {column + 1}  •  {EncodingLabel(state.Encoding)}  •  {LineEndingLabel(state.LiveText)}{source}  •  折返し {(state.Wrap ? "ON" : "OFF")} / Wrap {(state.Wrap ? "ON" : "OFF")}";
            TextSelectionBadge.ToolTip = "現在の行・列・文字コード / Current line, column, and encoding";
        }
        TextSelectionBadge.Visibility = Visibility.Visible;
    }
    private static string LineEndingLabel(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "改行形式 CRLF / EOL CRLF" : text.Contains('\n') ? "改行形式 LF / EOL LF" : "改行なし / No EOL";
    private static string EncodingLabel(Encoding encoding) => encoding.CodePage switch
    {
        65001 => "UTF-8",
        932 => "Shift-JIS",
        1200 => "UTF-16 LE",
        1201 => "UTF-16 BE",
        12000 => "UTF-32 LE",
        12001 => "UTF-32 BE",
        _ => encoding.WebName.ToUpperInvariant()
    };
    internal static (int Total, int FullWidth, int HalfWidth, int HalfWidthEquivalent) CountCharacterWidths(string text)
    {
        int full = 0, half = 0;
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = (string)elements.Current!;
            int codePoint = char.ConvertToUtf32(element, 0);
            if (codePoint is 10 or 13) continue;
            if (codePoint <= 0x7F || codePoint is >= 0xFF61 and <= 0xFFDC) half++;
            else full++;
        }
        return (full + half, full, half, full * 2 + half);
    }
    private void UpdateTextTabTitle(TextTabState state)
    {
        foreach (TabItem tab in Tabs.Items) if (tab.Tag == state) SetTabTitle(tab, (string.IsNullOrEmpty(state.Path) ? "無題.txt" : System.IO.Path.GetFileName(state.Path)) + (state.Dirty ? " *" : ""));
    }
    private bool CanClose(TextTabState state)
    {
        if (!state.CanEdit || !state.Dirty) return true;
        var result = MessageBox.Show(this, $"{System.IO.Path.GetFileName(state.Path)} の変更を保存しますか？", "未保存の変更", MessageBoxButton.YesNoCancel);
        return result == MessageBoxResult.No || result == MessageBoxResult.Yes && SaveText(state);
    }
    private bool CanClose(TabState state)
    {
        if (!state.Document.Dirty) return true;
        var result = MessageBox.Show(this, $"{System.IO.Path.GetFileName(state.Document.Path)} の変更を保存しますか？", "未保存の変更", MessageBoxButton.YesNoCancel);
        return result == MessageBoxResult.No || result == MessageBoxResult.Yes && Save(state);
    }
    private void CloseTabClick(object s, RoutedEventArgs e)
    {
        if (s is Button { Tag: TabItem tab }) CloseTab(tab);
        e.Handled = true;
    }
    private void CloseClick(object s, RoutedEventArgs e)
    {
        if (Tabs.SelectedItem is TabItem tab) CloseTab(tab);
    }
    private void CloseTab(TabItem tab)
    {
        if (tab.Tag is TabState state && !CanClose(state)) return;
        if (tab.Tag is TextTabState text && !CanClose(text)) return;
        if (StatePath(tab.Tag) is { } path && File.Exists(path))
        {
            closedPaths.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
            closedPaths.Add(path);
            if (closedPaths.Count > 20) closedPaths.RemoveAt(0);
        }
        ++renderVersion; Tabs.Items.Remove(tab);
        if (tab.Tag is TabState pdf) pdf.Document.Dispose();
    }
    private async void RestoreClosedTab(object s, RoutedEventArgs e) => await RestoreClosedTabAsync();
    internal async Task<bool> RestoreClosedTabAsync()
    {
        while (closedPaths.Count > 0)
        {
            string path = closedPaths[^1];
            closedPaths.RemoveAt(closedPaths.Count - 1);
            if (!File.Exists(path)) continue;
            await OpenPathsAsync([path]);
            return true;
        }
        return false;
    }
    internal void CloseCurrentTabForTest() { if (Tabs.SelectedItem is TabItem tab) CloseTab(tab); }
    private void WindowClosing(object? s, CancelEventArgs e)
    {
        var states = Tabs.Items.Cast<TabItem>().Select(t => t.Tag).OfType<TabState>().ToArray();
        var textStates = Tabs.Items.Cast<TabItem>().Select(t => t.Tag).OfType<TextTabState>().ToArray();
        if (states.Any(state => !CanClose(state)) || textStates.Any(state => !CanClose(state))) { e.Cancel = true; return; }
        WindowPreferences.Save(this);
        ++renderVersion; zoomTimer.Stop(); foreach (var state in states) state.Document.Dispose();
    }
    private void PrintClick(object s, RoutedEventArgs e)
    {
        try
        {
            if (Current is { } state) { new PrintWindow(state.Document, state.Page, null) { Owner = this }.ShowDialog(); UpdatePageInfo(); }
            else if (CurrentText is { Editable: true } text)
            {
                var dialog = new System.Windows.Controls.PrintDialog();
                if (dialog.ShowDialog() == true)
                {
                    var document = LightweightTextRenderer.BuildPlain(text.LiveText);
                    document.PagePadding = new Thickness(48); document.ColumnWidth = double.PositiveInfinity;
                    document.PageWidth = dialog.PrintableAreaWidth; document.PageHeight = dialog.PrintableAreaHeight;
                    dialog.PrintDocument(((System.Windows.Documents.IDocumentPaginatorSource)document).DocumentPaginator, string.IsNullOrEmpty(text.Path) ? "無題.txt" : System.IO.Path.GetFileName(text.Path));
                }
            }
        }
        catch (Exception ex) { Error(ex); }
    }
    private void CalibrationClick(object s, RoutedEventArgs e)
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Samples", "print-check.pdf");
        if (!File.Exists(path)) { Error(new IOException("寸法確認用PDFが見つかりません。実行用フォルダ全体を使用してください。")); return; }
        OpenPaths([path]);
    }
    private void HelpClick(object s, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "AiryView 2.0.17\n\n対応形式：PDF、Markdown、TXT、JPEG、PNG、TIFF、BMP、GIF、ICO、WebP、SVG\nファイルを開く：Ctrl＋O、またはドラッグ＆ドロップ\nページ移動：ホイールで連続スクロール、ページ番号入力、左右のボタン\nPDF・画像の拡大縮小：Ctrl＋ホイール、＋／−、倍率入力、画面幅に合わせる\n画像：回転アイコン、ダブルクリックで100％／画面幅表示\nMarkdown：Ctrl＋Shift＋MでPreview／Source編集、SourceはAlt＋Zで折り返し、Ctrl＋Sで保存\nTXT：Alt＋Zで折り返し、Ctrl＋Sで安全に保存、Ctrl＋Fで検索、Ctrl＋Pで印刷\n共通：Ctrl＋Shift＋Tで閉じたタブを復元、Ctrl＋0で100％、Ctrl＋＋／－で倍率変更\nPDF文字の選択：文字をドラッグ、Ctrl＋Cでコピー\n印刷：Ctrl＋P\nPDFの入力・注釈・検索・署名確認：Ctrl＋F\nパスワードはファイルを開く際に入力します。保存・ログには残しません。\n\n新しいPDFの印刷倍率は100%。指定倍率では自動縮小せず、欠けをプレビューで知らせます。\nドライバー側の拡大縮小・Nアップは無効にしてください。\n回転を保存するときは別名保存します。\n\n寸法確認用PDFには縦横100mmの基準線があります。\n会社での印刷は利用者評価で用途上合格（約0.1mmのずれに見えるとの報告）。",
            "AiryView — 使い方", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void ToolsClick(object sender, RoutedEventArgs e)
    {
        if (Current is not { } state) return;
        new PdfToolsWindow(state.Document, state.Page, GoPage, async path => await OpenPathsAsync([path])) { Owner = this }.ShowDialog();
    }
    private static IEnumerable<System.Windows.Documents.Run> FindRuns(DependencyObject parent)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is System.Windows.Documents.Run run) yield return run;
            else if (child is DependencyObject nested) foreach (var item in FindRuns(nested)) yield return item;
        }
    }
    private void ClearTextSearch()
    {
        foreach (var run in textMatches) run.Background = null;
        textMatches.Clear(); textEditorMatches.Clear(); textEditorQueryLength = 0; textMatchIndex = -1;
        if (TextSearchCount != null) TextSearchCount.Text = "";
    }
    private void ShowTextSearch()
    {
        if (CurrentText == null) return;
        TextSearchBar.Visibility = Visibility.Visible;
        TextSearchInput.Focus(); TextSearchInput.SelectAll();
    }
    private void SearchText(bool forward)
    {
        if (CurrentText is not { } state) return;
        string query = TextSearchInput.Text;
        ClearTextSearch();
        if (string.IsNullOrWhiteSpace(query)) return;
        if (state.ShowEditor)
        {
            textEditorQueryLength = query.Length;
            for (int at = 0; at <= TextEditor.Text.Length - query.Length;)
            {
                int found = TextEditor.Text.IndexOf(query, at, StringComparison.CurrentCultureIgnoreCase);
                if (found < 0) break;
                textEditorMatches.Add(found); at = found + Math.Max(1, query.Length);
            }
            if (textEditorMatches.Count == 0) { TextSearchCount.Text = "0件"; return; }
            textMatchIndex = forward ? 0 : textEditorMatches.Count - 1; ShowTextMatch(); return;
        }
        textMatches.AddRange(FindRuns(state.Document).Where(run => run.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
        foreach (var run in textMatches) run.Background = new SolidColorBrush(Color.FromRgb(255, 240, 150));
        if (textMatches.Count == 0) { TextSearchCount.Text = "0件"; return; }
        textMatchIndex = forward ? 0 : textMatches.Count - 1;
        ShowTextMatch();
    }
    private void MoveTextMatch(int delta)
    {
        int count = CurrentText?.ShowEditor == true ? textEditorMatches.Count : textMatches.Count;
        if (count == 0) { SearchText(delta >= 0); return; }
        if (CurrentText?.ShowEditor != true) textMatches[textMatchIndex].Background = new SolidColorBrush(Color.FromRgb(255, 240, 150));
        textMatchIndex = (textMatchIndex + delta + count) % count;
        ShowTextMatch();
    }
    private void ShowTextMatch()
    {
        if (CurrentText?.ShowEditor == true)
        {
            TextEditor.Focus(); TextEditor.Select(textEditorMatches[textMatchIndex], textEditorQueryLength);
            TextEditor.ScrollToLine(TextEditor.GetLineIndexFromCharacterIndex(textEditorMatches[textMatchIndex]));
            TextSearchCount.Text = $"{textMatchIndex + 1} / {textEditorMatches.Count}"; return;
        }
        var run = textMatches[textMatchIndex];
        run.Background = new SolidColorBrush(Color.FromRgb(255, 190, 80));
        run.BringIntoView();
        TextSearchCount.Text = $"{textMatchIndex + 1} / {textMatches.Count}";
    }
    private void TextSearchKeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) MoveTextMatch(-1); else SearchText(true); e.Handled = true; }
        else if (e.Key == Key.Escape) { CloseTextSearch(s, e); e.Handled = true; }
    }
    internal int SearchTextForTest(string query) { ShowTextSearch(); TextSearchInput.Text = query; SearchText(true); return CurrentText?.ShowEditor == true ? textEditorMatches.Count : textMatches.Count; }
    private void TextSearchGotFocus(object s, KeyboardFocusChangedEventArgs e) => TextSearchInput.SelectAll();
    private void PreviousTextMatch(object s, RoutedEventArgs e) => MoveTextMatch(-1);
    private void NextTextMatch(object s, RoutedEventArgs e) => MoveTextMatch(1);
    private void CloseTextSearch(object s, RoutedEventArgs e) { ClearTextSearch(); TextSearchBar.Visibility = Visibility.Collapsed; if (CurrentText?.ShowEditor == true) TextEditor.Focus(); else MarkdownViewer.Focus(); }

    private void WindowKeyDown(object s, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Alt && (e.Key == Key.Z || e.SystemKey == Key.Z)) { ToggleWrap(s, e); e.Handled = true; return; }
        if (e.Key == Key.F3 && CurrentText != null) { if (TextSearchBar.Visibility != Visibility.Visible) ShowTextSearch(); else MoveTextMatch(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); e.Handled = true; return; }
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.M) { MarkdownModeClick(s, e); e.Handled = true; return; }
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.T) { RestoreClosedTab(s, e); e.Handled = true; return; }
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.S)
        {
            if (CurrentText is { CanEdit: true } text) SaveText(text, true);
            e.Handled = true; return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.C && Current != null && CopySelectedPdfForTest()) { Status.Text = $"{selectedPdfText.Length}文字をコピーしました。"; e.Handled = true; return; }
            if (e.Key is Key.N or Key.T) { NewText(); e.Handled = true; }
            if (e.Key == Key.O) { OpenClick(s, e); e.Handled = true; }
            if (e.Key == Key.P) { PrintClick(s, e); e.Handled = true; }
            if (e.Key == Key.S) { SaveClick(s, e); e.Handled = true; }
            if (e.Key == Key.F) { if (CurrentText != null) ShowTextSearch(); else ToolsClick(s, e); e.Handled = true; }
            if (e.Key is Key.Add or Key.OemPlus) { StepZoom(1.2); e.Handled = true; }
            if (e.Key is Key.Subtract or Key.OemMinus) { StepZoom(1 / 1.2); e.Handled = true; }
            if (e.Key is Key.D0 or Key.NumPad0) { if (ActiveZoom > 0) Zoom(1 / ActiveZoom); e.Handled = true; }
            if (e.Key == Key.W) { CloseClick(s, e); e.Handled = true; }
        }
    }
}
