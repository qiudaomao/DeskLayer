// Ring(to): from defaults to 0
let properties = [];
render = () => view([
    Ring(0.75).lineWidth(6).ringColor('#30d158').trackColor('#3a3a3c').frame(48, 48)
]);
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
