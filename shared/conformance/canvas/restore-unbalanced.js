// restore() with empty stack is a recorded no-op, then draw
let properties = [];
function render(ctx) {
    ctx.restore();
    ctx.restore();
    ctx.fillStyle = '#8e8e93';
    ctx.fillRect(0, 0, 20, 20);
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
