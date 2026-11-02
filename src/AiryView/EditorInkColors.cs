namespace AiryView;

// 上部ツールバーと編集枠横のパレットで、色と並びを共有する。
internal static class EditorInkColors
{
    internal static readonly (string Name, Color Color)[] Choices =
    [
        ("赤", Colors.Red), ("黒", Colors.Black), ("青", Colors.RoyalBlue), ("緑", Colors.ForestGreen),
        ("橙", Colors.Orange), ("白", Colors.White), ("黄", Colors.Yellow), ("ピンク", Colors.HotPink),
        ("水色", Colors.DeepSkyBlue), ("紫", Colors.MediumPurple), ("濃い灰", Colors.DimGray), ("灰", Colors.Gray),
        ("濃い赤", Colors.DarkRed), ("黄緑", Colors.YellowGreen), ("紺", Colors.Navy),
        ("青緑", Color.FromRgb(20, 80, 106)), ("茶", Colors.SaddleBrown), ("金", Colors.Gold),
        ("薄い灰", Colors.LightGray), ("ベージュ", Colors.Beige)
    ];

    internal static Color At(int index) => Choices[Math.Clamp(index, 0, Choices.Length - 1)].Color;
    internal static int IndexOf(Color color) => Math.Max(0, Array.FindIndex(Choices, choice => choice.Color == color));
}
