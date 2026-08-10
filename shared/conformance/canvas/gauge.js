// composite: track arc, value arc, centered label
let properties = [];
function render(ctx) {
    ctx.strokeStyle = '#3a3a3c';
    ctx.lineWidth = 10;
    ctx.beginPath();
    ctx.arc(100, 80, 60, Math.PI, Math.PI * 2, false);
    ctx.stroke();
    ctx.strokeStyle = '#ff453a';
    ctx.beginPath();
    ctx.arc(100, 80, 60, Math.PI, Math.PI * 1.65, false);
    ctx.stroke();
    ctx.font = 'bold 18px Helvetica';
    ctx.fillStyle = '#ffffff';
    var label = '65%';
    ctx.fillText(label, 100 - ctx.measureText(label).width / 2, 75);
}
plugin.export = { version: "1.0.0", width: 200, height: 110, properties, render };
