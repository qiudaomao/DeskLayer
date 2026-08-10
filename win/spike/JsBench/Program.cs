// DeskLayer M0 spike — Jint vs ClearScript V8 on a real canvas plugin.
//
// The port plan keeps Jint as the fallback engine behind IJsEngine; this
// records the actual per-render cost of each on clock-face.js (a real
// conformance fixture: ~150 bridged ctx calls + trig per render) against a
// no-op bridge, so the fallback decision is data. Time-boxed measurement:
// 200 warmup renders, then 3s of renders each.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.ClearScript.V8;

/// No-op ctx with the JS-contract member names; isolates engine+interop cost.
public sealed class NullBridge
{
    public string fillStyle { get; set; } = "#000000";
    public string strokeStyle { get; set; } = "#000000";
    public double lineWidth { get; set; } = 1;
    public string lineCap { get; set; } = "butt";
    public string lineJoin { get; set; } = "miter";
    public double globalAlpha { get; set; } = 1;
    public string font { get; set; } = "13px Helvetica";
    public double width => 200;
    public double height => 100;

    public void save() { }
    public void restore() { }
    public void translate(double x, double y) { }
    public void rotate(double angle) { }
    public void scale(double x, double y) { }
    public void clearRect(double x, double y, double w, double h) { }
    public void fillRect(double x, double y, double w, double h) { }
    public void strokeRect(double x, double y, double w, double h) { }
    public void beginPath() { }
    public void closePath() { }
    public void moveTo(double x, double y) { }
    public void lineTo(double x, double y) { }
    public void arc(double x, double y, double r, double s, double e, bool acw) { }
    public void fill() { }
    public void stroke() { }
    public void fillText(string text, double x, double y) { }
    public object measureText(string text) => new Dictionary<string, object> { ["width"] = text.Length * 7.0 };
    public void drawImage(string name, double x, double y, double w, double h) { }
    public object? getProp(string name) => null;
}

internal static class Program
{
    private const string Prologue =
        "var plugin = { export: null }; var console = { log: function () {}, error: function () {}, warn: function () {} };";

    private static double Measure(string name, Action renderOnce)
    {
        for (int i = 0; i < 200; i++) renderOnce();
        var sw = Stopwatch.StartNew();
        long n = 0;
        while (sw.ElapsedMilliseconds < 3000) { renderOnce(); n++; }
        var perSec = n / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"{name,-14} {perSec,10:F0} renders/sec   {sw.Elapsed.TotalMilliseconds / n,8:F4} ms/render");
        return perSec;
    }

    private static void Main()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "clock-face.js"));

        using var v8 = new V8ScriptEngine();
        v8.Execute(Prologue);
        v8.Execute(source);
        dynamic v8Render = ((dynamic)v8.Script).plugin.export.render;
        var v8Bridge = new NullBridge();
        var v8PerSec = Measure("ClearScript V8", () => v8Render(v8Bridge));

        var jint = new Jint.Engine();
        jint.Execute(Prologue);
        jint.Execute(source);
        var jintRender = jint.Evaluate("plugin.export.render");
        var jintBridge = new NullBridge();
        var jintPerSec = Measure("Jint", () => jint.Invoke(jintRender, jintBridge));

        Console.WriteLine($"V8 / Jint ratio: {v8PerSec / jintPerSec:F1}x");
        Console.WriteLine($"Jint headroom at 60fps: {jintPerSec / 60:F0} items");
    }
}
