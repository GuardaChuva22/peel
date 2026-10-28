namespace FileAction.App.Shell;

/// <summary>Short messages, shown as Peel's own glass cards (see <see cref="Toasts"/>).</summary>
internal static class Notifications
{
    public static Action<string, string, bool>? Sink { get; set; } = Toasts.Info;

    public static void Show(string title, string text, bool error = false) => Sink?.Invoke(title, text, error);
}
