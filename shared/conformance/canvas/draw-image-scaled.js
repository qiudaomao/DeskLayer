// images scaled from canvas size and at fractional origin
let properties = [];
function render(ctx) {
    ctx.drawImage('background', 10, 10, ctx.width - 20, 50);
    ctx.drawImage('badge', 150.5, 60.25, 24, 24);
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
