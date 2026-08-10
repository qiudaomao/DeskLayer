// Ring(from, to) segments stacked into a segmented ring
let properties = [];
render = () => view([
    ZStack([
        Ring(0, 0.3).ringColor('#ff453a').lineWidth(5).frame(60, 60),
        Ring(0.35, 0.6).ringColor('#ffd60a').lineWidth(5).frame(60, 60),
        Ring(0.65, 0.9).ringColor('#30d158').lineWidth(5).frame(60, 60)
    ])
]);
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
