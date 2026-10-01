namespace AiryView;

internal static class TextFileFormats
{
    internal static readonly string[] Extensions = [".md", ".markdown", ".txt", ".json", ".csv"];
    internal static bool Supports(string path) => Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    internal static bool IsMarkdown(string path) => new[] { ".md", ".markdown" }.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    internal const string OpenFilter = "対応ファイル|*.pdf;*.md;*.markdown;*.txt;*.json;*.csv;*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp;*.gif;*.ico;*.webp;*.svg|PDF|*.pdf|文章・データ|*.md;*.markdown;*.txt;*.json;*.csv|JSON|*.json|CSV|*.csv|画像|*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp;*.gif;*.ico;*.webp;*.svg";

    internal static (string Filter, string Extension) SaveFormat(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md" or ".markdown" => ("Markdown|*.md;*.markdown", ".md"),
        ".json" => ("JSONファイル|*.json", ".json"),
        ".csv" => ("CSVファイル|*.csv", ".csv"),
        _ => ("テキストファイル|*.txt", ".txt")
    };
}
