// translate inside save/restore, then draw at origin
let properties = [];
function render(ctx) {
    ctx.fillStyle = '#ff2d55';
    ctx.save();
    ctx.translate(40, 20);
    ctx.fillRect(0, 0, 30, 30);
    ctx.restore();
    ctx.fillRect(0, 0, 10, 10);
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
