using System.Drawing.Printing;
using System.Globalization;
using System.Runtime.InteropServices;


namespace AiryView;

public partial class PrintWindow : Window
{
    private readonly PdfDocument document;
    private readonly int currentPage;
    private readonly Rect? selectedRegion;
    private readonly PrintDocument printer = new();
    private List<Sheet> sheets = [];
    private List<PrintSheet> printPlan = [];
    private Rect? activeRegion;
    private PrintMode activeMode;
    private bool ready, previewReady;
    private readonly System.Windows.Threading.DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int side, previewVersion, settingsVersion;
    internal IReadOnlyList<PrintSheet> PlanForTest => printPlan;
    internal Task ShowSideForTest(int index) { side = index; return ShowSide(); }
    public PrintWindow(PdfDocument doc, int page, Rect? region)
    {
        document = doc; currentPage = page; selectedRegion = region;
        InitializeComponent();
        PercentBox.Text = doc.PrintPercent.ToString(CultureInfo.InvariantCulture);
        ModeBox.SelectedIndex = (int)doc.LastPrintMode;
        RegionBox.IsEnabled = region.HasValue; RegionBox.IsChecked = region.HasValue;
        printer.OriginAtMargins = false;
        printer.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        printer.PrinterSettings.Copies = 1;
        printer.PrinterSettings.Duplex = Duplex.Simplex;
        foreach (string name in PrinterSettings.InstalledPrinters) PrinterBox.Items.Add(name);
        PrinterBox.SelectedItem = printer.PrinterSettings.PrinterName;
        LoadPapers();
        Size sourceSize = doc.SizeMm(page);
        OrientationBox.SelectedIndex = sourceSize.Width > sourceSize.Height ? 1 : 0;
        previewTimer.Tick += (_, _) => { previewTimer.Stop(); Refresh(); };
        ready = true;
        UpdatePaperControls();
        SourceInitialized += (_, _) => FitToScreen();
        Loaded += (_, _) => Refresh();
        Closed += (_, _) => { ready = false; previewTimer.Stop(); ++previewVersion; ++settingsVersion; printer.Dispose(); };
    }
    private void FitToScreen()
    {
        Window reference = Owner ?? this;
        var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(reference).Handle);
        var transform = PresentationSource.FromVisual(reference)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var area = screen.WorkingArea;
        Point origin = transform.Transform(new Point(area.Left, area.Top));
        Point end = transform.Transform(new Point(area.Right, area.Bottom));
        FitToWorkArea(new Rect(origin, end));
    }
    internal void FitToWorkArea(Rect area)
    {
        // タスクバーを除く画面内に収める。小さな画面でも最小高さで押し出さない。
        double availableWidth = Math.Max(1, area.Width - 24), availableHeight = Math.Max(1, area.Height - 24);
        MinWidth = Math.Min(940, availableWidth); MinHeight = Math.Min(640, availableHeight);
        Width = Math.Min(1150, availableWidth); Height = Math.Min(850, availableHeight);
        double centerX = Owner == null ? area.Left + area.Width / 2 : Owner.Left + Owner.ActualWidth / 2;
        double centerY = Owner == null ? area.Top + area.Height / 2 : Owner.Top + Owner.ActualHeight / 2;
        Left = Math.Clamp(centerX - Width / 2, area.Left + 12, area.Right - Width - 12);
        Top = Math.Clamp(centerY - Height / 2, area.Top + 12, area.Bottom - Height - 12);
    }
    private void LoadPapers()
    {
        PaperBox.Items.Clear();
        PrinterName.Text = printer.PrinterSettings.IsValid ? printer.PrinterSettings.PrinterName : "利用可能なプリンターがありません";
        if (!printer.PrinterSettings.IsValid) return;
        foreach (PaperSize paper in printer.PrinterSettings.PaperSizes) PaperBox.Items.Add(paper);
        PaperBox.DisplayMemberPath = "PaperName";
        Size original = document.SizeMm(currentPage);
        double shortSide = Math.Min(original.Width, original.Height), longSide = Math.Max(original.Width, original.Height);
        PaperBox.SelectedItem = PaperBox.Items.Cast<PaperSize>().FirstOrDefault(p =>
            Math.Abs(Math.Min(p.Width, p.Height) * 25.4 / 100 - shortSide) < 2 &&
            Math.Abs(Math.Max(p.Width, p.Height) * 25.4 / 100 - longSide) < 2)
            ?? PaperBox.Items.Cast<PaperSize>().FirstOrDefault(p => p.Kind == PaperKind.A4) ?? PaperBox.Items.Cast<PaperSize>().FirstOrDefault();
    }
    private void SettingsChanged(object s, RoutedEventArgs e)
    {
        if (!ready) return;
        UpdatePaperControls();
        PosterSettings.Visibility = ModeBox.SelectedIndex == (int)PrintMode.Poster ? Visibility.Visible : Visibility.Collapsed;
        previewReady = false; ++previewVersion; ++settingsVersion; PrintButton.IsEnabled = false;
        Warning.Text = "プレビューを更新しています…";
        previewTimer.Stop(); previewTimer.Start();
    }
    private bool MatchOriginal => MatchOriginalBox.IsChecked == true && ModeBox.SelectedIndex is 0 or 1;
    private void UpdatePaperControls()
    {
        MatchOriginalBox.IsEnabled = ModeBox.SelectedIndex is 0 or 1;
        FixedPaperSettings.Visibility = MatchOriginal ? Visibility.Collapsed : Visibility.Visible;
    }
    private void PrinterSelected(object s, SelectionChangedEventArgs e)
    {
        if (!ready || PrinterBox.SelectedItem is not string name) return;
        try
        {
            printer.PrinterSettings = new PrinterSettings { PrinterName = name };
            printer.DefaultPageSettings = new PageSettings(printer.PrinterSettings) { Margins = new Margins(0, 0, 0, 0) };
            LoadPapers(); SettingsChanged(s, e);
        }
        catch (Exception ex) { Warning.Text = ex.Message; PrintButton.IsEnabled = false; }
    }
    private void PrinterClick(object s, RoutedEventArgs e)
    {
        try
        {
            if (!NativePrinterSettings.Show(this, printer)) return;
            int kind = printer.DefaultPageSettings.PaperSize.RawKind;
            LoadPapers();
            PaperBox.SelectedItem = PaperBox.Items.Cast<PaperSize>().FirstOrDefault(p => p.RawKind == kind) ?? PaperBox.SelectedItem;
            OrientationBox.SelectedIndex = printer.DefaultPageSettings.Landscape ? 1 : 0;
            SettingsChanged(s, e);
        }
        catch (Exception ex) { Warning.Text = ex.Message; PrintButton.IsEnabled = false; }
    }
    private void NumberClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not TextBox box) return;
        box.Focus(); box.SelectAll(); e.Handled = true;
    }
    private void NumberFocused(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) box.SelectAll();
    }
    private void StepNumber(TextBox box, int step)
    {
        bool copies = box == CopiesBox;
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value)) value = copies ? 1 : 100;
        if (copies) value = Math.Round(value);
        box.Text = Math.Clamp(value + step, 1, copies ? 999 : 1000).ToString("0.##", CultureInfo.InvariantCulture);
        box.SelectAll();
    }
    private void NumberStepClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        string[] parts = tag.Split(':');
        StepNumber(parts[0] == "CopiesBox" ? CopiesBox : PercentBox, int.Parse(parts[1], CultureInfo.InvariantCulture));
    }
    private void NumberKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is TextBox box && (e.Key == System.Windows.Input.Key.Up || e.Key == System.Windows.Input.Key.Down))
        { StepNumber(box, e.Key == System.Windows.Input.Key.Up ? 1 : -1); e.Handled = true; }
    }
    private static double Number(TextBox box, string label)
    {
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || !double.IsFinite(n)) throw new ArgumentException($"{label}を数値で入力してください。");
        return n;
    }
    private void RefreshClick(object s, RoutedEventArgs e) => Refresh();
    private async void Refresh() => await RefreshAsync();
    internal async Task RefreshAsync()
    {
        previewTimer.Stop();
        int settings = ++settingsVersion;
        PrintButton.IsEnabled = false;
        try
        {
            if (!printer.PrinterSettings.IsValid || !MatchOriginal && PaperBox.SelectedItem is not PaperSize) throw new InvalidOperationException("プリンターと用紙を選んでください。");
            activeMode = (PrintMode)ModeBox.SelectedIndex;
            double percent = Number(PercentBox, "印刷倍率");
            if (!short.TryParse(CopiesBox.Text, out short copies) || copies < 1 || copies > 999) throw new ArgumentException("部数は1〜999で指定してください。");
            if (DuplexBox.SelectedIndex != 0 && !printer.PrinterSettings.CanDuplex) throw new ArgumentException("選んだプリンターは両面印刷に対応していません。");
            if (activeMode == PrintMode.Booklet && (OrientationBox.SelectedIndex != 1 || DuplexBox.SelectedIndex != 2)) throw new ArgumentException("小冊子は「横」「両面・短辺とじ」を指定してください。");
            if (activeMode == PrintMode.Poster && DuplexBox.SelectedIndex != 0) throw new ArgumentException("ポスターは片面を指定してください。");
            if (PaperBox.SelectedItem is PaperSize paper) printer.DefaultPageSettings.PaperSize = paper;
            printer.DefaultPageSettings.Landscape = OrientationBox.SelectedIndex == 1;
            printer.PrinterSettings.Copies = copies; printer.PrinterSettings.Collate = true;
            printer.PrinterSettings.Duplex = DuplexBox.SelectedIndex switch { 1 => Duplex.Vertical, 2 => Duplex.Horizontal, _ => Duplex.Simplex };
            activeRegion = RegionBox.IsChecked == true ? selectedRegion : null;
            int[] pages = activeRegion.HasValue ? [currentPage] : PrintLayout.ParsePages(RangeBox.Text, document.Count);
            var options = new PrintOptions(activeMode, percent, activeMode == PrintMode.Poster ? Number(OverlapBox, "貼り合わせ幅") : 5, RightBindingBox.IsChecked == true);
            printPlan = PrintPaperPlan.Build(printer, document.SizeMm, pages, options, MatchOriginal, activeRegion);
            sheets = printPlan.Select(s => s.Layout).ToList();
            document.PrintPercent = percent;
            document.LastPrintMode = activeMode;
            side = 0;
            bool clipped = activeMode != PrintMode.Poster && sheets.SelectMany(x => x.Items).Any(p => PrintLayout.IsClipped(p, document.SizeMm(p.Page), activeRegion));
            int paperCount = DuplexBox.SelectedIndex == 0 ? sheets.Count : (sheets.Count + 1) / 2;
            string paperSummary = string.Join(" / ", printPlan.GroupBy(PrintPaperPlan.PaperLabel).Select(g => $"{g.Key} {g.Count()}面"));
            int blanks = sheets.Count(s => s.Items.Count == 0);
            Summary.Text = $"{pages.Length}ページ → {sheets.Count}面 / {paperCount * copies}枚（{copies}部）\n{(MatchOriginal ? "原本に合わせる" : "指定用紙")}：{paperSummary}"
                + (blanks > 0 ? $"\n用紙・向きが変わる箇所に空白の裏面を{blanks}面追加" : "");
            Warning.Text = clipped ? "用紙の印刷可能範囲を超える部分が欠けます。指定倍率を維持し、自動縮小しません。赤線は印刷可能範囲です。" : "赤線は印刷可能範囲です（紙には印刷されません）。";
            await ShowSide();
            if (settings != settingsVersion) return;
            PrintButton.IsEnabled = document.CanPrint;
            if (!document.CanPrint) Warning.Text = "このPDFは高品質の印刷が制限されています。";
        }
        catch (Exception ex) { if (settings != settingsVersion || !ready) return; Warning.Text = ex.Message; sheets = []; printPlan = []; Summary.Text = ""; Preview.Children.Clear(); }
    }
    private async Task ShowSide()
    {
        if (sheets.Count == 0) return;
        previewReady = false;
        int version = ++previewVersion;
        Sheet sheet = sheets[side];
        const double display = 1.8;
        Preview.Width = sheet.Paper.Width * display; Preview.Height = sheet.Paper.Height * display;
        Preview.Children.Clear();
        foreach (Placement item in sheet.Items)
        {
            // 画面プレビューだけは軽量な画像。実印刷はPDFiumからプリンターへ直接描画する。
            double ratio = Math.Min(1, 1800 / Math.Max(item.Destination.Width * display, item.Destination.Height * display));
            BitmapSource bitmap = await Task.Run(() => document.Render(item.Page, Math.Max(1, (int)(item.Destination.Width * display * ratio)), Math.Max(1, (int)(item.Destination.Height * display * ratio))));
            if (version != previewVersion) return;
            var image = new Image { Source = bitmap, Width = item.Destination.Width * display, Height = item.Destination.Height * display, Stretch = Stretch.Fill,
                Clip = new RectangleGeometry(new Rect((item.Clip.X - item.Destination.X) * display, (item.Clip.Y - item.Destination.Y) * display, item.Clip.Width * display, item.Clip.Height * display)) };
            Canvas.SetLeft(image, item.Destination.X * display); Canvas.SetTop(image, item.Destination.Y * display); Preview.Children.Add(image);
        }
        var outline = new System.Windows.Shapes.Rectangle { Width = sheet.Printable.Width * display, Height = sheet.Printable.Height * display, Stroke = Brushes.IndianRed, StrokeThickness = 1, StrokeDashArray = [4, 3] };
        Canvas.SetLeft(outline, sheet.Printable.X * display); Canvas.SetTop(outline, sheet.Printable.Y * display); Preview.Children.Add(outline);
        previewReady = true;
        SideLabel.Text = $"{side + 1} / {sheets.Count}  ·  {sheet.Label}  ·  {PrintPaperPlan.PaperLabel(printPlan[side])}（{sheet.Paper.Width:0.#} × {sheet.Paper.Height:0.#} mm）  ·  " + string.Join(" / ", sheet.Items.Select(p => $"{p.Scale * 100:0.##}%"));
    }
    private async void PreviewWheel(object s, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Delta == 0) return;
        if (!previewReady) { e.Handled = true; return; }
        bool forward = e.Delta < 0;
        bool atEdge = forward ? PreviewScroller.VerticalOffset >= PreviewScroller.ScrollableHeight - .5 : PreviewScroller.VerticalOffset <= .5;
        if (!atEdge) return;
        e.Handled = true;
        int target = side + (forward ? 1 : -1);
        if (target < 0 || target >= sheets.Count) return;
        side = target;
        try
        {
            await ShowSide();
            if (!previewReady || !ready) return;
            PreviewScroller.UpdateLayout();
            if (forward) PreviewScroller.ScrollToTop(); else PreviewScroller.ScrollToBottom();
        }
        catch (Exception ex) { if (ready) Warning.Text = ex.Message; }
    }
    private async void PreviousClick(object s, RoutedEventArgs e) { if (side > 0) { side--; try { await ShowSide(); } catch (Exception ex) { Warning.Text = ex.Message; } } }
    private async void NextClick(object s, RoutedEventArgs e) { if (side + 1 < sheets.Count) { side++; try { await ShowSide(); } catch (Exception ex) { Warning.Text = ex.Message; } } }
    private void CanPrintCommand(object sender, System.Windows.Input.CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = ready && PrintButton.IsEnabled && sheets.Count > 0;
        e.Handled = true;
    }
    private void PrintShortcutKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.IsRepeat && e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            e.Handled = true;
    }
    private void PrintNowClick(object s, RoutedEventArgs e)
    {
        if (!PrintButton.IsEnabled || sheets.Count == 0) return;
        printer.DocumentName = System.IO.Path.GetFileName(document.Path);
        bool sent = false;
        try { sent = PrintPaperPlan.Print(document, printer, printPlan); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "印刷できませんでした"); }
        // 取消や送信エラーでは設定を残し、送信処理が終わってから閉じる。
        if (sent) Close();
    }
}

public static class PrinterOutput
{
    [DllImport("gdi32.dll")] private static extern int SaveDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool RestoreDC(IntPtr dc, int saved);
    [DllImport("gdi32.dll")] private static extern int IntersectClipRect(IntPtr dc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);

    public static (Size Paper, Rect Printable) GetGeometry(PrintDocument printer, PageSettings? settings = null)
    {
        // PrintableAreaはこの環境の横向き設定で縦の寸法を返したため、
        // 印刷時と同じPageSettingsのDCから物理寸法と余白を取得する。
        using var graphics = printer.PrinterSettings.CreateMeasurementGraphics(settings ?? printer.DefaultPageSettings);
        IntPtr dc = graphics.GetHdc();
        try
        {
            double dx = GetDeviceCaps(dc, 88) / 25.4, dy = GetDeviceCaps(dc, 90) / 25.4;
            if (dx <= 0 || dy <= 0) throw new IOException("プリンターの解像度を取得できません。");
            var paper = new Size(GetDeviceCaps(dc, 110) / dx, GetDeviceCaps(dc, 111) / dy);
            var printable = new Rect(GetDeviceCaps(dc, 112) / dx, GetDeviceCaps(dc, 113) / dy, GetDeviceCaps(dc, 8) / dx, GetDeviceCaps(dc, 10) / dy);
            if (paper.Width <= 0 || paper.Height <= 0 || printable.Width <= 0 || printable.Height <= 0) throw new IOException("プリンターの用紙寸法を取得できません。");
            return (paper, Rect.Intersect(new Rect(paper), printable));
        }
        finally { graphics.ReleaseHdc(dc); }
    }
    public static void Draw(PdfDocument document, Sheet sheet, PrintPageEventArgs args)
    {
        var graphics = args.Graphics ?? throw new IOException("プリンターの描画領域を取得できません。");
        IntPtr dc = graphics.GetHdc();
        try
        {
            double px = GetDeviceCaps(dc, 88) / 25.4, py = GetDeviceCaps(dc, 90) / 25.4;
            int offsetX = GetDeviceCaps(dc, 112), offsetY = GetDeviceCaps(dc, 113);
            if (px <= 0 || py <= 0) throw new IOException("プリンターの解像度を取得できません。");
            int X(double mm) => (int)Math.Round(mm * px, MidpointRounding.AwayFromZero) - offsetX;
            int Y(double mm) => (int)Math.Round(mm * py, MidpointRounding.AwayFromZero) - offsetY;
            foreach (Placement item in sheet.Items)
            {
                int saved = SaveDC(dc);
                try
                {
                    IntersectClipRect(dc, X(item.Clip.Left), Y(item.Clip.Top), X(item.Clip.Right), Y(item.Clip.Bottom));
                    document.DrawToPrinter(dc, item.Page, X(item.Destination.X), Y(item.Destination.Y),
                        (int)Math.Round(item.Destination.Width * px, MidpointRounding.AwayFromZero), (int)Math.Round(item.Destination.Height * py, MidpointRounding.AwayFromZero));
                }
                finally { RestoreDC(dc, saved); }
            }
        }
        finally { graphics.ReleaseHdc(dc); }
    }
}
