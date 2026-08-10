// rotate + non-uniform scale about the center
let properties = [];
function render(ctx) {
    ctx.fillStyle = '#007aff';
    ctx.save();
    ctx.translate(100, 50);
    ctx.rotate(Math.PI / 6);
    ctx.scale(2, 0.5);
    ctx.fillRect(-20, -20, 40, 40);
    ctx.restore();
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
