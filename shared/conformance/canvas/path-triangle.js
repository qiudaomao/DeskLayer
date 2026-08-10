// closed path filled
let properties = [];
function render(ctx) {
    ctx.fillStyle = '#ffcc00';
    ctx.beginPath();
    ctx.moveTo(100, 10);
    ctx.lineTo(180, 90);
    ctx.lineTo(20, 90);
    ctx.closePath();
    ctx.fill();
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
