// Video with loop and muted modifiers
let properties = [];
render = () => view([
    Video('https://example.com/loop.mp4').loop(true).muted(true).frame(160, 90)
]);
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
