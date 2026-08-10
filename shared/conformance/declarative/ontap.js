// onTapGesture registers an action id on any node
let properties = [];
render = () => view([
    Rect().frame(50, 50).background('#ff9500').onTapGesture(function (p) {})
]);
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
