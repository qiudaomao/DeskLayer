// font set + fillText
let properties = [];
function render(ctx) {
    ctx.font = '16px Menlo';
    ctx.fillStyle = '#ffffff';
    ctx.fillText('Hello, World!', 10, 30);
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
