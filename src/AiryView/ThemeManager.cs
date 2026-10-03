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
            SetBrush(res, "AirySurfaceSecondary", Color.FromRgb(30, 41, 59));    // #1E293B
            SetBrush(res, "AiryCanvas", Color.FromRgb(57, 53, 53)); // #393535
            SetBrush(res, "AiryInk", Color.FromRgb(248, 250, 252));              // #F8FAFC
            SetBrush(res, "AiryInkSecondary", Color.FromRgb(148, 163, 184));     // #94A3B8
            SetBrush(res, "AiryStroke", Color.FromRgb(51, 65, 85));              // #334155
            SetBrush(res, "AiryStrokeHover", Color.FromRgb(56, 189, 248));       // #38BDF8
            SetBrush(res, "AiryAccent", Color.FromRgb(56, 189, 248));            // #38BDF8
            SetBrush(res, "AiryButtonBackground", Color.FromRgb(30, 41, 59));    // #1E293B
            SetBrush(res, "AiryButtonBackgroundHover", Color.FromRgb(51, 65, 85));// #334155
            SetBrush(res, "AiryTabInactiveBackground", Color.FromRgb(20, 30, 48));// #141E30
            SetBrush(res, "AiryTabInactiveBorder", Color.FromRgb(41, 53, 72));   // #293548
            SetBrush(res, "AiryTabActiveBackground", Color.FromRgb(30, 41, 59)); // #1E293B
            SetBrush(res, "AiryTabActiveBorder", Color.FromRgb(56, 189, 248));   // #38BDF8
            SetBrush(res, "AiryPopupBackground", Color.FromRgb(24, 33, 47));     // #18212F
            SetBrush(res, "AiryIconStroke", Color.FromRgb(226, 232, 240));       // #E2E8F0
            SetBrush(res, "AiryEditorBackground", Color.FromRgb(57, 53, 53)); // #393535
            SetBrush(res, "AiryEditorForeground", Color.FromRgb(241, 245, 249)); // #F1F5F9
            SetBrush(res, "AiryEditCanvas", Color.FromRgb(57, 53, 53)); // #393535

            var titleGradient = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromRgb(10, 15, 29), 0),
                new(Color.FromRgb(22, 32, 53), .50),
                new(Color.FromRgb(15, 23, 42), 1)
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
