// Borderless always-on-top floating widget window — the Windows twin of the
// mac FloatingPanelController (nonactivating NSPanel). A normal top-level
// window, so per-pixel transparency works here (unlike under WorkerW).
// WS_EX_NOACTIVATE keeps focus where it is, WS_EX_TOOLWINDOW keeps it out
// of Alt-Tab; click-through adds WS_EX_TRANSPARENT and disables dragging,
// mirroring the mac isClickThrough coupling.

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace DeskLayer.App;

public sealed class FloatingPanel : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLongW(IntPtr hwnd, int index, int value);

    private readonly bool clickThrough;

    /// Fires after a user drag with the new Left/Top in DIPs; the engine
    /// converts to a normalized frame and persists it.
    public Action<double, double>? OnMovedDip;

    public FloatingPanel(bool clickThrough)
    {
        this.clickThrough = clickThrough;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLongW(hwnd, GwlExStyle) | WsExToolWindow | WsExNoActivate;
            if (this.clickThrough) ex |= WsExTransparent;
            SetWindowLongW(hwnd, GwlExStyle, ex);
        };

        MouseLeftButtonDown += (_, e) =>
        {
            if (this.clickThrough || e.ButtonState != MouseButtonState.Pressed) return;
            try
            {
                DragMove(); // returns when the drag ends
                OnMovedDip?.Invoke(Left, Top);
            }
            catch (InvalidOperationException)
            {
                // Button already released — nothing to drag.
            }
        };
    }
}
