using System.Windows.Threading;

namespace AiryView;

public partial class MainWindow
{
    private readonly DispatcherTimer tokenTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool countingTokens;
    private void InitializeTokenCounter()
    {
        TextSelectionInfo.TextWrapping = TextWrapping.Wrap;
        SizeChanged += (_, _) => TextSelectionBadge.MaxWidth = Math.Max(100, ActualWidth - 40);
        tokenTimer.Tick += async (_, _) => { tokenTimer.Stop(); await RefreshTokenCountsAsync(); };
        Closed += (_, _) => tokenTimer.Stop();
    }
    private void UpdateTokenStatus(TextTabState state)
    {
        string selection = state.ShowEditor && TextEditor.SelectionLength > 0 ? TextEditor.SelectedText : "";
        string total = state.TokenErrorText == state.LiveText ? "計数できません" : state.TokenText == state.LiveText && state.TokenCount is { } count ? count.ToString("N0") : "計数中…";
        string selected = selection.Length > 0 ? $"選択 {(state.TokenSelection == selection && state.SelectedTokenCount is { } tokens ? tokens.ToString("N0") : "計数中…")} / 全体 " : "";
        TextSelectionInfo.Text += $"  •  トークン数 {selected}{total}（{DocumentTokenCounter.Basis}）";
        TextSelectionBadge.ToolTip = DocumentTokenCounter.Explanation;
        if (state.TokenErrorText != state.LiveText && (state.TokenText != state.LiveText || selection.Length > 0 && state.TokenSelection != selection))
        {
            tokenTimer.Stop(); tokenTimer.Start();
        }
    }
    private async Task RefreshTokenCountsAsync()
    {
        if (windowClosed || countingTokens || CurrentText is not { } state) return;
        countingTokens = true;
        string text = state.LiveText;
        string selection = state.ShowEditor && TextEditor.SelectionLength > 0 ? TextEditor.SelectedText : "";
        try
        {
            int total = state.TokenText == text && state.TokenCount is { } cached ? cached : await DocumentTokenCounter.CountAsync(text);
            int selected = selection.Length == 0 ? 0 : await DocumentTokenCounter.CountAsync(selection);
            if (!windowClosed && state.LiveText == text)
            {
                state.TokenText = text; state.TokenCount = total;
                state.TokenSelection = selection; state.SelectedTokenCount = selected; state.TokenErrorText = null;
            }
        }
        catch (Exception) { state.TokenErrorText = text; }
        finally { countingTokens = false; }
        if (windowClosed) return;
        if (CurrentText?.ShowEditor == true) UpdateTextSmartStatus();
        else if (CurrentText?.IsMarkdown == true) UpdateMarkdownPreviewStatus();
    }
}
