// composite: bars computed from a data array and canvas size
let properties = [];
function render(ctx) {
    var data = [4, 9, 2, 7, 5];
    var barWidth = ctx.width / data.length;
    ctx.fillStyle = '#30d158';
    for (var i = 0; i < data.length; i++) {
        var barHeight = data[i] / 10 * ctx.height;
        ctx.fillRect(i * barWidth + 2, ctx.height - barHeight, barWidth - 4, barHeight);
    }
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
