// WebView2 host for webview-mode plugins — the Windows twin of the mac
// WebViewHost (WKWebView). Two targets:
//   - wallpaper: an opaque window reparented under WorkerW (M0-proven),
//     sitting above the D2D wallpaper window in the sibling z-order
//   - floating: a FloatingPanel, normal top-level window
// url/userAgent/zoom/scroll-offset come from WebViewConfig; headers and
// cookies join in the M4 bindings pass. WebView2 must be disposed on the
// UI thread (M0: finalizer-thread disposal crashes with E_NOINTERFACE).

using System.Globalization;
using System.IO;
using System.Windows;
using DeskLayer.Core.Js;
using DeskLayer.Core.Model;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DeskLayer.App;

public sealed class WebViewHostWindow : IDisposable
{
    public Window Window { get; }
    private readonly WebView2 view;

    /// UI thread only. `pixelRect` is the item frame in physical pixels;
    /// `scale` converts to WPF DIPs.
    public WebViewHostWindow(WebViewConfig config, LayoutItem item,
                             System.Drawing.RectangleF pixelRect, double scale,
                             Action<string> log)
    {
        view = new WebView2
        {
            CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = Path.Combine(LayoutStore.DataDirectory, "WebView2"),
            },
        };
        view.CoreWebView2InitializationCompleted += (_, e) =>
        {
            if (!e.IsSuccess)
            {
                log($"webview init failed: {e.InitializationException?.Message}");
                return;
            }
            if (config.UserAgent is { } userAgent)
                view.CoreWebView2.Settings.UserAgent = userAgent;
            view.ZoomFactor = config.Zoom;
            if (config.OffsetX != 0 || config.OffsetY != 0)
                view.CoreWebView2.NavigationCompleted += (_, _) => _ = view.CoreWebView2.ExecuteScriptAsync(
                    $"window.scrollTo({config.OffsetX.ToString(CultureInfo.InvariantCulture)}, {config.OffsetY.ToString(CultureInfo.InvariantCulture)})");
        };
        if (Uri.TryCreate(config.Url, UriKind.Absolute, out var uri)) view.Source = uri;
        else log($"webview: bad url \"{config.Url}\"");

        if (item.Target == RenderTarget.Wallpaper)
        {
            Window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = view,
            };
        }
        else
        {
            Window = new FloatingPanel(item.ClickThrough) { Content = view };
        }
        Window.Left = pixelRect.X / scale;
        Window.Top = pixelRect.Y / scale;
        Window.Width = pixelRect.Width / scale;
        Window.Height = pixelRect.Height / scale;
    }

    public void Dispose()
    {
        try { view.Dispose(); } catch { /* already down */ }
        try { Window.Close(); } catch { /* already closed */ }
    }
}
