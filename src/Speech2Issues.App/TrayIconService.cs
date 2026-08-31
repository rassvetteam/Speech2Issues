using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Speech2Issues.App.Services;

namespace Speech2Issues.App;

/// <summary>
/// Иконка в системном трее с меню: запись, открытие окна, настройки и выход.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notify;
    private readonly ToolStripMenuItem _toggleItem;

    public TrayIconService(string themeId)
    {
        _toggleItem = new ToolStripMenuItem("Начать запись");
        _toggleItem.Click += (_, _) => ToggleRecordingRequested?.Invoke(this, EventArgs.Empty);

        var showItem = new ToolStripMenuItem("Открыть окно");
        showItem.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        var settingsItem = new ToolStripMenuItem("Настройки");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new ToolStripMenuItem("Выход");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_toggleItem);
        menu.Items.Add(showItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notify = new NotifyIcon
        {
            Icon = CreateAppIcon(themeId),
            Text = "Speech2Issues",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notify.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        ThemeService.ThemeChanged += SetTheme;
    }

    public event EventHandler? ShowRequested;
    public event EventHandler? ToggleRecordingRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    public void SetRecording(bool recording) => _toggleItem.Text = recording ? "Остановить запись" : "Начать запись";

    public void ShowBalloon(string title, string message) => _notify.ShowBalloonTip(4000, title, message, ToolTipIcon.Info);

    public void SetTheme(string themeId)
    {
        var oldIcon = _notify.Icon;
        _notify.Icon = CreateAppIcon(themeId);
        oldIcon?.Dispose();
    }

    public void Dispose()
    {
        ThemeService.ThemeChanged -= SetTheme;
        _notify.Visible = false;
        var icon = _notify.Icon;
        _notify.Dispose();
        icon?.Dispose();
    }

    private static Icon CreateAppIcon(string themeId)
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri(ThemeService.GetIconUri(themeId), UriKind.Absolute))
            ?? throw new InvalidOperationException("Не удалось загрузить иконку темы.");
        using var source = new Bitmap(resource.Stream);
        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, 0, 0, 32, 32);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint handle);
}
