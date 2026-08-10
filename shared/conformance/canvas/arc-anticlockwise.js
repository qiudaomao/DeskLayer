// arc segment swept anticlockwise
let properties = [];
function render(ctx) {
    ctx.strokeStyle = '#00c7be';
    ctx.lineWidth = 6;
    ctx.beginPath();
    ctx.arc(100, 50, 35, Math.PI * 0.25, Math.PI * 1.25, true);
    ctx.stroke();
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
