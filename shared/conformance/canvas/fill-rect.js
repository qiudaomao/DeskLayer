// fillStyle + fillRect, integer and fractional coordinates
let properties = [];
function render(ctx) {
    ctx.fillStyle = '#ff3b30';
    ctx.fillRect(10, 10, 80, 40);
    ctx.fillStyle = 'rgb(0, 122, 255)';
    ctx.fillRect(0.5, 20.25, 30, 30);
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
