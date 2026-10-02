using Microsoft.ML.Tokenizers;
using System.Threading;

namespace AiryView;

internal static class DocumentTokenCounter
{
    internal const string Basis = "OpenAI・o200k_base基準";
    internal const string Explanation = "文書の文字列のみを計数。モデルによって異なり、会話全体やAPIの消費トークン数とは別。\nMarkdownは記号を含むソース全体を数えます。文章は外部へ送信しません。";
    private static readonly Lazy<TiktokenTokenizer> Tokenizer = new(() => TiktokenTokenizer.CreateForEncoding("o200k_base", new Dictionary<string, int>()));
    private static readonly SemaphoreSlim Gate = new(1);
    internal static int Count(string text) => text.Length == 0 ? 0 : Tokenizer.Value.CountTokens(text);
    internal static async Task<int> CountAsync(string text)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => Count(text)).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }
}
