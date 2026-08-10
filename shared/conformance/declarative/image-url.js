// Image by URL
let properties = [];
render = () => view([
    Image('https://example.com/logo.png').frame(40, 40).cornerRadius(8)
]);
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
