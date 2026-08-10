// Section→VStack and Paragraph→Text aliases
let properties = [];
render = () => view([
    Section([
        Paragraph('Aliased').fontSize(15)
    ]).padding(8)
]);
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
