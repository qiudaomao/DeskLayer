// Rect as a divider bar
let properties = [];
render = () => view([Rect().frame(60, 4).background('#ffffff40').cornerRadius(2)]);
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
