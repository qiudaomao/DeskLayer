// asset image at natural placement
let properties = [];
function render(ctx) {
    ctx.drawImage('icon', 0, 0, 32, 32);
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
