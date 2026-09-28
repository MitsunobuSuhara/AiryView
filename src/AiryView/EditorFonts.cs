namespace AiryView;

internal sealed record EditorFont(string Id, string Label, string? Folder = null)
{
    public override string ToString() => Label;
}

// 編集画面以外から参照しない。実際のフォント読込は選択・描画時だけ行う。
internal static class EditorFonts
{
    internal static readonly EditorFont[] Choices =
    [
        new("MS Gothic", "ＭＳ ゴシック"), new("Yu Gothic", "游ゴシック"), new("Yu Mincho", "游明朝"), new("Meiryo", "メイリオ"),
        new("noto", "Noto Sans JP", "notosansjp"), new("mplus", "M PLUS 2", "mplus2"),
        new("shippori", "しっぽり明朝", "shipporimincho"), new("line", "LINE Seed JP", "lineseedjp"),
        new("ibm", "IBM Plex Sans JP", "ibmplexsansjp"), new("zennew", "ZEN角ゴシック New", "zenkakugothicnew"),
        new("zenantique", "ZEN角ゴシック Antique", "zenkakugothicantique")
    ];
    private static readonly Dictionary<string, FontFamily> loaded = new();
    internal static FontFamily Family(string id)
    {
        if (loaded.TryGetValue(id, out var family)) return family;
        var choice = Choices.FirstOrDefault(item => item.Id == id) ?? Choices[0];
        if (choice.Folder == null) family = new FontFamily(choice.Id);
        else
        {
            string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "Fonts", choice.Folder) + System.IO.Path.DirectorySeparatorChar;
            family = Fonts.GetFontFamilies(new Uri(folder)).FirstOrDefault()
                ?? throw new IOException("フォントがありません。AiryViewを更新してください：" + choice.Label);
        }
        loaded[id] = family; return family;
    }
    internal static Typeface Typeface(string id, bool bold) => new(Family(id), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
}
