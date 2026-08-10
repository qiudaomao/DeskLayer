// determinate progress bar
let properties = [];
render = () => view([ProgressBar(0.42).frame(120, 6)]);
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
