// three-argument frame with alignment
let properties = [];
render = () => view([
    Text('pinned').frame(120, 40, 'leading'),
    Rect().frame(120, 1).background('#ffffff30')
]);
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
