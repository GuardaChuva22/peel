using System.Windows;
using Forms = System.Windows.Forms;
using static FileAction.Core.Loc;

namespace FileAction.App.Shell;

/// <summary>The notification-area icon with the app menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _pause;
    private Forms.ToolStripMenuItem? _update;
    private Action? _balloonClick;

    public TrayIcon(Action openHome, Action openSettings, Action openClipboard, Action openShelf, Action showHelp, Action<bool> setPaused, bool paused, Action exit,
        bool detectLinks, Action<bool> setDetectLinks)
    {
        var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Peel.ico"))!.Stream;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Peel", null, (_, _) => openHome());
        menu.Items.Add(T("Kolo pro schránku", "Wheel for the clipboard"), null, (_, _) => openClipboard());
        menu.Items.Add(T("Nastavení…", "Settings…"), null, (_, _) => openSettings());
        menu.Items.Add(T("Prázdný plovoucí panel", "Empty floating shelf"), null, (_, _) => openShelf());
        menu.Items.Add(T("Jak to funguje", "How it works"), null, (_, _) => showHelp());
        _pause = new Forms.ToolStripMenuItem(T("Pozastavit gesta", "Pause gestures")) { Checked = paused, CheckOnClick = true };
        _pause.CheckedChanged += (_, _) => setPaused(_pause.Checked);
        menu.Items.Add(_pause);
        var links = new Forms.ToolStripMenuItem(T("Nabízet stažení zkopírovaných videí", "Offer downloads for copied video links")) { Checked = detectLinks, CheckOnClick = true };
        links.CheckedChanged += (_, _) => setDetectLinks(links.Checked);
        menu.Items.Add(links);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(T("Ukončit", "Exit"), null, (_, _) => exit());

        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream),
            Text = "Peel",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => openHome();
        _icon.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonClick;
            _balloonClick = null;
            action?.Invoke();
        };
        _icon.BalloonTipClosed += (_, _) => _balloonClick = null;
    }

    public bool Paused
    {
        set => _pause.Checked = value;
    }

    /// <summary>Adds "Update to x.y.z" at the top of the menu and announces it once.</summary>
    public void OfferUpdate(Version version, Action install)
    {
        var text = T($"Aktualizovat na verzi {version}", $"Update to version {version}");
        if (_update is null)
        {
            _update = new Forms.ToolStripMenuItem(text) { Font = new System.Drawing.Font(_icon.ContextMenuStrip!.Font, System.Drawing.FontStyle.Bold) };
            _update.Click += (_, _) => install();
            _icon.ContextMenuStrip.Items.Insert(0, _update);
            _icon.ContextMenuStrip.Items.Insert(1, new Forms.ToolStripSeparator());
        }
        // The announcement itself is a Peel notification card (App.OfferUpdate).
    }

    public void Show(string title, string text, bool error)
    {
        _balloonClick = null;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = string.IsNullOrWhiteSpace(text) ? " " : text;
        _icon.BalloonTipIcon = error ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info;
        _icon.ShowBalloonTip(4000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
