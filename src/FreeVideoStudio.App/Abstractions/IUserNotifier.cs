using Avalonia.Controls;
using FreeVideoStudio.App.Controls;

namespace FreeVideoStudio.App.Abstractions;

public interface IUserNotifier
{
    void Notify(string text, NoticeKind kind = NoticeKind.Info);

    void Alert(string title, string message);

    Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText);

    Task<ConfirmDialogWindow.SaveChangesChoice> AskSaveChangesAsync(string title, string message);
}

public interface IActiveWindowProvider
{
    Window? ActiveWindow { get; }
}
