// Donut gauge — the WPF twin of the mac Ring node. Ring(to) draws 0…to;
// Ring(from, to) draws an arc segment so several stacked Rings make a
// segmented ring. Starts at 12 o'clock; a full 0…to<1 arc gets a round cap,
// partial segments stay flat so neighbours butt cleanly (mac parity).

using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using Size = System.Windows.Size;

namespace DeskLayer.App;

public sealed class RingElement : FrameworkElement
{
    public double From { get; init; }
    public double To { get; init; }
    public double StrokeWidth { get; init; } = 8;
    public Brush RingBrush { get; init; } = Brushes.LimeGreen;
    public Brush? TrackBrush { get; init; }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(RenderSize.Width, RenderSize.Height);
        if (size <= StrokeWidth) return;
        var radius = (size - StrokeWidth) / 2;
        var center = new Point(RenderSize.Width / 2, RenderSize.Height / 2);

        if (TrackBrush != null)
            dc.DrawEllipse(null, new Pen(TrackBrush, StrokeWidth), center, radius, radius);

        if (To <= From) return;
        var cap = From == 0 && To < 1 ? PenLineCap.Round : PenLineCap.Flat;
        var pen = new Pen(RingBrush, StrokeWidth) { StartLineCap = cap, EndLineCap = cap };

        if (To - From >= 1)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        Point At(double fraction)
        {
            var angle = (fraction * 360 - 90) * Math.PI / 180;
            return new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(At(From), false, false);
            ctx.ArcTo(At(To), new Size(radius, radius), 0,
                To - From > 0.5, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}
