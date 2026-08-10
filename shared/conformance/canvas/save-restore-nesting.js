// two-deep save/restore nesting
let properties = [];
function render(ctx) {
    ctx.save();
    ctx.translate(10, 10);
    ctx.save();
    ctx.scale(2, 2);
    ctx.fillRect(0, 0, 5, 5);
    ctx.restore();
    ctx.fillRect(20, 20, 5, 5);
    ctx.restore();
    ctx.fillRect(40, 40, 5, 5);
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
