// TextField with value + onChange action
let properties = [];
let name = '';
render = () => view([
    TextField('Your name').value(name).onChange(function (e) { name = e.text; })
]);
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
