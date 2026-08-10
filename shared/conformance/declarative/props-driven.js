// tree built from declared properties (see .overrides.json)
let properties = [{"name": "title", "valueType": "string", "value": "Default title"}, {"name": "size", "valueType": "number", "value": "14"}];
render = () => view([
    VStack([
        Text(properties[0].value).fontSize(properties[1].value)
    ])
]);
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
