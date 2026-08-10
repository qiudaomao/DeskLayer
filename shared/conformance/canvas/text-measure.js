// measureText (stub width = 7 x utf16 length) drives layout
let properties = [];
function render(ctx) {
    ctx.font = '12px Helvetica';
    var label = 'CPU';
    var w = ctx.measureText(label).width;
    ctx.fillText(label, (ctx.width - w) / 2, 20);
    var wide = ctx.measureText('a much longer string').width;
    ctx.fillText('right', ctx.width - wide, 40);
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
