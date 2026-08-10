// clearRect over the full canvas, then a stroked border
let properties = [];
function render(ctx) {
    ctx.clearRect(0, 0, ctx.width, ctx.height);
    ctx.strokeStyle = '#34c759';
    ctx.lineWidth = 2.5;
    ctx.strokeRect(5, 5, 190, 90);
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
