// fillStyle set on frame 0 persists into frame 1; per-frame drift in args
let properties = [];
function render(ctx) {
    if (typeof frameCount === 'undefined') { frameCount = 0; }
    if (frameCount === 0) {
        ctx.fillStyle = '#ff9500';
    }
    ctx.fillRect(frameCount * 10, 0, 10, 10);
    frameCount++;
}
plugin.export = { version: "1.0.0", width: 200, height: 100, properties, render };
