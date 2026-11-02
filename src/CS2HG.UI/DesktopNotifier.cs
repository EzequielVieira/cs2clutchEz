using Forms = System.Windows.Forms;

namespace CS2HG.UI;

/// <summary>
/// Windows notification (a tray balloon, which Windows 10/11 shows as a toast) — works for an unpackaged app
/// without registering a notification identity.
/// </summary>
public sealed class DesktopNotifier : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public DesktopNotifier()
    {
        var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = stream is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(stream),
            Text = Core.Brand.Name,
            Visible = false,
        };
        _icon.BalloonTipClicked += (_, _) => Clicked?.Invoke();
        _icon.Click += (_, _) => Clicked?.Invoke();
        // The tray icon only exists while a notification is showing.
        _icon.BalloonTipClosed += (_, _) => _icon.Visible = false;
    }

    public event Action? Clicked;

    public void Show(string title, string text, bool warning)
    {
        _icon.Visible = true;
        _icon.ShowBalloonTip(8000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
