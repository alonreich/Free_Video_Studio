using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace FreeVideoStudio.App.Controls;

public partial class ConfirmDialogWindow : Window
{
    public enum ConfirmDialogResult { Cancelled, Yes, No, Alt }

    public ConfirmDialogResult DialogResult { get; private set; } = ConfirmDialogResult.Cancelled;
    public bool Result => DialogResult == ConfirmDialogResult.Yes;

    public enum SaveChangesChoice { BackToEditing, Save, Discard }

    public static System.Threading.Tasks.Task<SaveChangesChoice> AskSaveChangesAsync(
        Window owner, string profileName, string destination)
        => AskSaveChangesWithMessageAsync(owner, "Save your changes?",
            $"Save your changes to \"{profileName}\" before {destination}?\n\n" +
            "Discard changes will lose your unsaved edits.");

    public static async System.Threading.Tasks.Task<SaveChangesChoice> AskSaveChangesWithMessageAsync(
        Window owner, string title, string message)
    {
        try
        {
            var dlg = new ConfirmDialogWindow();
            dlg.SetTitle(title);
            dlg.SetMessage(message);
            dlg.SetButtonText("Save changes", "Back to editing", "Discard changes");
            if (dlg.YesBtnCtl is { } yesTip) ToolTip.SetTip(yesTip, "Save your work, then continue.");
            if (dlg.NoBtnCtl is { } noTip) ToolTip.SetTip(noTip, "Close this question and keep editing. Nothing is lost.");
            if (dlg.AltBtnCtl is { } altTip) ToolTip.SetTip(altTip, "Throw away the changes you have not saved.");
            dlg.SetButtonClasses("Success", "Secondary", "Danger");
            var save = dlg.FindControl<Button>("YesBtn")!;
            var back = dlg.FindControl<Button>("NoBtn")!;
            save.IsDefault = false;
            back.IsDefault = true;
            dlg.Opened += (_, _) => back.Focus();
            await dlg.ShowDialog(owner);
            return dlg.DialogResult switch
            {
                ConfirmDialogResult.Yes => SaveChangesChoice.Save,
                ConfirmDialogResult.Alt => SaveChangesChoice.Discard,
                _ => SaveChangesChoice.BackToEditing
            };
        }
        catch (System.Exception ex)
        {
            RuntimeLog.Fail("DIALOG", $"Save prompt failed, keeping the edits open: {ex.Message}");
            return SaveChangesChoice.BackToEditing;
        }
    }

    public ConfirmDialogWindow()
    {
        InitializeComponent();
        var yesBtn = YesBtnCtl;
        var noBtn = NoBtnCtl;
        var altBtn = AltBtnCtl;
        if (yesBtn != null) yesBtn.Click += (s, e) => { DialogResult = ConfirmDialogResult.Yes; Close(); };
        if (noBtn != null) noBtn.Click += (s, e) => { DialogResult = ConfirmDialogResult.No; Close(); };
        if (altBtn != null) altBtn.Click += (s, e) => { DialogResult = ConfirmDialogResult.Alt; Close(); };
    }

    public void SetMessage(string message)
    {
        var msg = this.FindControl<TextBlock>("MessageText");
        if (msg != null) msg.Text = message;
    }

    public void SetTitle(string title)
    {
        var titleTb = this.FindControl<TextBlock>("DialogTitle");
        if (titleTb != null) titleTb.Text = title;
        this.Title = title;
    }

    public void UseDestructiveStyling()
    {
        var yesBtn = YesBtnCtl;
        var noBtn = NoBtnCtl;

        if (yesBtn != null)
        {
            yesBtn.Classes.Remove("Success");
            yesBtn.Classes.Add("Danger");
            yesBtn.IsDefault = false;
        }
        if (noBtn != null)
        {
            noBtn.Classes.Remove("Secondary");
            noBtn.Classes.Add("Success");
            noBtn.IsDefault = true;
        }
    }

    public void UseEditOrRemoveStyling()
    {
        var yesBtn = YesBtnCtl;
        var noBtn = NoBtnCtl;
        var altBtn = AltBtnCtl;

        if (yesBtn != null)
        {
            yesBtn.Classes.Remove("Success");
            yesBtn.Classes.Remove("Danger");
            yesBtn.Classes.Add("Primary");
            yesBtn.IsDefault = false;
        }
        if (noBtn != null)
        {
            noBtn.Classes.Remove("Success");
            noBtn.Classes.Add("Secondary");
            noBtn.IsDefault = true;
        }
        if (altBtn != null)
        {
            altBtn.Classes.Remove("Primary");
            altBtn.Classes.Remove("Success");
            altBtn.Classes.Add("Danger");
        }
    }

    public void SetButtonClasses(string? yesClass, string? noClass, string? altClass)
    {
        Repaint(YesBtnCtl, yesClass);
        Repaint(NoBtnCtl, noClass);
        Repaint(AltBtnCtl, altClass);

        static void Repaint(Button? btn, string? cls)
        {
            if (btn == null || string.IsNullOrWhiteSpace(cls)) return;
            btn.Classes.Remove("Primary");
            btn.Classes.Remove("Secondary");
            btn.Classes.Remove("Success");
            btn.Classes.Remove("Danger");
            btn.Classes.Add(cls);
        }
    }

    public enum EditOrRemoveChoice
    {
        Cancelled,
        Edit,
        Remove
    }

    public static async System.Threading.Tasks.Task<EditOrRemoveChoice> AskEditOrRemoveAsync(
        Window owner,
        string title,
        string message,
        string editText = "EDIT",
        string cancelText = "CANCEL",
        string removeText = "REMOVE")
    {
        try
        {
            var dlg = new ConfirmDialogWindow();
            dlg.SetTitle(title);
            dlg.SetMessage(message);
            dlg.SetButtonText(editText, cancelText, removeText);
            dlg.UseEditOrRemoveStyling();
            await dlg.ShowDialog(owner);

            return dlg.DialogResult switch
            {
                ConfirmDialogResult.Yes => EditOrRemoveChoice.Edit,
                ConfirmDialogResult.Alt => EditOrRemoveChoice.Remove,
                _ => EditOrRemoveChoice.Cancelled
            };
        }
        catch (System.Exception ex)
        {
            RuntimeLog.Fail("DIALOG", $"Edit-or-remove prompt failed, keeping the existing work: {ex.Message}");
            return EditOrRemoveChoice.Cancelled;
        }
    }

    public void SetButtonText(string yesText, string noText, string? altText = null)
    {
        var yesBtn = YesBtnCtl;
        var noBtn = NoBtnCtl;
        var altBtn = AltBtnCtl;
        if (yesBtn != null)
        {
            if (!string.Equals(yesBtn.Content as string, yesText, System.StringComparison.OrdinalIgnoreCase))
                ToolTip.SetTip(yesBtn, null);
            yesBtn.Content = yesText;
            Avalonia.Automation.AutomationProperties.SetName(yesBtn, yesText);
        }
        if (noBtn != null)
        {
            if (!string.Equals(noBtn.Content as string, noText, System.StringComparison.OrdinalIgnoreCase))
                ToolTip.SetTip(noBtn, null);
            noBtn.Content = noText;
            Avalonia.Automation.AutomationProperties.SetName(noBtn, noText);
        }
        if (altBtn != null && altText != null)
        {
            altBtn.Content = altText;
            Avalonia.Automation.AutomationProperties.SetName(altBtn, altText);
            altBtn.IsVisible = true;
        }
    }

    public static async System.Threading.Tasks.Task<bool> AskAsync(
        Window owner, string message, string title, string yesText = "Yes", string noText = "No",
        bool destructive = false)
    {
        try
        {
            var dlg = new ConfirmDialogWindow();
            dlg.SetTitle(title);
            dlg.SetMessage(message);
            dlg.SetButtonText(yesText, noText);
            if (destructive) dlg.UseDestructiveStyling();
            await dlg.ShowDialog(owner);
            return dlg.Result;
        }
        catch (System.Exception ex)
        {
            RuntimeLog.Fail("DIALOG", $"Themed confirm failed, treating as declined: {ex.Message}");
            return false;
        }
    }

    public static async System.Threading.Tasks.Task<ConfirmDialogResult> AskThreeWayAsync(
        Window owner, string title, string message, string yesText, string altText, string noText = "Cancel")
    {
        try
        {
            var dlg = new ConfirmDialogWindow();
            dlg.SetTitle(title);
            dlg.SetMessage(message);
            dlg.SetButtonText(yesText, noText, altText);
            dlg.SetButtonClasses("Success", "Secondary", "Primary");
            await dlg.ShowDialog(owner);
            return dlg.DialogResult;
        }
        catch (System.Exception ex)
        {
            RuntimeLog.Fail("DIALOG", $"Three-way prompt failed, treating as cancelled: {ex.Message}");
            return ConfirmDialogResult.Cancelled;
        }
    }

    private void InitializeComponent() { AvaloniaXamlLoader.Load(this); }
}
