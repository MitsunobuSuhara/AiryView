using System.Text.Json;
namespace AiryView;
public static class WindowPreferences
{
    private sealed record Preferences(double Width, double Height, bool Maximized, string? Theme = null);
    private static string SettingsPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiryView", "window.json");
    private static string[] LegacySettingsPaths => [System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiryReader", "window.json"), System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "airyPDF", "window.json")];
    
    public static void Restore(Window window)
    {
        try
        {
            var path = File.Exists(SettingsPath) ? SettingsPath : LegacySettingsPaths.FirstOrDefault(File.Exists);
            if (path == null || !File.Exists(path))
            {
                if (!MainWindow.SuppressRecentFilesForTest)
                {
                    ThemeManager.Apply(AppTheme.Modern, window);
                }
                return;
            }
            var saved = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path));
            if (saved == null || !double.IsFinite(saved.Width) || !double.IsFinite(saved.Height)) return;
            window.Width = Math.Clamp(saved.Width, window.MinWidth, Math.Max(window.MinWidth, SystemParameters.WorkArea.Width));
            window.Height = Math.Clamp(saved.Height, window.MinHeight, Math.Max(window.MinHeight, SystemParameters.WorkArea.Height));
            if (saved.Maximized) window.WindowState = WindowState.Maximized;

            if (!MainWindow.SuppressRecentFilesForTest)
            {
                var theme = saved.Theme == nameof(AppTheme.Classic) ? AppTheme.Classic : AppTheme.Modern;
                ThemeManager.Apply(theme, window);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
    
    public static void Save(Window window)
    {
        try
        {
            var bounds = window.WindowState == WindowState.Normal ? new Rect(window.Left, window.Top, window.Width, window.Height) : window.RestoreBounds;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Preferences(
                bounds.Width,
                bounds.Height,
                window.WindowState == WindowState.Maximized,
                ThemeManager.CurrentTheme.ToString()
            )));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
