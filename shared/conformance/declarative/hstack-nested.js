// stacks nested inside stacks
let properties = [];
render = () => view([
    HStack([
        VStack([Text('a'), Text('b')]).spacing(2),
        VStack([Text('c'), Text('d')]).spacing(2)
    ]).spacing(10)
]);
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
