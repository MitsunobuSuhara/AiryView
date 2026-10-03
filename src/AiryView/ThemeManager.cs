using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace AiryView;

public enum AppTheme
{
    Classic,
    Modern
}

public static class ThemeManager
{
    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Modern;
    public static event Action<AppTheme>? ThemeChanged;

    public static void Apply(AppTheme theme, Window? window = null)
    {
        CurrentTheme = theme;
        var res = Application.Current?.Resources;
        if (res == null) return;

        if (theme == AppTheme.Modern)
        {
            SetBrush(res, "AirySurface", Color.FromRgb(57, 53, 53)); // #393535
            SetBrush(res, "AirySurfaceSecondary", Color.FromRgb(67, 63, 63));
            SetBrush(res, "AiryCanvas", Color.FromRgb(57, 53, 53)); // #393535
            SetBrush(res, "AiryInk", Color.FromRgb(242, 239, 235));
            SetBrush(res, "AiryInkSecondary", Color.FromRgb(188, 182, 177));
            SetBrush(res, "AiryStroke", Color.FromRgb(94, 87, 85));
            SetBrush(res, "AiryStrokeHover", Color.FromRgb(151, 169, 156));
            SetBrush(res, "AiryAccent", Color.FromRgb(166, 182, 170));
            SetBrush(res, "AiryButtonBackground", Color.FromRgb(67, 63, 63));
            SetBrush(res, "AiryButtonBackgroundHover", Color.FromRgb(82, 76, 73));
            SetBrush(res, "AiryTabInactiveBackground", Color.FromRgb(52, 48, 48));
            SetBrush(res, "AiryTabInactiveBorder", Color.FromRgb(84, 78, 76));
            SetBrush(res, "AiryTabActiveBackground", Color.FromRgb(73, 68, 65));
            SetBrush(res, "AiryTabActiveBorder", Color.FromRgb(151, 169, 156));
            SetBrush(res, "AiryPopupBackground", Color.FromRgb(63, 59, 59));
            SetBrush(res, "AiryIconStroke", Color.FromRgb(224, 218, 211));
            SetBrush(res, "AiryEditorBackground", Color.FromRgb(57, 53, 53)); // #393535
            SetBrush(res, "AiryEditorForeground", Color.FromRgb(242, 239, 235));
            SetBrush(res, "AiryEditCanvas", Color.FromRgb(57, 53, 53)); // #393535

            SetBrush(res, "AiryEditorSelection", Color.FromRgb(89, 111, 101));
            res["AiryEditorSelectionOpacity"] = .85;

            var titleGradient = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromRgb(44, 41, 41), 0),
                new(Color.FromRgb(52, 48, 48), .50),
                new(Color.FromRgb(44, 41, 41), 1)
            }, new Point(0, 0), new Point(1, 0));
            titleGradient.Freeze();
            res["AiryTitleBarBackground"] = titleGradient;
        }
        else
        {
            SetBrush(res, "AirySurface", Color.FromRgb(247, 249, 252));          // #F7F9FC
            SetBrush(res, "AirySurfaceSecondary", Color.FromRgb(255, 255, 255)); // #FFFFFF
            SetBrush(res, "AiryCanvas", Color.FromRgb(232, 237, 244));           // #E8EDF4
            SetBrush(res, "AiryInk", Color.FromRgb(36, 50, 68));                 // #243244
            SetBrush(res, "AiryInkSecondary", Color.FromRgb(82, 99, 122));       // #52637A
            SetBrush(res, "AiryStroke", Color.FromRgb(215, 224, 234));           // #D7E0EA
            SetBrush(res, "AiryStrokeHover", Color.FromRgb(158, 181, 210));      // #9EB5D2
            SetBrush(res, "AiryAccent", Color.FromRgb(71, 121, 189));            // #4779BD
            SetBrush(res, "AiryButtonBackground", Color.FromRgb(255, 255, 255)); // #FFFFFF
            SetBrush(res, "AiryButtonBackgroundHover", Color.FromRgb(247, 249, 252));
            SetBrush(res, "AiryTabInactiveBackground", Color.FromRgb(237, 241, 246)); // #EDF1F6
            SetBrush(res, "AiryTabInactiveBorder", Color.FromRgb(223, 230, 238));     // #DFE6EE
            SetBrush(res, "AiryTabActiveBackground", Color.FromRgb(255, 255, 255));   // #FFFFFF
            SetBrush(res, "AiryTabActiveBorder", Color.FromRgb(185, 203, 225));       // #B9CBE1
            SetBrush(res, "AiryPopupBackground", Color.FromRgb(255, 255, 255));
            SetBrush(res, "AiryIconStroke", Color.FromRgb(25, 36, 50));          // #192432
            SetBrush(res, "AiryEditorBackground", Color.FromRgb(255, 255, 255));
            SetBrush(res, "AiryEditorForeground", Color.FromRgb(36, 50, 68));

            SetBrush(res, "AiryEditorSelection", Color.FromRgb(191, 219, 254));
            res["AiryEditorSelectionOpacity"] = .6;

            var titleGradient = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromRgb(20, 47, 64), 0),
                new(Color.FromRgb(20, 80, 106), .48),
                new(Color.FromRgb(12, 52, 76), 1)
            }, new Point(0, 0), new Point(1, 0));
            titleGradient.Freeze();
            res["AiryTitleBarBackground"] = titleGradient;
        }

        if (window != null)
        {
            UpdateWindowDarkAttribute(window, theme == AppTheme.Modern);
        }

        ThemeChanged?.Invoke(theme);
    }

    private static void SetBrush(ResourceDictionary res, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        res[key] = brush;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void UpdateWindowDarkAttribute(Window window, bool isDark)
    {
        try
        {
            int enabled = isDark ? 1 : 0;
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero)
            {
                if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                    DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
            }
        }
        catch { }
    }

    public static void Toggle(Window? window = null)
    {
        var next = CurrentTheme == AppTheme.Modern ? AppTheme.Classic : AppTheme.Modern;
        Apply(next, window);
        if (window != null) WindowPreferences.Save(window);
    }
}
