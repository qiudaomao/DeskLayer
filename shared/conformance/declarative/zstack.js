// ZStack layering a Rect under a Text
let properties = [];
render = () => view([
    ZStack([
        Rect().frame(100, 100).background('#000000'),
        Text('on top').textColor('#ffffff')
    ])
]);
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
