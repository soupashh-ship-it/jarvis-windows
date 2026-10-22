using System.Windows;
using Jarvis.Core;

namespace Jarvis.App;

/// <summary>Lets the core talk to the Windows shell without knowing about WPF.</summary>
public sealed class ShellUi : IUserInterface
{
    private readonly App _app;

    public ShellUi(App app) => _app = app;

    public Task NotifyAsync(string title, string body, CancellationToken ct = default)
    {
        _app.Dispatcher.BeginInvoke(() => _app.Tray?.Notify(title, body));
        return Task.CompletedTask;
    }

    public Task<bool?> AskAsync(string question, CancellationToken ct = default)
    {
        var operation = _app.Dispatcher.InvokeAsync(() => ConfirmDialog.AskAsync(question, _app.Deck));
        return operation.Task.Unwrap();
    }

    public void ShowDeck() => _app.Dispatcher.BeginInvoke(_app.ShowDeck);
}
