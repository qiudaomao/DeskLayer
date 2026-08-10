// Spike-only animated plugin: Date-driven second hand + frame counter.
// (Conformance fixtures must be deterministic; this one deliberately isn't —
// it exists to prove per-frame V8→D2D rendering is live and cheap.)
let frames = 0;
function render(ctx) {
    frames++;
    var now = new Date();
    var s = now.getSeconds() + now.getMilliseconds() / 1000;

    ctx.fillStyle = '#101418';
    ctx.fillRect(0, 0, 300, 300);

    ctx.strokeStyle = '#ffffff';
    ctx.lineWidth = 3;
    ctx.beginPath();
    ctx.arc(150, 140, 100, 0, Math.PI * 2, false);
    ctx.stroke();

    ctx.save();
    ctx.translate(150, 140);
    ctx.rotate(s / 60 * Math.PI * 2);
    ctx.strokeStyle = '#ff453a';
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(0, 10);
    ctx.lineTo(0, -90);
    ctx.stroke();
    ctx.restore();

    ctx.fillStyle = '#30d158';
    ctx.font = 'bold 16px Segoe UI';
    ctx.fillText('V8 + D2D  frame ' + frames, 55, 285);
}
plugin.export = {
    version: "1.0.0",
    width: 300, height: 300,
    properties: [{ "name": "fps", "valueType": "number", "value": "60" }],
    render
};
