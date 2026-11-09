
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace AirStream.App.Services;

public sealed class SystemTrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public SystemTrayService()
    {
        var contextMenu = new Forms.ContextMenuStrip();

        var openItem = new Forms.ToolStripMenuItem("Open AirStream");
        openItem.Click += (_, _) => OpenRequested?.Invoke();

        var exitItem = new Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        contextMenu.Items.Add(openItem);
        contextMenu.Items.Add(new Forms.ToolStripSeparator());
        contextMenu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "AirStream",
            Icon = Drawing.SystemIcons.Application,
            ContextMenuStrip = contextMenu,
            Visible = true
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                OpenRequested?.Invoke();
            }
        };
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
