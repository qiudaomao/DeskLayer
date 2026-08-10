// Loads/saves layout.json — port of the mac LayoutStore: hand-editable JSON,
// debounced atomic writes. Data dir: %APPDATA%\DeskLayer, overridable via
// DESKLAYER_DATA_DIR (same env var as the mac app).

using System.Text.Encodings.Web;
using System.Text.Json;

namespace DeskLayer.Core.Model;

public sealed class LayoutStore : IDisposable
{
    public static string DataDirectory =>
        Environment.GetEnvironmentVariable("DESKLAYER_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskLayer");

    public static string FilePath => Path.Combine(DataDirectory, "layout.json");

    private readonly object gate = new();
    private readonly Timer saveTimer;
    private bool savePending;
    public Layout Layout { get; private set; }
    public event Action? OnChange;

    public LayoutStore()
    {
        saveTimer = new Timer(_ => FlushIfPending(), null, Timeout.Infinite, Timeout.Infinite);
        Layout = Load();
    }

    private static Layout Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                return Layout.ReadJson(doc.RootElement);
            }
        }
        catch (Exception)
        {
            // A malformed hand-edited file must not brick the app; start
            // empty and leave the file for the user to fix.
        }
        return new Layout();
    }

    public void Update(Func<Layout, Layout> mutate)
    {
        lock (gate)
        {
            Layout = mutate(Layout);
            savePending = true;
            saveTimer.Change(500, Timeout.Infinite); // debounce
        }
        OnChange?.Invoke();
    }

    private void FlushIfPending()
    {
        Layout snapshot;
        lock (gate)
        {
            if (!savePending) return;
            savePending = false;
            snapshot = Layout;
        }
        try
        {
            Directory.CreateDirectory(DataDirectory);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                snapshot.WriteJson(writer);
            }
            var tmp = FilePath + ".tmp";
            File.WriteAllBytes(tmp, stream.ToArray());
            File.Move(tmp, FilePath, overwrite: true); // atomic on NTFS
        }
        catch (Exception)
        {
            // Retried on the next change; losing one debounce tick is fine.
        }
    }

    public void Dispose()
    {
        FlushIfPending();
        saveTimer.Dispose();
    }
}
