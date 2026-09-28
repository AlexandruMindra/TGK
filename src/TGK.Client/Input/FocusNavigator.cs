using System.Collections.Generic;
using Blossom.Core.Visual;

namespace TGK.Client.Input;

/// <summary>Tab / Shift+Tab focus traversal over the <see cref="IFocusable"/> elements of a subtree, in tree order.</summary>
public static class FocusNavigator
{
    /// <summary>Moves focus to the next (or previous) tab stop inside <paramref name="scope"/>; returns whether focus moved.</summary>
    public static bool Move(VisualElement scope, bool backwards)
    {
        var stops = new List<VisualElement>();
        Collect(scope, stops);
        if (stops.Count == 0 || scope.ParentView is not { } view)
            return false;

        int current = view.ActiveKeyboardElement is { } focused ? stops.IndexOf(focused) : -1;
        int next = current < 0
            ? (backwards ? stops.Count - 1 : 0)
            : (current + (backwards ? stops.Count - 1 : 1)) % stops.Count;
        VisualElement target = stops[next];
        view.SetActiveKeyboardElement(target);
        ((IFocusable)target).OnTabFocus();
        return true;
    }

    /// <summary>Focuses the first tab stop inside <paramref name="scope"/>.</summary>
    public static bool FocusFirst(VisualElement scope)
    {
        var stops = new List<VisualElement>();
        Collect(scope, stops);
        if (stops.Count == 0 || scope.ParentView is not { } view)
            return false;
        view.SetActiveKeyboardElement(stops[0]);
        return true;
    }

    private static void Collect(VisualElement element, List<VisualElement> stops)
    {
        if (!element.Visible || !element.Interactive)
            return;
        if (element is IFocusable { IsTabStop: true } && element.ReceivesKeyboard)
            stops.Add(element);
        foreach (VisualElement child in element.Children)
            Collect(child, stops);
    }
}
