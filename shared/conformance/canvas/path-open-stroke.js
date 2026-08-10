// open polyline stroked, no closePath
let properties = [];
function render(ctx) {
    ctx.strokeStyle = '#5856d6';
    ctx.beginPath();
    ctx.moveTo(0, 50);
    ctx.lineTo(50, 20);
    ctx.lineTo(100, 80);
    ctx.lineTo(150, 30);
    ctx.lineTo(200, 60);
    ctx.stroke();
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
