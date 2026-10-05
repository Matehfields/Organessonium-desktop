using System.Drawing;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Organessonium.Services.Sync;

// UI adapter only. The synchronization worker does not reference this class.
public sealed class TrayManager : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _pause;
    private readonly Dispatcher _dispatcher;
    private readonly Icon _image;
    private readonly Dictionary<string, DateTime> _notifications = new();

    public TrayManager(Dispatcher dispatcher, Action open, Action sync, Action<bool> pause, Action exit)
    {
        _dispatcher = dispatcher;
        using var source = System.Windows.Application.GetResourceStream(new Uri("/Organessonium;component/Assets/app.ico", UriKind.Relative))?.Stream;
        _image = source == null ? (Icon)SystemIcons.Application.Clone() : new Icon(source);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("Organessônium") { Enabled = false });
        menu.Items.Add("Abrir Organessônium", null, (_, _) => open());
        menu.Items.Add("Sincronizar agora", null, (_, _) => sync());
        _pause = new Forms.ToolStripMenuItem("Pausar sincronização");
        _pause.Click += (_, _) => { _pause.Checked = !_pause.Checked; pause(_pause.Checked); };
        menu.Items.Add(_pause);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => exit());
        _icon = new Forms.NotifyIcon { Icon = _image, Text = "Organessônium", ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => open();
        _icon.BalloonTipClicked += (_, _) => open();
    }

    public void Update(bool paused, string state) => _dispatcher.BeginInvoke(() =>
    {
        _pause.Checked = paused;
        _pause.Text = paused ? "Retomar sincronização" : "Pausar sincronização";
        _icon.Text = ("Organessônium: " + state)[..Math.Min(63, ("Organessônium: " + state).Length)];
    });

    public void Notify(string message) => _dispatcher.BeginInvoke(() =>
    {
        var now = DateTime.UtcNow;
        if (_notifications.TryGetValue(message, out var last) && now - last < TimeSpan.FromMinutes(10)) return;
        if (_notifications.Count > 128) _notifications.Clear();
        _notifications[message] = now;
        _icon.ShowBalloonTip(5000, "Organessônium", message, Forms.ToolTipIcon.Info);
    });

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _image.Dispose();
    }
}
