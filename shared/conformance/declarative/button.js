// Button handler serializes as action id 1
let properties = [];
let clicks = 0;
render = () => view([
    Button('Click me', function () { clicks++; })
]);
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
