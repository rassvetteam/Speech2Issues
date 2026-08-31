using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Speech2Issues.Core.Configuration;

namespace Speech2Issues.App.Services;

/// <summary>
/// Регистрирует глобальную горячую клавишу (RegisterHotKey) и доставляет нажатия
/// через сообщение WM_HOTKEY в окно приложения.
/// </summary>
public sealed class HotKeyService : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private readonly int _id;
    private nint _hwnd;
    private HwndSource? _source;
    private bool _registered;

    public HotKeyService(int id = 0x5348) => _id = id;

    public event EventHandler? Pressed;

    public bool IsRegistered => _registered;

    public void Attach(nint hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
    }

    public bool Register(HotKeySettings hotKey)
    {
        Unregister();
        if (!hotKey.IsValid)
        {
            return false;
        }

        var modifiers = (hotKey.Alt ? ModAlt : 0u)
                      | (hotKey.Ctrl ? ModControl : 0u)
                      | (hotKey.Shift ? ModShift : 0u)
                      | (hotKey.Win ? ModWin : 0u);
        if (modifiers == 0 || !Enum.TryParse<Key>(hotKey.Key, true, out var key) || key == Key.None)
        {
            return false;
        }

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
        {
            return false;
        }

        _registered = RegisterHotKey(_hwnd, _id, modifiers | ModNoRepeat, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered && _hwnd != 0)
        {
            UnregisterHotKey(_hwnd, _id);
        }

        _registered = false;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == _id)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return 0;
    }

    public void Dispose()
    {
        Unregister();
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
