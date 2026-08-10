// lineCap and lineJoin state changes between strokes
let properties = [];
function render(ctx) {
    ctx.strokeStyle = '#ffffff';
    ctx.lineWidth = 10;
    ctx.lineCap = 'round';
    ctx.beginPath();
    ctx.moveTo(20, 20);
    ctx.lineTo(180, 20);
    ctx.stroke();
    ctx.lineCap = 'square';
    ctx.lineJoin = 'bevel';
    ctx.beginPath();
    ctx.moveTo(20, 60);
    ctx.lineTo(100, 90);
    ctx.lineTo(180, 60);
    ctx.stroke();
    ctx.lineJoin = 'round';
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
