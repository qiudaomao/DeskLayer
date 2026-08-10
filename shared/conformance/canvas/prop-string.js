// getProp of a string property drawn as text
let properties = [{"name": "label", "valueType": "string", "value": "hello"}];
function render(ctx) {
    ctx.font = '13px Helvetica';
    ctx.fillText(String(ctx.getProp('label')), 10, 20);
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
