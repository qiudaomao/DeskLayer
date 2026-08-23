// Async remote images for the community UI — Avalonia has no built-in URL
// image source, so tiles get their bitmap when the download lands. Cached
// per-URL for the process lifetime (thumbnails repeat across pages),
// tolerant of failure (a broken URL just keeps the placeholder).

using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace DeskLayer.LinuxApp.Ui;

public static class WebImage
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Dictionary<string, Bitmap?> Cache = new();

    /// Fetches the URL and hands the decoded bitmap to `apply` on the UI
    /// thread (skipped entirely on failure).
    public static void Into(string url, Action<Bitmap> apply)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(url, out var hit))
            {
                if (hit != null) apply(hit);
                return;
            }
        }
        _ = Task.Run(async () =>
        {
            Bitmap? bitmap = null;
            try
            {
                var bytes = await Http.GetByteArrayAsync(url);
                bitmap = new Bitmap(new MemoryStream(bytes));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                or UriFormatException or InvalidOperationException or ArgumentException)
            {
            }
            lock (Cache) Cache[url] = bitmap;
            if (bitmap != null)
                await Dispatcher.UIThread.InvokeAsync(() => apply(bitmap));
        });
    }
}
