// four gapped arc segments forming a ring
let properties = [];
function render(ctx) {
    ctx.strokeStyle = '#af52de';
    ctx.lineWidth = 8;
    for (var i = 0; i < 4; i++) {
        var start = i * Math.PI / 2 + 0.15;
        var end = (i + 1) * Math.PI / 2 - 0.15;
        ctx.beginPath();
        ctx.arc(100, 50, 30, start, end, false);
        ctx.stroke();
    }
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
