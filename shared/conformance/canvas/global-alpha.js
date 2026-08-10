// overlapping fills at different alphas
let properties = [];
function render(ctx) {
    ctx.fillStyle = '#000000';
    ctx.globalAlpha = 0.5;
    ctx.fillRect(0, 0, 100, 100);
    ctx.globalAlpha = 0.25;
    ctx.fillRect(50, 0, 100, 100);
    ctx.globalAlpha = 1;
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
