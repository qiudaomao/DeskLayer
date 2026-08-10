// Records every ctx call as data instead of drawing — the C# twin of the mac
// RecordingCanvas. The recording rules live in
// shared/conformance/runner-notes.md; both implementations must match them
// exactly, or the golden diff is the bug report.
//
// Members are lowercase to match the JS contract (Jint binds case-sensitively).

namespace DeskLayer.Core.Conformance;

public sealed class RecordingCanvas
{
    public List<object> Ops { get; } = new();
    private readonly double widthPts;
    private readonly double heightPts;

    /// Same bridge production uses: PropertyValue.BridgeValue or null.
    public Func<string, object?>? PropertyProvider { get; set; }

    public RecordingCanvas(double width, double height)
    {
        widthPts = width;
        heightPts = height;
    }

    public void MarkFrame(int index) =>
        Ops.Add(new Dictionary<string, object> { ["op"] = "frame", ["index"] = (double)index });

    private void Record(string op, params object[] args) =>
        Ops.Add(new Dictionary<string, object> { ["op"] = op, ["args"] = args.ToList() });

    private void RecordSet(string name, object value) =>
        Ops.Add(new Dictionary<string, object> { ["op"] = "set", ["name"] = name, ["value"] = value });

    // ---- properties (recorded on every assignment) ----

    private string fillStyleValue = "#000000";
    public string fillStyle { get => fillStyleValue; set { fillStyleValue = value; RecordSet("fillStyle", value); } }

    private string strokeStyleValue = "#000000";
    public string strokeStyle { get => strokeStyleValue; set { strokeStyleValue = value; RecordSet("strokeStyle", value); } }

    private double lineWidthValue = 1;
    public double lineWidth { get => lineWidthValue; set { lineWidthValue = value; RecordSet("lineWidth", value); } }

    private string lineCapValue = "butt";
    public string lineCap { get => lineCapValue; set { lineCapValue = value; RecordSet("lineCap", value); } }

    private string lineJoinValue = "miter";
    public string lineJoin { get => lineJoinValue; set { lineJoinValue = value; RecordSet("lineJoin", value); } }

    private double globalAlphaValue = 1;
    public double globalAlpha { get => globalAlphaValue; set { globalAlphaValue = value; RecordSet("globalAlpha", value); } }

    private string fontValue = "13px Helvetica";
    public string font { get => fontValue; set { fontValue = value; RecordSet("font", value); } }

    public double width => widthPts;
    public double height => heightPts;

    // ---- state & transforms ----

    public void save() => Record("save");
    public void restore() => Record("restore");
    public void translate(double x, double y) => Record("translate", x, y);
    public void rotate(double angle) => Record("rotate", angle);
    public void scale(double x, double y) => Record("scale", x, y);

    // ---- rects ----

    public void clearRect(double x, double y, double w, double h) => Record("clearRect", x, y, w, h);
    public void fillRect(double x, double y, double w, double h) => Record("fillRect", x, y, w, h);
    public void strokeRect(double x, double y, double w, double h) => Record("strokeRect", x, y, w, h);

    // ---- paths ----

    public void beginPath() => Record("beginPath");
    public void closePath() => Record("closePath");
    public void moveTo(double x, double y) => Record("moveTo", x, y);
    public void lineTo(double x, double y) => Record("lineTo", x, y);
    public void arc(double x, double y, double r, double startAngle, double endAngle, bool anticlockwise) =>
        Record("arc", x, y, r, startAngle, endAngle, anticlockwise);
    public void fill() => Record("fill");
    public void stroke() => Record("stroke");

    // ---- text ----

    public void fillText(string text, double x, double y) => Record("fillText", text, x, y);

    public MeasureResult measureText(string text)
    {
        Record("measureText", text);
        // Deterministic stub: 7 × UTF-16 code-unit count (runner-notes.md).
        return new MeasureResult(text.Length * 7.0);
    }

    // ---- images / properties ----

    public void drawImage(string name, double x, double y, double w, double h) =>
        Record("drawImage", name, x, y, w, h);

    public object? getProp(string name)
    {
        Record("getProp", name);
        return PropertyProvider?.Invoke(name);
    }
}

public sealed class MeasureResult
{
    public double width { get; }
    public MeasureResult(double width) => this.width = width;
}
