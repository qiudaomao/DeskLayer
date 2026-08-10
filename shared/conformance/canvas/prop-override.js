// persisted overrides replace declared defaults (see .overrides.json)
let properties = [{"name": "label", "valueType": "string", "value": "default"}, {"name": "bars", "valueType": "number", "value": "2"}];
function render(ctx) {
    ctx.font = '13px Helvetica';
    ctx.fillText(String(ctx.getProp('label')), 10, 20);
    ctx.fillRect(0, 30, ctx.getProp('bars') * 10, 8);
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
