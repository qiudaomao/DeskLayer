// every CSS color form the contract accepts
let properties = [];
function render(ctx) {
    var colors = ['#f00', '#00ff00', '#0000ff80', 'rgb(255, 149, 0)',
                  'rgba(88, 86, 214, 0.5)', 'red'];
    for (var i = 0; i < colors.length; i++) {
        ctx.fillStyle = colors[i];
        ctx.fillRect(i * 30, 0, 28, 28);
    }
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
