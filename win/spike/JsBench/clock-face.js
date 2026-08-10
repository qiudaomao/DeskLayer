// composite: circle + 12 rotated tick marks
let properties = [];
function render(ctx) {
    ctx.save();
    ctx.translate(100, 50);
    ctx.strokeStyle = '#ffffff';
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.arc(0, 0, 45, 0, Math.PI * 2, false);
    ctx.stroke();
    for (var i = 0; i < 12; i++) {
        ctx.save();
        ctx.rotate(i * Math.PI / 6);
        ctx.beginPath();
        ctx.moveTo(0, -45);
        ctx.lineTo(0, -38);
        ctx.stroke();
        ctx.restore();
    }
    ctx.restore();
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
