using System.IO;
using TurrisStudio.Core.Service;
using Forms = System.Windows.Forms;

namespace TurrisStudio;

/// <summary>System-tray icon; the app keeps feeding the display while the window is closed.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private bool _hintShown;

    public TrayIcon(Action show, Action exit)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        _icon = new Forms.NotifyIcon
        {
            Icon = File.Exists(iconPath) ? new System.Drawing.Icon(iconPath) : System.Drawing.SystemIcons.Application,
            Text = "Turris Studio",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        _icon.ContextMenuStrip.Items.Add("Open Turris Studio", null, (_, _) => show());
        var overlayItem = new Forms.ToolStripMenuItem("Show stats overlay") { CheckOnClick = true, Checked = AppServices.Settings.OverlayEnabled };
        overlayItem.CheckedChanged += (_, _) =>
        {
            AppServices.Settings.OverlayEnabled = overlayItem.Checked;
            AppServices.Store.Save();
            AppServices.Device.InvalidateLayout();
        };
        _icon.ContextMenuStrip.Items.Add(overlayItem);
        _icon.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _icon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => exit());
        _icon.DoubleClick += (_, _) => show();
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) show(); };

        AppServices.Device.StateChanged += UpdateTooltip;
    }

    private void UpdateTooltip()
    {
        var device = AppServices.Device;
        var text = device.State switch
        {
            ConnectionState.Connected => "Turris Studio - connected",
            ConnectionState.Busy => "Turris Studio - uploading",
            _ => "Turris Studio - " + device.StatusText,
        };
        try { _icon.Text = text.Length > 63 ? text[..63] : text; } catch { /* ignored */ }
    }

    public void ShowHintOnce()
    {
        if (_hintShown) return;
        _hintShown = true;
        _icon.ShowBalloonTip(3000, "Turris Studio is still running",
            "Your screen keeps updating in the background. Open it again from the tray icon.", Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        AppServices.Device.StateChanged -= UpdateTooltip;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
