//
//  SamplePlugins.swift
//  DeskLayer
//
//  Bundled sample plugins, written to the user's Plugins folder on first
//  launch (as Swift string constants rather than bundle resources so the
//  synchronized-group resource phase can't silently drop them).
//

import Foundation

nonisolated enum SamplePlugins {
    /// Samples are canonical: (re)written whenever the bundled source
    /// differs. Users who want to hack on one should duplicate it under a
    /// new name — the folder watcher picks the copy up as its own plugin.
    static func installIfMissing(into directory: URL) {
        for (name, source) in all {
            let url = directory.appendingPathComponent("\(name).js")
            if let existing = try? String(contentsOf: url, encoding: .utf8), existing == source {
                continue
            }
            try? source.write(to: url, atomically: true, encoding: .utf8)
        }
    }

    static let all: [String: String] = [
        "AnalogClock": analogClock,
        "Particles": particles,
        "FetchDemo": fetchDemo,
        "WebSocketDemo": webSocketDemo,
        "HelloCard": helloCard,
        "WeatherCard": weatherCard,
        "SystemMonitor": systemMonitor,
        "HookBoard": hookBoard,
    ]

    // Live CPU / RAM / disk / network gauges via the native $system API —
    // no shell, no permissions. Declarative, updates once a second.
    static let systemMonitor = #"""
    let properties = [
        {"name": "interval", "valueType": "number", "value": "1"},
        {"name": "accent", "valueType": "color", "value": "#4CD964FF"}
    ];

    let lastNet = null;

    function fmtBytes(n) {
        const u = ['B', 'KB', 'MB', 'GB', 'TB'];
        let i = 0;
        while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
        return n.toFixed(1) + ' ' + u[i];
    }

    function bar(label, value, detail, accent) {
        const pct = Math.round(value * 100);
        return HStack([
            Text(label).fontSize(11).textColor('#FFFFFF99').frame(64, 16),
            Text(pct + '%').fontSize(12).bold().textColor(accent).frame(44, 16),
            Text(detail).fontSize(11).textColor('#FFFFFFCC')
        ]).spacing(6);
    }

    render = () => {
        const s = $system.stats();
        const accent = String(properties.find(p => p.name === 'accent').value);
        const memUsed = s.memory.used / s.memory.total;
        const diskUsed = (s.disk.total - s.disk.free) / s.disk.total;

        let netRate = '—';
        if (lastNet) {
            const dt = Math.max(s.time - lastNet.time, 0.001);
            const down = (s.network.rxBytes - lastNet.rxBytes) / dt;
            const up = (s.network.txBytes - lastNet.txBytes) / dt;
            netRate = '↓' + fmtBytes(down) + '/s  ↑' + fmtBytes(up) + '/s';
        }
        lastNet = { time: s.time, rxBytes: s.network.rxBytes, txBytes: s.network.txBytes };

        return view([
            VStack([
                Text('System').fontSize(13).bold().textColor('white'),
                bar('CPU', s.cpu, s.cores + ' cores', accent),
                bar('Memory', memUsed, fmtBytes(s.memory.used) + ' / ' + fmtBytes(s.memory.total), accent),
                bar('Disk', diskUsed, fmtBytes(s.disk.free) + ' free', accent),
                HStack([
                    Text('Net').fontSize(11).textColor('#FFFFFF99').frame(64, 16),
                    Text(netRate).fontSize(11).textColor('#FFFFFFCC')
                ]).spacing(6)
            ]).spacing(6).padding(14).background('#0C0E16E6').cornerRadius(14)
        ]);
    };

    plugin.export = { properties, render };
    """#

    // Local hook receiver. The APP listens on 127.0.0.1:8787 and fans each
    // request out to every plugin that registered a handler; this one shows
    // the last few events. Point a Claude/Codex hook at:
    //   curl -s -X POST -d '{"tool":"Bash"}' http://127.0.0.1:8787
    // Requires the "server" permission.
    static let hookBoard = #"""
    let properties = [
        {"name": "interval", "valueType": "number", "value": "1"}
    ];

    let events = [];

    $server.on('POST', (event, body) => {
        let label = body;
        try { const j = JSON.parse(body); label = j.tool || j.event || j.type || body; } catch (e) {}
        events.unshift({ at: new Date().toLocaleTimeString(), text: String(label).slice(0, 40) });
        events = events.slice(0, 6);
        console.log('hook ' + event.method + ' ' + event.path + ': ' + label);
    });
    console.log('registered POST handler (app listens on 127.0.0.1:8787)');

    render = () => {
        const rows = events.length
            ? events.map(e => HStack([
                Text(e.at).fontSize(10).textColor('#FFFFFF66').frame(70, 14),
                Text(e.text).fontSize(12).textColor('white')
              ]).spacing(4))
            : [Text('waiting for POST to :8787…').fontSize(12).textColor('#FFFFFF88')];
        return view([
            VStack([
                HStack([
                    Image('antenna.radiowaves.left.and.right').fontSize(13).textColor('#4CD964FF'),
                    Text('Hooks :8787').fontSize(13).bold().textColor('white')
                ]).spacing(6)
            ].concat(rows)).spacing(4).padding(14).background('#0C0E16E6').cornerRadius(14)
        ]);
    };

    plugin.export = { permissions: ['server'], properties, render };
    """#

    static let analogClock = #"""
    let properties = [
        {"name": "fps", "valueType": "number", "value": "30"},
        {"name": "faceColor", "valueType": "color", "value": "#12141ED9"},
        {"name": "label", "valueType": "string", "value": ""}
    ];

    function render(ctx) {
        const w = ctx.width, h = ctx.height;
        const cx = w / 2, cy = h / 2;
        const r = Math.min(w, h) / 2 - 10;

        ctx.clearRect(0, 0, w, h);

        ctx.beginPath();
        ctx.arc(cx, cy, r, 0, Math.PI * 2, false);
        ctx.fillStyle = ctx.getProp('faceColor') || 'rgba(18,20,30,0.85)';
        ctx.fill();
        ctx.lineWidth = 4;
        ctx.strokeStyle = 'rgba(255,255,255,0.9)';
        ctx.beginPath();
        ctx.arc(cx, cy, r, 0, Math.PI * 2, false);
        ctx.stroke();

        ctx.strokeStyle = 'rgba(255,255,255,0.7)';
        ctx.lineWidth = 3;
        for (let i = 0; i < 12; i++) {
            const a = i * Math.PI / 6;
            ctx.beginPath();
            ctx.moveTo(cx + Math.cos(a) * (r - 14), cy + Math.sin(a) * (r - 14));
            ctx.lineTo(cx + Math.cos(a) * (r - 4), cy + Math.sin(a) * (r - 4));
            ctx.stroke();
        }

        const now = new Date();
        const sec = now.getSeconds() + now.getMilliseconds() / 1000;
        const min = now.getMinutes() + sec / 60;
        const hr = (now.getHours() % 12) + min / 60;

        function hand(angle, length, width, style) {
            ctx.save();
            ctx.translate(cx, cy);
            ctx.rotate(angle - Math.PI / 2);
            ctx.strokeStyle = style;
            ctx.lineWidth = width;
            ctx.lineCap = 'round';
            ctx.beginPath();
            ctx.moveTo(-length * 0.15, 0);
            ctx.lineTo(length, 0);
            ctx.stroke();
            ctx.restore();
        }

        hand(hr * Math.PI / 6, r * 0.5, 6, 'white');
        hand(min * Math.PI / 30, r * 0.75, 4, 'white');
        hand(sec * Math.PI / 30, r * 0.85, 2, 'red');

        ctx.beginPath();
        ctx.arc(cx, cy, 5, 0, Math.PI * 2, false);
        ctx.fillStyle = 'red';
        ctx.fill();

        ctx.font = 'bold 16px Helvetica';
        ctx.fillStyle = 'rgba(255,255,255,0.8)';
        const label = now.toLocaleTimeString();
        const m = ctx.measureText(label);
        ctx.fillText(label, cx - m.width / 2, cy + r * 0.55);

        const custom = String(ctx.getProp('label') || '');
        if (custom) {
            ctx.font = '12px Helvetica';
            ctx.fillStyle = 'rgba(255,220,120,0.9)';
            const cm = ctx.measureText(custom);
            ctx.fillText(custom, cx - cm.width / 2, cy - r * 0.35);
        }
    }

    plugin.export = { properties, render };
    """#

    static let particles = #"""
    let properties = [
        {"name": "fps", "valueType": "number", "value": "60"},
        {"name": "count", "valueType": "number", "value": "200"},
        {"name": "trail", "valueType": "color", "value": "#0A0C1459"}
    ];

    const COLORS = ['#5ac8fa', '#ff9500', '#ff2d55', '#4cd964', '#ffcc00', '#af52de'];
    let parts = null;
    let last = 0;

    function init(w, h) {
        const n = Number(ctxCount()) || 200;
        parts = [];
        for (let i = 0; i < n; i++) {
            parts.push({
                x: Math.random() * w,
                y: Math.random() * h,
                vx: (Math.random() - 0.5) * 160,
                vy: (Math.random() - 0.5) * 160,
                r: 2 + Math.random() * 4,
                c: COLORS[i % COLORS.length]
            });
        }
    }

    let ctxCount = function () { return 200; };

    function render(ctx) {
        ctxCount = function () { return ctx.getProp('count'); };
        const w = ctx.width, h = ctx.height;
        if (!parts) init(w, h);
        const t = Date.now() / 1000;
        const dt = last ? Math.min(t - last, 0.1) : 1 / 60;
        last = t;

        ctx.fillStyle = ctx.getProp('trail') || 'rgba(10,12,20,0.35)';
        ctx.fillRect(0, 0, w, h);

        for (const p of parts) {
            p.x += p.vx * dt;
            p.y += p.vy * dt;
            if (p.x < p.r) { p.x = p.r; p.vx = -p.vx; }
            if (p.x > w - p.r) { p.x = w - p.r; p.vx = -p.vx; }
            if (p.y < p.r) { p.y = p.r; p.vy = -p.vy; }
            if (p.y > h - p.r) { p.y = h - p.r; p.vy = -p.vy; }
            ctx.beginPath();
            ctx.arc(p.x, p.y, p.r, 0, 6.28318530718, false);
            ctx.fillStyle = p.c;
            ctx.fill();
        }
    }

    plugin.export = { properties, render };
    """#

    // Declarative mode: render() takes no ctx and returns a view tree,
    // rendered as native SwiftUI. No fps → re-renders only on property edits.
    static let helloCard = #"""
    let properties = [
        {"name": "title", "valueType": "string", "value": "Hello, World!"},
        {"name": "subtitle", "valueType": "string", "value": "rendered as native SwiftUI"},
        {"name": "accent", "valueType": "color", "value": "#4CD964FF"}
    ];

    const prop = name => String(properties.find(p => p.name === name).value);

    render = () => view([
        Section([
            Paragraph(prop('title'))
                .textColor(prop('accent'))
                .fontSize(28)
                .bold(),
            Paragraph(prop('subtitle'))
                .textColor('#FFFFFFAA')
                .fontSize(13)
        ])
        .spacing(6)
        .padding(18)
        .background('#101420CC')
        .cornerRadius(14)
    ]);

    plugin.export = { properties, render };
    """#

    // Declarative with fps: the tree re-renders each second; unchanged
    // trees are skipped by equality on the Swift side.
    static let weatherCard = #"""
    let properties = [
        {"name": "fps", "valueType": "number", "value": "1"},
        {"name": "city", "valueType": "string", "value": "Cupertino"},
        {"name": "temp", "valueType": "string", "value": "72°F"}
    ];

    const prop = name => String(properties.find(p => p.name === name).value);

    render = () => view([
        HStack([
            Image('sun.max.fill').fontSize(30).textColor('#FFCC00FF'),
            VStack([
                Text(prop('temp') + '  ' + prop('city')).fontSize(18).bold().textColor('white'),
                Text(new Date().toLocaleTimeString()).fontSize(12).textColor('#FFFFFF99')
            ]).spacing(2)
        ])
        .spacing(12)
        .padding(14)
        .background('#0A1E32D9')
        .cornerRadius(12)
    ]);

    plugin.export = { properties, render };
    """#

    static let fetchDemo = #"""
    let properties = [
        {"name": "interval", "valueType": "number", "value": "5"},
        {"name": "url", "valueType": "string", "value": "https://api.github.com/zen"},
        {"name": "refreshSeconds", "valueType": "number", "value": "60"}
    ];

    let text = 'loading…';
    let status = '';
    let fetchedAt = '';

    function refresh() {
        console.log('fetching ' + properties.find(p => p.name === 'url').value);
        fetch(String(properties.find(p => p.name === 'url').value))
            .then(r => { status = 'HTTP ' + r.status; return r.text(); })
            .then(body => {
                text = body.slice(0, 80);
                fetchedAt = new Date().toLocaleTimeString();
                console.log(status + ': ' + text.slice(0, 40));
            })
            .catch(e => { text = 'error: ' + e.message; console.log('fetch failed: ' + e.message); });
    }

    refresh();
    setInterval(refresh, (Number(properties.find(p => p.name === 'refreshSeconds').value) || 60) * 1000);

    function render(ctx) {
        const w = ctx.width, h = ctx.height;
        ctx.clearRect(0, 0, w, h);
        ctx.fillStyle = 'rgba(15,17,26,0.85)';
        ctx.fillRect(0, 0, w, h);
        ctx.strokeStyle = 'rgba(255,255,255,0.25)';
        ctx.lineWidth = 1;
        ctx.strokeRect(0.5, 0.5, w - 1, h - 1);

        ctx.fillStyle = 'rgba(255,255,255,0.5)';
        ctx.font = '11px Helvetica';
        ctx.fillText('fetch demo · ' + status + ' · ' + fetchedAt, 12, 20);

        ctx.fillStyle = 'white';
        ctx.font = '14px Helvetica';
        // naive wrap
        const words = String(text).split(' ');
        let line = '', y = 46;
        for (const word of words) {
            const probe = line ? line + ' ' + word : word;
            if (ctx.measureText(probe).width > w - 24 && line) {
                ctx.fillText(line, 12, y);
                y += 20;
                line = word;
            } else {
                line = probe;
            }
        }
        if (line) ctx.fillText(line, 12, y);
    }

    plugin.export = { properties, render };
    """#

    static let webSocketDemo = #"""
    let properties = [
        {"name": "fps", "valueType": "number", "value": "2"},
        {"name": "url", "valueType": "string", "value": "wss://echo.websocket.org"}
    ];

    let state = 'connecting';
    let lastMessage = '';
    let sent = 0;
    let ws = null;

    function connect() {
        ws = new WebSocket(String(properties.find(p => p.name === 'url').value));
        ws.onopen = function () {
            state = 'open';
            ws.send('hello from DeskLayer');
            sent++;
        };
        ws.onmessage = function (e) { lastMessage = String(e.data).slice(0, 60); };
        ws.onclose = function (e) { state = 'closed(' + e.code + ')'; };
        ws.onerror = function (e) { state = 'error: ' + e.message; };
    }
    connect();
    setInterval(function () {
        if (ws && ws.readyState === 1) { ws.send('ping ' + new Date().toLocaleTimeString()); sent++; }
    }, 15000);

    function render(ctx) {
        const w = ctx.width, h = ctx.height;
        ctx.clearRect(0, 0, w, h);
        ctx.fillStyle = 'rgba(15,26,17,0.85)';
        ctx.fillRect(0, 0, w, h);
        ctx.strokeStyle = 'rgba(255,255,255,0.25)';
        ctx.lineWidth = 1;
        ctx.strokeRect(0.5, 0.5, w - 1, h - 1);

        ctx.fillStyle = 'rgba(255,255,255,0.5)';
        ctx.font = '11px Helvetica';
        ctx.fillText('websocket demo · ' + state + ' · sent ' + sent, 12, 20);

        ctx.fillStyle = 'white';
        ctx.font = '14px Helvetica';
        ctx.fillText(lastMessage || '(no message yet)', 12, 46);
    }

    plugin.export = { properties, render };
    """#
}
