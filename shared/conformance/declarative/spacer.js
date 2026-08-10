// Spacer between two texts
let properties = [];
render = () => view([
    HStack([
        Text('left'),
        Spacer(),
        Text('right')
    ]).frame(200, 30)
]);
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
