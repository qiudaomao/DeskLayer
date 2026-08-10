// full circle, clockwise
let properties = [];
function render(ctx) {
    ctx.fillStyle = '#ff9500';
    ctx.beginPath();
    ctx.arc(100, 50, 40, 0, Math.PI * 2, false);
    ctx.fill();
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
