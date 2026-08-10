// ctx.width/height reflect plugin.export size
let properties = [];
function render(ctx) {
    var w = ctx.width;
    var h = ctx.height;
    ctx.fillRect(w / 2 - 25, h / 2 - 25, 50, 50);
    ctx.strokeRect(0, 0, w, h);
}
plugin.export = { version: "1.0.0", width: 320, height: 180, properties, render };
