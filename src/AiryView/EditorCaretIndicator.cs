using System.Windows.Documents;

namespace AiryView;

// 写真の明暗や文字色によらず入力位置を見つけられる、点滅しない補助カーソル。
internal sealed class EditorCaretIndicator : FrameworkElement
{
    private readonly RichTextBox input;
    private readonly Func<double> zoom;
    private Rect caret = Rect.Empty;
    internal Rect CaretForTest => caret;

    internal EditorCaretIndicator(RichTextBox input, Func<double> zoom)
    {
        this.input = input; this.zoom = zoom;
        IsHitTestVisible = false; ClipToBounds = true;
        input.LayoutUpdated += (_, _) => Refresh();
        input.SelectionChanged += (_, _) => Refresh();
        input.IsKeyboardFocusWithinChanged += (_, _) => Refresh();
        input.Unloaded += (_, _) => { caret = Rect.Empty; InvalidateVisual(); };
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
        caret = next; InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (caret.IsEmpty) return;
        Point bottom = new(caret.X, caret.Bottom);
        dc.DrawLine(new Pen(Brushes.White, 3.5 / zoom()), caret.TopLeft, bottom);
        dc.DrawLine(new Pen(Brushes.Black, 1.5 / zoom()), caret.TopLeft, bottom);
    }
}
