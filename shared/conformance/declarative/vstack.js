// VStack with container modifiers
let properties = [];
render = () => view([
    VStack([
        Text('one'),
        Text('two')
    ]).spacing(6).padding(14).background('#101418e6').cornerRadius(12)
]);
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
