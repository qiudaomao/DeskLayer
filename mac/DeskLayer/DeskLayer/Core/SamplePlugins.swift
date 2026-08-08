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
    /// Plugins the app maintains — always present, not removable.
    static let builtinNames: Set<String> = ["AnalogClock", "SystemMonitor", "RemoteMonitor"]

    /// Everything else bundled is an example the user may uninstall.
    static func origin(of name: String) -> PluginOrigin {
        if builtinNames.contains(name) { return .builtin }
        return all[name] != nil ? .example : .user
    }

    /// Samples are canonical: (re)written whenever the bundled source
    /// differs. Users who want to hack on one should duplicate it under a
    /// new name — the folder watcher picks the copy up as its own plugin.
    /// An uninstalled example stays gone (see PluginRegistry.uninstall).
    static func installIfMissing(into directory: URL, skipping removed: Set<String> = []) {
        for (name, source) in all {
            guard !removed.contains(name) else { continue }
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
        "RemoteMonitor": remoteMonitor,
        "WebPage": webPage,
        "Counter": counter,
    ]

    // Interactive declarative plugin (floating window only — the wallpaper
    // layer ignores mouse events). Demonstrates Button, TextField, a
    // ProgressBar, and tap callbacks.
    static let counter = #"""
    let properties = [];
    let count = 0;
    let name = "friend";

    render = () => view([
        VStack([
            Text("Hi, " + name + "!").fontSize(16).bold().textColor("white"),
            TextField("your name", (e) => { name = e.text || "friend"; }).value(name),
            HStack([
                Button("−", () => { count = Math.max(0, count - 1); }).textColor("#FF453A"),
                Text(String(count)).fontSize(22).bold().textColor("white").frame(48, 28),
                Button("+", () => { count += 1; }).textColor("#32D74B")
            ]).spacing(10),
            ProgressBar(Math.min(count / 10, 1)),
            Text("tap the card").fontSize(10).textColor("#FFFFFF66")
                .onTapGesture((e) => { count += 1; })
        ]).spacing(10).padding(16).background("#0C0E16E6").cornerRadius(14)
    ]);

    plugin.export = {
        version: "1.0.0",
        author: "DeskLayer",
        description: "Interactive counter — buttons, a text field, and tap callbacks. Use as a floating window.",
        width: 260, height: 220,
        resizable: false,   // fixed-size card: SwiftUI lays it out at its natural size
        properties,
        render
    };
    """#

    // Webview plugin: renders a live web page. url / offsetX / offsetY / zoom
    // are inspector-editable; user-agent, headers, and cookies come from the
    // static `webview` config. offsetX/Y scroll to show a region of the page.
    static let webPage = #"""
    let properties = [
        {"name": "url", "valueType": "string", "value": "https://example.com"},
        {"name": "offsetX", "valueType": "number", "value": "0"},
        {"name": "offsetY", "valueType": "number", "value": "0"},
        {"name": "zoom", "valueType": "number", "value": "1"}
    ];

    plugin.export = {
        mode: "webview",
        version: "1.0.0",
        author: "DeskLayer",
        description: "Shows a web page. Edit URL, scroll offset, and zoom in the inspector.",
        properties,
        webview: {
            // userAgent: "Mozilla/5.0 …",
            // headers: { "X-Example": "1" },
            // cookies: [{ name: "session", value: "…", domain: "example.com", path: "/" }]
        }
    };
    """#

    // Remote machine monitor over ssh(). Configure the destination in the
    // inspector's SSH section; unconfigured, ssh() rejects with a message
    // this plugin displays. Needs the "ssh" permission.
    static let remoteMonitor = #"""
    let properties = [
        {"name": "interval", "valueType": "number", "value": "2"},
        {"name": "title", "valueType": "string", "value": "remote"},
        {"name": "accent", "valueType": "color", "value": "#34C759FF"},
        {"name": "warnColor", "valueType": "color", "value": "#FF9F0AFF"}
    ];

    const prop = n => String(properties.find(p => p.name === n).value);

    // One entry per configured server: { stats, prev, status }.
    const servers = {};

    // Portable probe: works on Linux (/proc) and macOS (sysctl/vm_stat/
    // netstat/iostat). Emits the same "KEY value…" lines either way.
    // printf "%.0f" everywhere: awk's default %g prints big counters as
    // 9.9e+11, and the lost precision would wreck the rate diffs.
    const PROBE = [
        'sh', '-c',
        'if [ -r /proc/loadavg ]; then ' +
            'echo "L $(cut -d\" \" -f1 /proc/loadavg) $(nproc)"; ' +
            // M used total cached free
            'free -k | awk \'/Mem:/{printf "M %.0f %.0f %.0f %.0f\\n", $3, $2, $6, $4}\'; ' +
            'echo "T $(cat /sys/class/thermal/thermal_zone*/temp 2>/dev/null | head -1 || echo 0)"; ' +
            'awk \'$1 ~ /^(eth|ens|enp|eno|wl)/ {gsub(":"," ");rx+=$2;tx+=$10} END{printf "N %.0f %.0f\\n", rx, tx}\' /proc/net/dev; ' +
            'awk \'$3 ~ /^(sd|nvme|vd|hd)/ {r+=$6;w+=$10} END{printf "D %.0f %.0f\\n", r*512, w*512}\' /proc/diskstats; ' +
        'else ' +
            'echo "L $(sysctl -n vm.loadavg | awk \'{print $2}\') $(sysctl -n hw.ncpu)"; ' +
            // active+wired+compressed = used, inactive ≈ cached, free = free
            'vm_stat | awk -v p=$(sysctl -n hw.pagesize) -v t=$(sysctl -n hw.memsize) ' +
                '\'/Pages free/{f=$3} /Pages active/{a=$3} /Pages inactive/{i=$3} ' +
                '/Pages wired/{w=$4} /Pages occupied by compressor/{c=$5} ' +
                'END{gsub("\\\\.","",f);gsub("\\\\.","",a);gsub("\\\\.","",i);gsub("\\\\.","",w);gsub("\\\\.","",c); ' +
                'printf "M %.0f %.0f %.0f %.0f\\n", (a+w+c)*p/1024, t/1024, i*p/1024, f*p/1024}\'; ' +
            'echo "T 0"; ' +
            'netstat -ib | awk \'$1 ~ /^en/ && $4 !~ /:/ {rx+=$7; tx+=$10} END{printf "N %.0f %.0f\\n", rx, tx}\'; ' +
            // macOS iostat reports combined throughput only; -1 marks the
            // write column unavailable so the UI shows a single io/s figure.
            'iostat -Id disk0 2>/dev/null | awk \'NR==3{printf "D %.0f -1\\n", $3*1048576}\' || echo "D 0 -1"; ' +
        'fi'
    ];

    function refreshOne(name) {
        ssh(PROBE, name).then(r => {
            const e = servers[name] || (servers[name] = {});
            if (r.status !== 0) {
                e.status = 'exit ' + r.status + ' ' + (r.stderr || '').trim().slice(0, 36);
                return;
            }
            const s = { time: Date.now() / 1000 };
            r.stdout.trim().split('\n').forEach(line => {
                const f = line.trim().split(/\s+/);
                if (f[0] === 'L') { s.load = parseFloat(f[1]) || 0; s.cores = parseInt(f[2]) || 1; }
                else if (f[0] === 'M') {
                    s.memUsed = +f[1] || 0; s.memTotal = +f[2] || 1;
                    s.memCached = +f[3] || 0; s.memFree = +f[4] || 0;
                }
                else if (f[0] === 'T') { s.temp = (+f[1] || 0) / 1000; }
                else if (f[0] === 'N') { s.rx = +f[1] || 0; s.tx = +f[2] || 0; }
                else if (f[0] === 'D') { s.dr = +f[1] || 0; s.dw = +f[2] || 0; }
            });
            e.prev = e.stats; e.stats = s; e.status = 'ok';
        }).catch(err => {
            const e = servers[name] || (servers[name] = {});
            e.status = err.message;
        });
    }

    function refresh() {
        const names = ($ssh.hosts && $ssh.hosts.length) ? $ssh.hosts : [];
        if (!names.length) { servers['—'] = { status: 'no SSH destination configured' }; return; }
        names.forEach(refreshOne);
    }
    setTimeout(refresh, 0);              // host APIs are ready after load
    setInterval(refresh, 2000);

    // 102 K / 1.2 M — value and unit split so they can be styled separately.
    function rate(bytesPerSec) {
        const u = ['', 'K', 'M', 'G'];
        let n = bytesPerSec || 0, i = 0;
        while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
        return { n: n >= 100 ? String(Math.round(n)) : n.toFixed(n >= 10 ? 0 : 1), u: u[i] };
    }

    function perSec(e, key) {
        if (!e.prev || !e.stats) return 0;
        const dt = Math.max(e.stats.time - e.prev.time, 0.001);
        return Math.max(0, (e.stats[key] - e.prev[key]) / dt);
    }

    // Big green number + small unit, with a caption underneath.
    function stat(value, caption, accent) {
        const v = rate(value);
        return VStack([
            HStack([
                Text(v.n).fontSize(26).bold().textColor(accent),
                Text(v.u).fontSize(13).textColor('#FFFFFF99')
            ]).spacing(3),
            Text(caption).fontSize(11).textColor('#FFFFFF99')
        ]).spacing(0).frame(78, 52, 'center');
    }

    function gauge(inner, caption) {
        return VStack([ inner.frame(52, 52), Text(caption).fontSize(11).textColor('#FFFFFF99') ]).spacing(4);
    }

    // Segmented memory ring from Ring(from, to) arcs:
    //   used = green (accent), cached = gray, free = the dim remainder.
    function memoryRing(s, accent, warn) {
        const total = s.memTotal || 1;
        const used = Math.min(Math.max(s.memUsed / total, 0), 1);
        const cached = Math.min(Math.max((s.memCached || 0) / total, 0), 1 - used);
        return ZStack([
            Ring(0, 1).lineWidth(6).ringColor('#FFFFFF14'),                  // free
            Ring(0, used).lineWidth(6).ringColor(used > 0.9 ? warn : accent), // used
            Ring(used, used + cached).lineWidth(6).ringColor('#C7C7CCCC'),   // cached
            Text(Math.round(used * 100) + '%').fontSize(12).textColor('#FFFFFFCC')
        ]);
    }

    // One server block: header + gauges + rates.
    function serverView(name, e, accent, warn) {
        if (!e || e.status !== 'ok' || !e.stats) {
            return VStack([
                HStack([ Text(name).fontSize(15).bold().textColor('white'), Spacer() ]),
                Text((e && e.status) || 'connecting…').fontSize(11).textColor(warn).lineLimit(2)
            ]).spacing(3);
        }
        const s = e.stats;
        const loadFrac = Math.min(s.load / s.cores, 1);
        const memFrac = s.memUsed / s.memTotal;

        return VStack([
            HStack([
                Text(name).fontSize(15).bold().textColor('white'),
                Spacer(),
                Text(s.temp > 0 ? Math.round(s.temp) + '°C' : '').fontSize(13).textColor('#FFFFFF99')
            ]),
            HStack([
                // Load: outer ring = 1-min load per core.
                gauge(ZStack([
                    Ring(loadFrac).lineWidth(6)
                        .ringColor(loadFrac > 0.8 ? warn : accent).trackColor('#FFFFFF1A'),
                    Ring(Math.min(s.load / (s.cores * 2), 1)).lineWidth(6)
                        .ringColor(accent).trackColor('#00000000').frame(28, 28)
                ]), 'load'),
                // Memory ring, segmented in JS: used, then cached (light
                // gray), then free (green) — stacked Ring(from, to) arcs.
                gauge(memoryRing(s, accent, warn), 'memory'),
                Spacer(),
                VStack([ stat(perSec(e, 'tx'), '↑/s', accent), stat(perSec(e, 'rx'), '↓/s', accent) ]).spacing(0),
                // macOS reports combined disk throughput (dw === -1).
                s.dw < 0
                    ? VStack([ stat(perSec(e, 'dr'), 'io/s', accent) ]).spacing(0)
                    : VStack([ stat(perSec(e, 'dr'), 'read/s', accent), stat(perSec(e, 'dw'), 'write/s', accent) ]).spacing(0)
            ]).spacing(12)
        ]).spacing(4);
    }

    render = () => {
        const accent = prop('accent');
        const warn = prop('warnColor');
        const names = ($ssh.hosts && $ssh.hosts.length) ? $ssh.hosts : Object.keys(servers);
        const blocks = [];
        names.forEach((n, i) => {
            // null width = flexible, so the rule spans the card without
            // forcing the stack wider than the item.
            if (i > 0) blocks.push(Rect().frame(null, 1).background('#FFFFFF14'));
            blocks.push(serverView(n, servers[n], accent, warn));
        });
        if (!blocks.length) {
            blocks.push(Text('no SSH destination configured').fontSize(12).textColor(warn));
        }
        return view([
            VStack(blocks).spacing(10).padding(16).background('#141414F2').cornerRadius(16)
        ]);
    };

    plugin.export = {
        version: "1.1.0",
        author: "DeskLayer",
        description: "Remote host dashboard over SSH: load and memory rings, network and disk I/O rates.",
        width: 430, height: 190,
        // Height follows the number of servers; width stays whatever you set.
        scaleMode: "free",
        autoSize: "height",
        minWidth: 340, maxWidth: 760,
        permissions: ['ssh'],
        properties,
        render
    };
    """#

    // Live CPU / RAM / disk / network gauges via the native $system API —
    // no shell, no permissions. Declarative, updates once a second.
    static let systemMonitor = #"""
    let properties = [
        {"name": "interval", "valueType": "number", "value": "1"},
        {"name": "accent", "valueType": "color", "value": "#4CD964FF"}
    ];

    const LABEL_W = 54;   // aligned label column
    const PCT_W = 38;     // aligned percentage column
    const BAR_W = 66;     // aligned bar column
    let lastNet = null;

    // Bar drawn from Rects so it matches the accent color exactly (and
    // renders identically on the wallpaper, in widgets, and in snapshots).
    function bar(fraction, accent) {
        const filled = Math.max(2, Math.min(BAR_W, BAR_W * fraction));
        return ZStack([
            HStack([
                Rect().frame(BAR_W, 5).background('#FFFFFF26').cornerRadius(2.5),
                Spacer()
            ]),
            HStack([
                Rect().frame(filled, 5).background(accent).cornerRadius(2.5),
                Spacer()
            ])
        ]).frame(BAR_W, 5, 'leading');
    }

    // Compact: 21.1G rather than "21.1 GB", so rows never wrap.
    function fmt(n) {
        const u = ['B', 'K', 'M', 'G', 'T'];
        let i = 0;
        while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
        return (n >= 100 ? n.toFixed(0) : n.toFixed(1)) + u[i];
    }

    // label | percent | bar | detail — every column fixed so rows line up.
    function metric(label, fraction, detail, accent) {
        return HStack([
            Text(label).fontSize(11).textColor('#FFFFFF99').frame(LABEL_W, 14, 'leading'),
            Text(Math.round(fraction * 100) + '%').fontSize(12).bold()
                .textColor(accent).frame(PCT_W, 14, 'trailing'),
            bar(fraction, accent),
            Text(detail).fontSize(10).textColor('#FFFFFF99').lineLimit(1),
            Spacer()
        ]).spacing(8);
    }

    render = () => {
        const s = $system.stats();
        const accent = String(properties.find(p => p.name === 'accent').value);
        const memUsed = s.memory.used / s.memory.total;
        const diskUsed = (s.disk.total - s.disk.free) / s.disk.total;

        let down = 0, up = 0;
        if (lastNet) {
            const dt = Math.max(s.time - lastNet.time, 0.001);
            down = (s.network.rxBytes - lastNet.rxBytes) / dt;
            up = (s.network.txBytes - lastNet.txBytes) / dt;
        }
        lastNet = { time: s.time, rxBytes: s.network.rxBytes, txBytes: s.network.txBytes };

        return view([
            VStack([
                HStack([
                    Image('gauge.medium').fontSize(12).textColor(accent),
                    Text('System').fontSize(13).bold().textColor('white'),
                    Spacer()
                ]).spacing(6),

                metric('CPU', s.cpu, s.cores + ' cores', accent),
                metric('Memory', memUsed, fmt(s.memory.used) + '/' + fmt(s.memory.total), accent),
                metric('Disk', diskUsed, fmt(s.disk.free) + ' free', accent),

                // Net has no percentage — keep the same column grid so it
                // still lines up with the rows above.
                HStack([
                    Text('Net').fontSize(11).textColor('#FFFFFF99').frame(LABEL_W, 14, 'leading'),
                    Text('↓ ' + fmt(down) + '/s').fontSize(11).textColor('#FFFFFFCC').lineLimit(1),
                    Text('↑ ' + fmt(up) + '/s').fontSize(11).textColor('#FFFFFFCC').lineLimit(1),
                    Spacer()
                ]).spacing(8)
            ]).spacing(7).padding(14).background('#0C0E16E6').cornerRadius(14)
        ]);
    };

    plugin.export = {
        version: "1.1.0",
        author: "DeskLayer",
        description: "Live CPU, memory, disk, and network gauges.",
        width: 320, height: 170,
        scaleMode: "free",         // rows reflow, so width and height are independent
        minWidth: 260, maxWidth: 640, minHeight: 120, maxHeight: 360,
        properties,
        render
    };
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

    plugin.export = {
        version: "1.0.0",
        author: "DeskLayer",
        description: "An analog clock with a custom face color and label.",
        width: 260, height: 260,   // square: rect matches the round face
        scaleMode: "ratio",        // stays circular when resized
        minWidth: 120, maxWidth: 600, minHeight: 120, maxHeight: 600,
        properties,
        render
    };
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
