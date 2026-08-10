// number/color/boolean properties steering the draw
let properties = [{"name": "count", "valueType": "number", "value": "3"}, {"name": "tint", "valueType": "color", "value": "#ff3b30"}, {"name": "filled", "valueType": "boolean", "value": "true"}];
function render(ctx) {
    var n = ctx.getProp('count');
    var color = ctx.getProp('tint');
    var on = ctx.getProp('filled');
    ctx.fillStyle = String(color);
    for (var i = 0; i < n; i++) {
        if (on) {
            ctx.fillRect(i * 20, 10, 16, 16);
        } else {
            ctx.strokeRect(i * 20, 10, 16, 16);
        }
    }
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
