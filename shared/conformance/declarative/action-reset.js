// action ids restart at 1 every render — frames must be identical
let properties = [];
render = () => view([
    Button('first', function () {}),
    Button('second', function () {}),
    Rect().frame(10, 10).onTapGesture(function () {})
]);
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
