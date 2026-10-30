using System.Windows.Documents;

namespace AiryView;

// 写真の明暗や文字色によらず入力位置を見つけられる補助カーソル。
internal sealed class EditorCaretIndicator : FrameworkElement
{
    private readonly RichTextBox input;
    private readonly Func<double> zoom;
    private Rect caret = Rect.Empty;
    private readonly System.Windows.Threading.DispatcherTimer blinkTimer;
    private bool visible = true;
    internal Rect CaretForTest => caret;
    internal bool VisibleForTest => !caret.IsEmpty && visible;

    internal EditorCaretIndicator(RichTextBox input, Func<double> zoom)
    {
        this.input = input; this.zoom = zoom;
        blinkTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Render, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(530) };
        blinkTimer.Tick += (_, _) => { visible = !visible; InvalidateVisual(); };
        IsHitTestVisible = false; ClipToBounds = true;
        input.LayoutUpdated += (_, _) => Refresh();
        input.SelectionChanged += (_, _) => { Refresh(); RestartBlink(); };
        input.TextChanged += (_, _) => { Refresh(); RestartBlink(); };
        input.IsKeyboardFocusWithinChanged += (_, _) => Refresh();
        input.Unloaded += (_, _) => { blinkTimer.Stop(); caret = Rect.Empty; InvalidateVisual(); };
    }

    private void RestartBlink()
    {
        blinkTimer.Stop(); visible = true;
        if (!caret.IsEmpty) blinkTimer.Start();
        InvalidateVisual();
    }

    private void Refresh()
    {
        Rect next = Rect.Empty;
        if (input.IsLoaded && input.IsKeyboardFocusWithin && input.Selection.IsEmpty)
        {
            Rect text = input.CaretPosition.GetCharacterRect(LogicalDirection.Forward);
            if (!text.IsEmpty && text.Height > 0)
            {
                Point point = input.TranslatePoint(text.TopLeft, this);
                next = new Rect(point, new Size(0, text.Height));
            }
        }
        if (next == caret) return;
        caret = next; RestartBlink();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (caret.IsEmpty || !visible) return;
        Point bottom = new(caret.X, caret.Bottom);
        dc.DrawLine(new Pen(Brushes.White, 3.5 / zoom()), caret.TopLeft, bottom);
        dc.DrawLine(new Pen(Brushes.Black, 1.5 / zoom()), caret.TopLeft, bottom);
    }
}
