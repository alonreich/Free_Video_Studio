using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;

namespace FreeVideoStudio.App.Controls;

public partial class SettingsWindow
{
    private readonly Dictionary<Control, Control> _helpOwner = new();
    private Control? _openHelp;

    private static bool IsInput(Control c) => c is TextBox or ComboBox or Slider or Button;

    private static string? TipOf(Control c) => ToolTip.GetTip(c) as string;

    private static IEnumerable<Control> InputsIn(Control root)
    {
        if (IsInput(root)) yield return root;
        foreach (var d in root.GetLogicalDescendants().OfType<Control>())
            if (IsInput(d)) yield return d;
    }

    private void BuildHelp()
    {
        _helpOwner.Clear();
        var all = this.GetLogicalDescendants().OfType<Control>().ToList();

        foreach (var row in all.OfType<StackPanel>().Where(p => p.Classes.Contains("LabelRow") && TipOf(p) != null))
        {
            var targets = InputsIn(row).ToList();
            if (targets.Count == 0 && row.Parent is Grid grid)
            {
                int r = Grid.GetRow(row), c = Grid.GetColumn(row);
                targets = grid.Children.OfType<Control>()
                    .Where(x => !ReferenceEquals(x, row) && Grid.GetRow(x) == r && Grid.GetColumn(x) > c)
                    .SelectMany(InputsIn).ToList();
            }
            else if (targets.Count == 0 && row.Parent is Panel panel)
            {
                int i = panel.Children.IndexOf(row);
                if (i >= 0 && i + 1 < panel.Children.Count) targets = InputsIn(panel.Children[i + 1]).ToList();
            }
            foreach (var t in targets) MapHelp(t, row);
        }

        foreach (var input in all.Where(c => IsInput(c) && TipOf(c) != null)) MapHelp(input, input);

        foreach (var holder in all.Where(c => !IsInput(c) && TipOf(c) != null && !c.Classes.Contains("LabelRow")).Reverse())
            foreach (var t in InputsIn(holder)) MapHelp(t, holder);

        foreach (var info in all.OfType<TextBlock>().Where(t => t.Classes.Contains("Info")))
        {
            info.PointerPressed += (_, e) =>
            {
                if (info.Parent is not Control owner || TipOf(owner) == null) return;
                OpenHelp(owner);
                e.Handled = true;
            };
        }

        AddHandler(GotFocusEvent, (_, _) => CloseHelp(), handledEventsToo: true);
    }

    private void MapHelp(Control input, Control owner)
    {
        if (_helpOwner.ContainsKey(input)) return;
        _helpOwner[input] = owner;
        if (string.IsNullOrEmpty(AutomationProperties.GetHelpText(input)))
            AutomationProperties.SetHelpText(input, TipOf(owner));
    }

    private bool ShowHelpForFocused()
    {
        for (var c = FocusManager?.GetFocusedElement() as Control; c != null; c = c.Parent as Control)
        {
            if (_helpOwner.TryGetValue(c, out var owner)) { OpenHelp(owner); return true; }
            if (TipOf(c) != null) { OpenHelp(c); return true; }
        }
        return false;
    }

    private void OpenHelp(Control owner)
    {
        if (ReferenceEquals(_openHelp, owner) && ToolTip.GetIsOpen(owner)) { CloseHelp(); return; }
        CloseHelp();
        _openHelp = owner;
        ToolTip.SetIsOpen(owner, true);
    }

    private void CloseHelp()
    {
        if (_openHelp != null) ToolTip.SetIsOpen(_openHelp, false);
        _openHelp = null;
    }
}
