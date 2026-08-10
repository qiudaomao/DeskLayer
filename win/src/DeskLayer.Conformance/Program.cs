// DeskLayer conformance runner (Windows) — runs shared/conformance fixtures
// through the real Jint runtime and verifies the checked-in goldens
// byte-for-byte. The macOS test suite generated those goldens; this runner
// only ever verifies (runner-notes.md: only the mac regenerates).
//
// Usage: DeskLayer.Conformance [path-to-shared/conformance]
// Without an argument, walks up from the exe looking for shared/conformance.

using System.Text;
using System.Text.Json;
using DeskLayer.Core.Conformance;
using DeskLayer.Core.Js;
using DeskLayer.Core.Model;

var conformanceRoot = args.Length > 0 ? args[0] : FindConformanceRoot()
    ?? throw new InvalidOperationException("shared/conformance not found — pass its path as the first argument");

Console.WriteLine($"conformance root: {conformanceRoot}");
var failures = 0;
var passes = 0;

RunSuite("canvas", RunCanvasFixture);
RunSuite("declarative", RunDeclarativeFixture);

Console.WriteLine();
Console.WriteLine(failures == 0 ? $"ALL GREEN — {passes} fixtures match" : $"{failures} FAILED, {passes} passed");
return failures == 0 ? 0 : 1;

void RunSuite(string suite, Func<string, string, IReadOnlyDictionary<string, PropertyValue>, string?> run)
{
    var dir = Path.Combine(conformanceRoot, suite);
    var fixtures = Directory.GetFiles(dir, "*.js").OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal).ToList();
    Console.WriteLine($"\n[{suite}] {fixtures.Count} fixtures");
    foreach (var path in fixtures)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var goldenPath = Path.Combine(dir, "golden", name + ".json");
        try
        {
            var output = run(name, File.ReadAllText(path), LoadOverrides(Path.Combine(dir, name + ".overrides.json")));
            if (output == null) { Fail(name, "no output"); continue; }
            var expected = File.ReadAllText(goldenPath);
            if (output + "\n" == expected) { passes++; Console.WriteLine($"  ok    {name}"); }
            else
            {
                Fail(name, "output drifted from golden");
                File.WriteAllText(Path.Combine(dir, "golden", name + ".actual-win.json"), output + "\n");
            }
        }
        catch (Exception ex)
        {
            Fail(name, ex.Message);
        }
    }
}

void Fail(string name, string reason)
{
    failures++;
    Console.WriteLine($"  FAIL  {name}: {reason}");
}

string? RunCanvasFixture(string name, string source, IReadOnlyDictionary<string, PropertyValue> overrides)
{
    using var instance = PluginInstance.Boot(name, source, overrides);
    if (instance == null) return null;
    if (instance.Mode != RenderMode.Canvas) throw new InvalidOperationException("not a canvas plugin");

    var recorder = new RecordingCanvas(
        instance.DeclaredWidth > 0 ? instance.DeclaredWidth : 200,
        instance.DeclaredHeight > 0 ? instance.DeclaredHeight : 100)
    {
        PropertyProvider = propName => instance.PropertyNamed(propName)?.BridgeValue,
    };
    for (var frame = 0; frame < 2; frame++)
    {
        recorder.MarkFrame(frame);
        if (!instance.CallRender(recorder))
            throw new InvalidOperationException($"render threw on frame {frame}: {instance.ErrorMessage}");
    }
    return CanonicalJson.Serialize(recorder.Ops);
}

string? RunDeclarativeFixture(string name, string source, IReadOnlyDictionary<string, PropertyValue> overrides)
{
    using var instance = PluginInstance.Boot(name, source, overrides);
    if (instance == null) return null;
    if (instance.Mode != RenderMode.Declarative) throw new InvalidOperationException("not a declarative plugin");

    var frames = new List<object>();
    for (var frame = 0; frame < 2; frame++)
    {
        var json = instance.CallRenderTree()
            ?? throw new InvalidOperationException($"no tree on frame {frame}: {instance.ErrorMessage}");
        frames.Add(JsonDocument.Parse(json).RootElement.Clone());
    }
    return CanonicalJson.Serialize(new Dictionary<string, object> { ["frames"] = frames });
}

static IReadOnlyDictionary<string, PropertyValue> LoadOverrides(string path)
{
    var overrides = new Dictionary<string, PropertyValue>();
    if (!File.Exists(path)) return overrides;
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    foreach (var entry in doc.RootElement.EnumerateArray())
    {
        var name = entry.GetProperty("name").GetString()!;
        var valueType = entry.GetProperty("valueType").GetString()!;
        var raw = PropertyValue.FromJsonElement(entry.GetProperty("value"));
        var value = PropertyValue.Coerce(raw, valueType);
        if (value != null) overrides[name] = value.Value;
    }
    return overrides;
}

static string? FindConformanceRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        var candidate = Path.Combine(dir.FullName, "shared", "conformance");
        if (Directory.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    return null;
}
