// non-array children are normalized to one-element arrays
let properties = [];
render = () => view(
    VStack(Text('solo'))
);
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
