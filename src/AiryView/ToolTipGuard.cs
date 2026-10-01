using System.Runtime.CompilerServices;

namespace AiryView;

internal sealed class ToolTipGuard
{
    private static readonly ConditionalWeakTable<Window, ToolTipGuard> Guards = new();
    private readonly Window owner;
    private int modalDepth;
    internal bool CanShow => modalDepth == 0 && owner.IsActive && owner.IsEnabled;

    static ToolTipGuard()
    {
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler((sender, _) =>
        {
            var tip = (ToolTip)sender;
            if (tip.PlacementTarget is { } target && Window.GetWindow(target) is { } window &&
                Guards.TryGetValue(window, out var guard) && !guard.CanShow) tip.IsOpen = false;
        }));
    }

    internal ToolTipGuard(Window owner)
    {
        this.owner = owner;
        Guards.Add(owner, this);
        owner.AddHandler(ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler((_, e) =>
        {
            if (!CanShow) e.Handled = true;
        }), true);
        owner.Deactivated += (_, _) => CloseVisibleToolTips();
    }

    internal T RunModal<T>(Func<T> showDialog)
    {
        // ネイティブダイアログのメッセージループ中も、遅れて届く表示要求を抑止する。
        ++modalDepth;
        try { CloseVisibleToolTips(); return showDialog(); }
        finally { --modalDepth; }
    }

    private void CloseVisibleToolTips()
    {
        foreach (var source in PresentationSource.CurrentSources.Cast<PresentationSource>().ToArray())
            if (source.RootVisual is { } root)
                foreach (var tip in FindToolTips(root).ToArray())
                    if (tip.PlacementTarget is { } target && Window.GetWindow(target) == owner) tip.IsOpen = false;
    }

    private static IEnumerable<ToolTip> FindToolTips(DependencyObject root)
    {
        if (root is ToolTip tip) yield return tip;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); ++i)
            foreach (var child in FindToolTips(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
