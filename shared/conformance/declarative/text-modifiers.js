// chained text modifiers keep declaration order
let properties = [];
render = () => view([
    Text('styled').fontSize(18).bold().textColor('#ffffff').opacity(0.9).lineLimit(2)
]);
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
