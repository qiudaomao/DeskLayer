// bold prefix and multi-word family names
let properties = [];
function render(ctx) {
    ctx.font = 'bold 14px Helvetica';
    ctx.fillText('Bold', 10, 20);
    ctx.font = '11px SF Mono';
    ctx.fillText('mono', 10, 40);
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
