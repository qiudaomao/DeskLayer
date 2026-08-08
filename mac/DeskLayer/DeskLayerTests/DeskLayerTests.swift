//
//  DeskLayerTests.swift
//  DeskLayerTests
//

import Testing
import CoreGraphics
import DeskLayerKit
import Foundation
import ImageIO
import IOSurface
import JavaScriptCore
@testable import DeskLayer

struct PropertyValueTests {
    @Test func coercesNumberFromString() {
        // PLAN.md declares {"valueType": "number", "value": "30"} — string JSON.
        #expect(PropertyValue.coerce("30", valueType: "number") == .number(30))
        #expect(PropertyValue.coerce(30 as NSNumber, valueType: "number") == .number(30))
        #expect(PropertyValue.coerce("abc", valueType: "number") == nil)
    }

    @Test func coercesBoolFromStringAndNumber() {
        #expect(PropertyValue.coerce("true", valueType: "boolean") == .bool(true))
        #expect(PropertyValue.coerce("0", valueType: "boolean") == .bool(false))
        #expect(PropertyValue.coerce(1 as NSNumber, valueType: "bool") == .bool(true))
    }

    @Test func coercesStringFromNumber() {
        #expect(PropertyValue.coerce(42 as NSNumber, valueType: "string") == .string("42"))
    }

    @Test func codableRoundTrip() throws {
        let values: [PropertyValue] = [.string("a"), .number(1.5), .bool(true), .color("#ff0000aa")]
        let data = try JSONEncoder().encode(values)
        let decoded = try JSONDecoder().decode([PropertyValue].self, from: data)
        #expect(decoded == values)
    }
}

struct LayoutItemTests {
    @Test func decodesLayoutsWrittenBeforeClickThroughExisted() throws {
        // A hand-editable file must never be invalidated by an app update.
        let json = """
        {"id": "11111111-1111-1111-1111-111111111111", "pluginID": "AnalogClock",
         "displayUUID": "X", "normalizedFrame": [[0.1, 0.2], [0.3, 0.4]],
         "target": "floatingWindow", "propertyOverrides": {}, "isEnabled": true, "zOrder": 1}
        """
        let item = try JSONDecoder().decode(LayoutItem.self, from: Data(json.utf8))
        #expect(item.clickThrough == false)
        #expect(item.target == .floatingWindow)

        let minimal = """
        {"id": "11111111-1111-1111-1111-111111111111", "pluginID": "P",
         "displayUUID": "X", "normalizedFrame": [[0, 0], [1, 1]]}
        """
        let sparse = try JSONDecoder().decode(LayoutItem.self, from: Data(minimal.utf8))
        #expect(sparse.isEnabled == true)
        #expect(sparse.target == .wallpaper)

        // Round trip keeps the new field.
        var withFlag = item
        withFlag.clickThrough = true
        let data = try JSONEncoder().encode(withFlag)
        let decoded = try JSONDecoder().decode(LayoutItem.self, from: data)
        #expect(decoded.clickThrough == true)
    }
}

struct CSSColorTests {
    private func components(_ s: String) -> [CGFloat]? {
        CSSColor.parse(s)?.components
    }

    @Test func parsesHexForms() {
        #expect(components("#fff") == [1, 1, 1, 1])
        #expect(components("#ff0000") == [1, 0, 0, 1])
        let rgba = components("#ff000080")
        #expect(rgba?[0] == 1)
        #expect(abs((rgba?[3] ?? 0) - 128.0 / 255.0) < 0.001)
    }

    @Test func parsesRGBFunctions() {
        #expect(components("rgb(255, 0, 0)") == [1, 0, 0, 1])
        let rgba = components("rgba(0, 255, 0, 0.5)")
        #expect(rgba?[1] == 1)
        #expect(rgba?[3] == 0.5)
    }

    @Test func parsesNamedAndRejectsJunk() {
        #expect(components("white") == [1, 1, 1, 1])
        #expect(components("WHITE") == [1, 1, 1, 1])
        #expect(CSSColor.parse("not-a-color") == nil)
        #expect(CSSColor.parse("#12") == nil)
    }
}

struct PluginInstanceTests {
    @Test func bootsAndParsesProperties() {
        let source = """
        let properties = [
            {"name": "fps", "valueType": "number", "value": "24"},
            {"name": "label", "valueType": "string", "value": "hi"}
        ];
        function render(ctx) {}
        plugin.export = { properties, render };
        """
        let instance = PluginInstance(pluginID: "t", source: source, overrides: [:])
        #expect(instance != nil)
        #expect(instance?.fps == 24)
        #expect(instance?.property(named: "label") == .string("hi"))
        instance?.invalidate()
    }

    @Test func overridesApplyOverDeclared() {
        let source = """
        let properties = [{"name": "fps", "valueType": "number", "value": "24"}];
        function render(ctx) {}
        plugin.export = { properties, render };
        """
        let instance = PluginInstance(pluginID: "t", source: source, overrides: ["fps": .number(5)])
        #expect(instance?.fps == 5)
        instance?.invalidate()
    }

    @Test func brokenPluginReturnsNilNotCrash() {
        #expect(PluginInstance(pluginID: "t", source: "syntax error here (", overrides: [:]) == nil)
        #expect(PluginInstance(pluginID: "t", source: "var x = 1;", overrides: [:]) == nil)
    }

    @Test func renderExceptionMarksErrored() {
        let source = """
        let properties = [];
        function render(ctx) { throw new Error('boom'); }
        plugin.export = { properties, render };
        """
        let instance = PluginInstance(pluginID: "t", source: source, overrides: [:])
        #expect(instance != nil)
        let ok = instance!.callRender(with: nil)
        #expect(ok == false)
        #expect(instance!.isErrored)
        instance?.invalidate()
    }

    @Test func timerFires() async throws {
        let source = """
        let properties = [];
        let fired = false;
        setTimeout(function () { fired = true; }, 50);
        function render(ctx) {}
        plugin.export = { properties, render, check: function () { return fired; } };
        """
        let instance = try #require(PluginInstance(pluginID: "t", source: source, overrides: [:]))
        try await Task.sleep(for: .milliseconds(400))
        let fired = await withCheckedContinuation { continuation in
            instance.queue.async {
                let value = instance.context
                    .objectForKeyedSubscript("plugin")?
                    .objectForKeyedSubscript("export")?
                    .objectForKeyedSubscript("check")?
                    .call(withArguments: [])
                continuation.resume(returning: value?.toBool() ?? false)
            }
        }
        #expect(fired)
        instance.invalidate()
    }

    @Test func applyOverrideReachesJS() async throws {
        // The inspector's edit path: applyOverride must update the Swift copy
        // (ctx.getProp source) AND the plugin's exported properties array.
        let source = """
        let properties = [{"name": "label", "valueType": "string", "value": "old"}];
        function render(ctx) {}
        plugin.export = { properties, render, read: function () { return properties[0].value; } };
        """
        let instance = try #require(PluginInstance(pluginID: "t", source: source, overrides: [:]))
        instance.applyOverride(name: "label", value: .string("new"))
        let jsSide = await withCheckedContinuation { continuation in
            instance.queue.async {
                let value = instance.context
                    .objectForKeyedSubscript("plugin")?
                    .objectForKeyedSubscript("export")?
                    .objectForKeyedSubscript("read")?
                    .call(withArguments: [])
                continuation.resume(returning: value?.toString() ?? "")
            }
        }
        #expect(jsSide == "new")
        #expect(instance.property(named: "label") == .string("new"))
        instance.invalidate()
    }

    @Test func declarativeModeDetectedByArity() throws {
        let declarative = """
        let properties = [];
        render = () => view([Text('hi')]);
        plugin.export = { properties, render };
        """
        let canvas = """
        let properties = [];
        function render(ctx) {}
        plugin.export = { properties, render };
        """
        let d = try #require(PluginInstance(pluginID: "d", source: declarative, overrides: [:]))
        let c = try #require(PluginInstance(pluginID: "c", source: canvas, overrides: [:]))
        #expect(d.renderMode == .declarative)
        #expect(d.hasDeclaredCadence == false)
        #expect(c.renderMode == .canvas)
        d.invalidate(); c.invalidate()
    }

    @Test func declarativeTreeBuildsWithAliasesAndModifiers() async throws {
        // The user's PLAN syntax: Section/Paragraph aliases, chained modifiers.
        let source = """
        let properties = [];
        render = () => view([
            Section([
                Paragraph('Hello, World!').textColor('green').fontSize(20).bold()
            ]).padding(8).background('#00000080').cornerRadius(12)
        ]);
        plugin.export = { properties, render };
        """
        let instance = try #require(PluginInstance(pluginID: "t", source: source, overrides: [:]))
        let json = await withCheckedContinuation { continuation in
            instance.queue.async { continuation.resume(returning: instance.callRenderTree()) }
        }
        let jsonString = try #require(json)
        let tree = try #require(ViewNode.decode(fromJSON: jsonString))
        #expect(tree.type == "Root")
        let section = try #require(tree.children?.first)
        #expect(section.type == "VStack") // Section alias
        #expect(section.modifiers?.map(\.name) == ["padding", "background", "cornerRadius"])
        let paragraph = try #require(section.children?.first)
        #expect(paragraph.type == "Text") // Paragraph alias
        #expect(paragraph.text == "Hello, World!")
        #expect(paragraph.modifiers?.map(\.name) == ["textColor", "fontSize", "bold"])
        #expect(paragraph.modifiers?[0].firstString == "green")
        #expect(paragraph.modifiers?[1].firstDouble == 20)

        // Same input → identical tree (the update-skip guarantee).
        let json2 = await withCheckedContinuation { continuation in
            instance.queue.async { continuation.resume(returning: instance.callRenderTree()) }
        }
        #expect(json == json2)
        instance.invalidate()
    }

    @Test func viewNodeDecodesUnknownTypesAndModifiers() throws {
        // Unknown content must decode fine (NodeView shows placeholders).
        let json = """
        {"type": "Marquee", "text": null, "modifiers": [{"name": "blink", "args": [3, "fast", true]}], "children": []}
        """
        let node = try #require(ViewNode.decode(fromJSON: json))
        #expect(node.type == "Marquee")
        #expect(node.modifiers?.first?.args.count == 3)
        #expect(node.modifiers?.first?.args[0].doubleValue == 3)
        #expect(node.modifiers?.first?.args[1].stringValue == "fast")
    }

    @Test func flagWedgedMarksErroredOnce() throws {
        let source = """
        let properties = [];
        function render(ctx) {}
        plugin.export = { properties, render };
        """
        let instance = try #require(PluginInstance(pluginID: "t", source: source, overrides: [:]))
        instance.flagWedged(after: 2.5)
        #expect(instance.isErrored)
        #expect(instance.errorMessage?.contains("watchdog") == true)
        instance.invalidate()
    }

    @Test func drawImageRendersFolderAsset() async throws {
        // Build a .deskplugin-style assets folder with a 2×2 red PNG.
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("desklayer-test-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        let bitmap = CGContext(
            data: nil, width: 2, height: 2, bitsPerComponent: 8, bytesPerRow: 0,
            space: space,
            bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue
        )!
        bitmap.setFillColor(CGColor(colorSpace: space, components: [1, 0, 0, 1])!)
        bitmap.fill(CGRect(x: 0, y: 0, width: 2, height: 2))
        let assetURL = dir.appendingPathComponent("dot.png")
        let destination = CGImageDestinationCreateWithURL(assetURL as CFURL, "public.png" as CFString, 1, nil)!
        CGImageDestinationAddImage(destination, bitmap.makeImage()!, nil)
        CGImageDestinationFinalize(destination)

        let source = """
        let properties = [];
        function render(ctx) {
            ctx.drawImage('dot.png', 0, 0, ctx.width, ctx.height);
        }
        plugin.export = { properties, render };
        """
        let instance = try #require(PluginInstance(pluginID: "t", source: source, overrides: [:]))
        let renderer = try #require(ItemRenderer(
            instance: instance, size: CGSize(width: 4, height: 4), scale: 1, assetsURL: dir
        ))
        let surface = await withCheckedContinuation { continuation in
            renderer.queue.async { continuation.resume(returning: renderer.renderFrame()) }
        }
        let rendered = try #require(surface)
        rendered.lock(options: [.readOnly], seed: nil)
        // BGRA: center pixel should be opaque red.
        let pixels = rendered.baseAddress.assumingMemoryBound(to: UInt8.self)
        let offset = 2 * rendered.bytesPerRow + 2 * 4
        #expect(pixels[offset + 2] == 255) // R
        #expect(pixels[offset + 1] == 0)   // G
        #expect(pixels[offset + 0] == 0)   // B
        rendered.unlock(options: [.readOnly], seed: nil)
        instance.invalidate()
    }

    @Test func renderCadenceFromFpsAndInterval() throws {
        func boot(_ props: String) -> PluginInstance? {
            PluginInstance(pluginID: "t", source: """
            let properties = [\(props)];
            function render(ctx) {}
            plugin.export = { properties, render };
            """, overrides: [:])
        }
        // fps fractions: 0.2 fps = every 5 seconds
        let slow = try #require(boot(#"{"name": "fps", "valueType": "number", "value": "0.2"}"#))
        #expect(abs(slow.renderInterval - 5) < 0.001)
        // interval in seconds beats everything: hourly renders
        let hourly = try #require(boot(#"{"name": "interval", "valueType": "number", "value": "3600"}"#))
        #expect(hourly.renderInterval == 3600)
        // fps 0 = render exactly once
        let once = try #require(boot(#"{"name": "fps", "valueType": "number", "value": "0"}"#))
        #expect(once.renderInterval == .infinity)
        // nothing declared = 30fps default, not a declared cadence
        let none = try #require(boot(""))
        #expect(abs(none.renderInterval - 1.0 / 30.0) < 0.0001)
        #expect(none.hasDeclaredCadence == false)
        for instance in [slow, hourly, once, none] { instance.invalidate() }
    }

    @Test func consoleLogCapturedToBuffer() throws {
        let source = """
        let properties = [];
        console.log('boot message');
        console.error('an error');
        function render(ctx) {}
        plugin.export = { properties, render };
        """
        let instance = try #require(PluginInstance(pluginID: "t", source: source, overrides: [:]))
        let logs = instance.recentLogs()
        #expect(logs.map(\.message) == ["boot message", "an error"])
        instance.clearLogs()
        #expect(instance.recentLogs().isEmpty)
        #expect(instance.context.isInspectable)
        instance.invalidate()
    }

    @Test func systemStatsAvailableWithoutPermission() async throws {
        let source = """
        let properties = [];
        let snap = null;
        function render(ctx) {}
        plugin.export = { properties, render, grab: function () { snap = $system.stats(); return snap; } };
        """
        let instance = try #require(PluginInstance(pluginID: "t", source: source, overrides: [:]))
        let hasKeys = await withCheckedContinuation { continuation in
            instance.queue.async {
                let v = instance.context.objectForKeyedSubscript("plugin")?
                    .objectForKeyedSubscript("export")?
                    .objectForKeyedSubscript("grab")?
                    .call(withArguments: [])
                let dict = v?.toDictionary()
                continuation.resume(returning:
                    dict?["cpu"] != nil && dict?["memory"] != nil &&
                    dict?["disk"] != nil && dict?["network"] != nil)
            }
        }
        #expect(hasKeys)
        #expect(instance.permissions.isEmpty)
        instance.invalidate()
    }

    @Test func shellRejectsWithoutPermission() async throws {
        let source = """
        let properties = [];
        let result = 'pending';
        shell(['echo', 'hi']).then(function () { result = 'ran'; })
                             .catch(function (e) { result = 'denied:' + e.message; });
        function render(ctx) {}
        plugin.export = { properties, render, read: function () { return result; } };
        """
        let instance = try #require(PluginInstance(pluginID: "t", source: source, overrides: [:]))
        try await Task.sleep(for: .milliseconds(200))
        let result = await readExport(instance, "read")
        #expect(result.hasPrefix("denied:"))
        instance.invalidate()
    }

    @Test func shellRejectsStringArgument() async throws {
        // argv-array only; a string must be refused (no shell injection).
        let source = """
        let properties = [];
        let result = 'pending';
        shell('rm -rf /').catch(function (e) { result = 'err:' + e.message; });
        function render(ctx) {}
        plugin.export = { permissions: ['shell'], properties, render, read: function () { return result; } };
        """
        let instance = try #require(PluginInstance(pluginID: "t", source: source, overrides: [:]))
        try await Task.sleep(for: .milliseconds(200))
        let result = await readExport(instance, "read")
        #expect(result.contains("array"))
        instance.invalidate()
    }

    @Test func shellBlocksDangerousCommands() async throws {
        let source = """
        let properties = [];
        let result = 'pending';
        shell(['rm', '-rf', 'x']).catch(function (e) { result = 'blocked:' + e.message; });
        function render(ctx) {}
        plugin.export = { permissions: ['shell'], properties, render, read: function () { return result; } };
        """
        let instance = try #require(PluginInstance(pluginID: "t", source: source, overrides: [:]))
        try await Task.sleep(for: .milliseconds(200))
        let result = await readExport(instance, "read")
        #expect(result.contains("blocked") && result.contains("rm"))
        instance.invalidate()
    }

    @Test func hookServerFansOutToHandlers() async throws {
        // App-level server delivers one POST to every registered plugin.
        let server = HookServer()
        let port = UInt16(8000 + Int(ProcessInfo.processInfo.processIdentifier % 900))
        server.start(port: port)
        defer { server.stop() }

        actor Box { var value = ""; func set(_ v: String) { value = v }; func get() -> String { value } }
        let a = Box(), b = Box()
        let id1 = UUID(), id2 = UUID()
        server.addHandler(.init(itemID: id1, method: "POST") { _, body in Task { await a.set(body) } })
        server.addHandler(.init(itemID: id2, method: "POST") { _, body in Task { await b.set(body) } })

        try await Task.sleep(for: .milliseconds(300))
        var request = URLRequest(url: URL(string: "http://127.0.0.1:\(port)/hook")!)
        request.httpMethod = "POST"
        request.httpBody = Data("{\"tool\":\"Bash\"}".utf8)
        let (data, _) = try await URLSession.shared.data(for: request)
        let ack = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        #expect(ack?["delivered"] as? Int == 2)

        try await Task.sleep(for: .milliseconds(200))
        let gotA = await a.get(), gotB = await b.get()
        #expect(gotA.contains("Bash") && gotB.contains("Bash"))
    }

    @Test func sshRejectsWithoutDestination() async throws {
        // Permission granted, but no destination configured → clear error.
        // Host APIs are called after load (render/timers), when permissions
        // are resolved — mirror that with setTimeout.
        let source = """
        let properties = [];
        let result = 'pending';
        setTimeout(function () {
            ssh(['uptime']).catch(function (e) { result = 'err:' + e.message; });
        }, 0);
        function render(ctx) {}
        plugin.export = { permissions: ['ssh'], properties, render, read: function () { return result; } };
        """
        let instance = try #require(PluginInstance(pluginID: "t", source: source, overrides: [:]))
        #expect(instance.permissions.contains("ssh"))
        try await Task.sleep(for: .milliseconds(300))
        let result = await readExport(instance, "read")
        #expect(result.contains("no SSH destination"))
        instance.invalidate()
    }

    @Test func sshRejectsWithoutPermission() async throws {
        let source = """
        let properties = [];
        let result = 'pending';
        ssh(['uptime']).catch(function (e) { result = 'denied:' + e.message; });
        function render(ctx) {}
        plugin.export = { properties, render, read: function () { return result; } };
        """
        let instance = try #require(PluginInstance(pluginID: "t", source: source, overrides: [:]))
        try await Task.sleep(for: .milliseconds(200))
        let result = await readExport(instance, "read")
        #expect(result.hasPrefix("denied:"))
        instance.invalidate()
    }

    @Test func multiHostSSHModelAndLegacyDecode() throws {
        // A layout written before multi-host support carries a single `ssh`.
        let legacy = """
        {"id": "11111111-1111-1111-1111-111111111111", "pluginID": "P",
         "displayUUID": "X", "normalizedFrame": [[0,0],[1,1]],
         "ssh": {"host": "nas", "port": 2222, "user": "zfu", "auth": "key", "keyPath": "/k"}}
        """
        let decoded = try JSONDecoder().decode(LayoutItem.self, from: Data(legacy.utf8))
        #expect(decoded.sshHosts.count == 1)
        #expect(decoded.ssh.host == "nas")
        #expect(decoded.ssh.port == 2222)

        // Several destinations round trip, and a bare alias counts as
        // configured (~/.ssh/config supplies user/port/key).
        var item = decoded
        item.sshHosts = [
            SSHConfig(name: "docker", host: "docker"),
            SSHConfig(name: "mini", host: "mini"),
        ]
        let allConfigured = item.sshHosts.filter(\.isConfigured).count == item.sshHosts.count
        #expect(allConfigured)
        let data = try JSONEncoder().encode(item)
        let round = try JSONDecoder().decode(LayoutItem.self, from: data)
        #expect(round.sshHosts.map(\.name) == ["docker", "mini"])
        #expect(round.ssh.name == "docker") // `ssh` is the first host
    }

    @Test func sshConfigAliasesParse() throws {
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("dl-ssh-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let file = dir.appendingPathComponent("config")
        try """
        Host *
            ServerAliveInterval 60
        # a comment
        Host docker pve
            HostName 10.0.0.5
        Host mini
            User zfu
        """.write(to: file, atomically: true, encoding: .utf8)
        // Patterns like `*` are skipped; multiple names on one line are kept.
        let aliases = SSHConfigFile.aliases(at: file)
        #expect(aliases == ["docker", "pve", "mini"])
    }

    @Test func keychainRoundTrip() {
        let id = UUID()
        KeychainStore.setPassword("hunter2", forItem: id)
        #expect(KeychainStore.password(forItem: id) == "hunter2")
        KeychainStore.setPassword(nil, forItem: id)
        #expect(KeychainStore.password(forItem: id) == nil)
    }

    @Test func layoutItemCarriesBackgroundAndSSH() throws {
        // Old layouts (no new fields) still decode; round trip keeps them.
        let old = """
        {"id": "11111111-1111-1111-1111-111111111111", "pluginID": "P",
         "displayUUID": "X", "normalizedFrame": [[0,0],[1,1]]}
        """
        let decoded = try JSONDecoder().decode(LayoutItem.self, from: Data(old.utf8))
        #expect(decoded.backgroundColor == nil)
        #expect(decoded.ssh.auth == .none)

        var item = decoded
        item.backgroundColor = "#112233ff"
        item.ssh = SSHConfig(host: "h", port: 2222, user: "u", auth: .key, keyPath: "/k")
        let data = try JSONEncoder().encode(item)
        let round = try JSONDecoder().decode(LayoutItem.self, from: data)
        #expect(round.backgroundColor == "#112233ff")
        #expect(round.ssh.isConfigured)
        #expect(round.ssh.port == 2222)
    }

    @Test func metadataExtractionAndVersionCompare() {
        let source = """
        let properties = [];
        function render(ctx) {}
        plugin.export = {
            version: "2.3.1", author: "Ada", description: "Does things",
            updateURL: "https://example.com/p.js", properties, render
        };
        """
        let meta = PluginMetadata.extract(from: source)
        #expect(meta.version == "2.3.1")
        #expect(meta.author == "Ada")
        #expect(meta.summary == "Does things")
        #expect(meta.updateURL == "https://example.com/p.js")

        // Metadata extraction must not run side effects (timers/network).
        let sideEffecty = """
        let fired = false;
        setInterval(function () { fired = true; }, 1);
        fetch("https://nope.example");
        plugin.export = { version: "1.0.0", render: function () {} };
        """
        #expect(PluginMetadata.extract(from: sideEffecty).version == "1.0.0")

        #expect(compareVersions("1.2.10", "1.2.9") == .orderedDescending)
        #expect(compareVersions("1.2.0", "1.2") == .orderedSame)
        #expect(compareVersions("0.9", "1.0") == .orderedAscending)
    }

    @Test func updaterInstallsNewerVersion() async throws {
        // Serve a "remote" newer plugin from a temp file:// URL.
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("dl-upd-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        let remoteURL = dir.appendingPathComponent("remote.js")
        let installed = dir.appendingPathComponent("Plugin.js")
        let updateURLLine = "updateURL: \"\(remoteURL.absoluteString)\""
        let v1 = "plugin.export = { version: \"1.0.0\", \(updateURLLine), render: function(){} };"
        let v2 = "plugin.export = { version: \"1.1.0\", \(updateURLLine), render: function(){} };"
        try v1.write(to: installed, atomically: true, encoding: .utf8)
        try v2.write(to: remoteURL, atomically: true, encoding: .utf8)

        let updater = await PluginUpdater()
        let result = await updater.check(pluginID: "Plugin", installedSource: v1, destination: installed)
        #expect(result == .updated(from: "1.0.0", to: "1.1.0"))
        let onDisk = try String(contentsOf: installed, encoding: .utf8)
        #expect(onDisk.contains("1.1.0"))

        // Re-checking against the now-current file reports up to date.
        let again = await updater.check(pluginID: "Plugin", installedSource: onDisk, destination: installed)
        #expect(again == .upToDate(version: "1.1.0"))
    }

    @Test func updaterUsesManifest() async throws {
        // A tiny .json manifest is checked; the .js body downloads only when
        // the manifest reports a newer version.
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("dl-mani-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        let jsURL = dir.appendingPathComponent("Plugin.js")
        let manifestURL = dir.appendingPathComponent("Plugin.json")   // sibling, same name
        let updateLine = "updateURL: \"\(jsURL.absoluteString)\""
        let installed = "plugin.export = { version: \"1.0.0\", \(updateLine), render: function(){} };"
        let newBody = "plugin.export = { version: \"2.0.0\", \(updateLine), render: function(){} };"
        let manifest = "{ \"version\": \"2.0.0\", \"url\": \"\(jsURL.absoluteString)\" }"
        try installed.write(to: dir.appendingPathComponent("Plugin_installed.js"), atomically: true, encoding: .utf8)
        try newBody.write(to: jsURL, atomically: true, encoding: .utf8)
        try manifest.write(to: manifestURL, atomically: true, encoding: .utf8)

        let dest = dir.appendingPathComponent("Plugin_installed.js")
        let updater = await PluginUpdater()
        let result = await updater.check(pluginID: "Plugin", installedSource: installed, destination: dest)
        #expect(result == .updated(from: "1.0.0", to: "2.0.0"))
        #expect(try String(contentsOf: dest, encoding: .utf8).contains("2.0.0"))

        // Manifest now equal → up to date, no re-download needed.
        let again = await updater.check(pluginID: "Plugin", installedSource: newBody, destination: dest)
        #expect(again == .upToDate(version: "2.0.0"))
    }

    @Test func updateFallsBackToJSWhenManifestMissingOrBad() async throws {
        // The manifest is optional: with none (or a malformed one), the check
        // must still update by reading the .js body's declared version.
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("dl-fallback-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        let jsURL = dir.appendingPathComponent("Plugin.js")
        let dest = dir.appendingPathComponent("Plugin_installed.js")
        let updateLine = "updateURL: \"\(jsURL.absoluteString)\""
        let installed = "plugin.export = { version: \"1.0.0\", \(updateLine), render: function(){} };"
        let newBody = "plugin.export = { version: \"3.0.0\", \(updateLine), render: function(){} };"
        try installed.write(to: dest, atomically: true, encoding: .utf8)
        try newBody.write(to: jsURL, atomically: true, encoding: .utf8)

        let updater = await PluginUpdater()

        // (a) No manifest file exists at Plugin.json → JS fallback updates.
        let noManifest = await updater.check(pluginID: "Plugin", installedSource: installed, destination: dest)
        #expect(noManifest == .updated(from: "1.0.0", to: "3.0.0"))

        // (b) A malformed manifest is ignored → JS fallback still works.
        try "not json at all".write(to: dir.appendingPathComponent("Plugin.json"), atomically: true, encoding: .utf8)
        try installed.write(to: dest, atomically: true, encoding: .utf8) // reset to 1.0.0
        let badManifest = await updater.check(pluginID: "Plugin", installedSource: installed, destination: dest)
        #expect(badManifest == .updated(from: "1.0.0", to: "3.0.0"))
    }

    @Test func webviewModeParsesConfig() throws {
        let source = """
        let properties = [
            { name: "url", valueType: "string", value: "https://example.com/page" },
            { name: "offsetY", valueType: "number", value: "120" }
        ];
        plugin.export = {
            mode: "webview",
            properties,
            webview: {
                userAgent: "DeskLayer/1.0",
                headers: { "X-Test": "yes" },
                cookies: [{ name: "s", value: "abc", domain: "example.com", path: "/" }],
                zoom: 1.5
            }
        };
        """
        let instance = try #require(PluginInstance(pluginID: "w", source: source, overrides: [:]))
        #expect(instance.renderMode == .webview)
        let cfg = try #require(instance.webviewConfig)
        #expect(cfg.url == "https://example.com/page")     // from a property
        #expect(cfg.offsetY == 120)                        // from a property
        #expect(cfg.userAgent == "DeskLayer/1.0")          // from webview config
        #expect(cfg.headers["X-Test"] == "yes")
        #expect(cfg.cookies.first?["value"] == "abc")
        #expect(cfg.zoom == 1.5)
        instance.invalidate()
    }

    @Test func interactiveTreeCarriesActionsAndInvokes() async throws {
        // Button/onTapGesture register callbacks in the action table and
        // serialize a numeric id; __dl_invokeAction runs them.
        let source = """
        let properties = [];
        let taps = 0; let lastText = "";
        render = () => view([
            VStack([
                Button("inc", () => { taps += 1; }),
                TextField("name", (e) => { lastText = e.text; }),
                Text("card").onTapGesture((e) => { taps += 100; })
            ])
        ]);
        plugin.export = { properties, render,
            readTaps: () => taps, readText: () => lastText };
        """
        let instance = try #require(PluginInstance(pluginID: "t", source: source, overrides: [:]))
        #expect(instance.renderMode == .declarative)

        // Everything in one queue hop so the assertions can't race with
        // parallel test teardown (JSValues are queue-confined).
        struct Result: Sendable { let json: String?; let taps: String; let text: String }
        let result: Result = await withCheckedContinuation { continuation in
            instance.queue.async {
                let json = instance.callRenderTree()
                guard let json, let tree = ViewNode.decode(fromJSON: json),
                      let stack = tree.children?.first,
                      let button = stack.children?.first(where: { $0.type == "Button" }),
                      let onTapID = button.modifiers?.first(where: { $0.name == "onTap" })?.firstDouble.map({ Int($0) }),
                      let field = stack.children?.first(where: { $0.type == "TextField" }),
                      let onChangeID = field.modifiers?.first(where: { $0.name == "onChange" })?.firstDouble.map({ Int($0) })
                else {
                    continuation.resume(returning: Result(json: json, taps: "?", text: "?")); return
                }
                instance.invokeAction(id: onTapID, payloadJSON: "{}")
                instance.invokeAction(id: onChangeID, payloadJSON: "{\"text\":\"ada\"}")
                func read(_ name: String) -> String {
                    instance.context.objectForKeyedSubscript("plugin")?
                        .objectForKeyedSubscript("export")?
                        .objectForKeyedSubscript(name)?.call(withArguments: [])?.toString() ?? ""
                }
                continuation.resume(returning: Result(json: json, taps: read("readTaps"), text: read("readText")))
            }
        }
        #expect(result.json != nil)
        #expect(result.taps == "1")
        #expect(result.text == "ada")
        instance.invalidate()
    }

    @Test func metadataReadsSizeAndResizable() {
        let sized = """
        function render(ctx) {}
        plugin.export = { width: 260, height: 180, resizable: false, render };
        """
        let m = PluginMetadata.extract(from: sized)
        #expect(m.preferredSize == CGSize(width: 260, height: 180))
        #expect(m.resizable == false)

        // Defaults: no size, resizable true.
        let plain = "function render(ctx){}; plugin.export = { render };"
        let d = PluginMetadata.extract(from: plain)
        #expect(d.preferredSize == nil)
        #expect(d.resizable == true)
    }

    @Test func pluginOriginClassification() {
        // Built-ins are app-maintained and can't be removed; other bundled
        // samples are examples; anything else is user-installed.
        #expect(SamplePlugins.origin(of: "AnalogClock") == .builtin)
        #expect(SamplePlugins.origin(of: "SystemMonitor") == .builtin)
        #expect(SamplePlugins.origin(of: "RemoteMonitor") == .builtin)
        #expect(SamplePlugins.origin(of: "Particles") == .example)
        #expect(SamplePlugins.origin(of: "HelloCard") == .example)
        #expect(SamplePlugins.origin(of: "MyOwnPlugin") == .user)

        #expect(PluginOrigin.builtin.isRemovable == false)
        #expect(PluginOrigin.example.isRemovable)
        #expect(PluginOrigin.user.isRemovable)
    }

    @Test func uninstalledExampleIsNotReinstalled() throws {
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("dl-samples-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        SamplePlugins.installIfMissing(into: dir)
        let particles = dir.appendingPathComponent("Particles.js")
        #expect(FileManager.default.fileExists(atPath: particles.path))

        // Simulate an uninstall, then a relaunch: the example stays gone…
        try FileManager.default.removeItem(at: particles)
        SamplePlugins.installIfMissing(into: dir, skipping: ["Particles"])
        #expect(FileManager.default.fileExists(atPath: particles.path) == false)

        // …while everything else is still restored.
        #expect(FileManager.default.fileExists(atPath: dir.appendingPathComponent("AnalogClock.js").path))
    }

    @Test func metadataScalePolicyAndLimits() {
        let ratio = PluginMetadata.extract(from: """
        plugin.export = { width: 200, height: 100, scaleMode: "ratio",
                          minWidth: 150, maxWidth: 400, minHeight: 80, maxHeight: 300,
                          render: function(){} };
        """)
        #expect(ratio.keepsAspect)
        #expect(ratio.clamp(CGSize(width: 100, height: 50)) == CGSize(width: 150, height: 80))
        #expect(ratio.clamp(CGSize(width: 900, height: 900)) == CGSize(width: 400, height: 300))
        #expect(ratio.clamp(CGSize(width: 250, height: 120)) == CGSize(width: 250, height: 120))

        // scaleMode "free" opts out of aspect locking…
        let free = PluginMetadata.extract(from: """
        plugin.export = { width: 200, height: 100, scaleMode: "free", render: function(){} };
        """)
        #expect(free.keepsAspect == false)
        // …and with nothing declared, a natural size implies a locked aspect,
        // while a plugin with no size resizes freely.
        let sized = PluginMetadata.extract(from: "plugin.export = { width: 10, height: 10, render: function(){} };")
        let unsized = PluginMetadata.extract(from: "plugin.export = { render: function(){} };")
        #expect(sized.keepsAspect)
        #expect(unsized.keepsAspect == false)
        // A limit may be one-sided.
        #expect(unsized.clamp(CGSize(width: 5, height: 5)) == CGSize(width: 5, height: 5))
    }

    @Test func preferredSizeMapsToScreenFraction() {
        // 260pt on a 1300pt-wide screen → 0.2 width.
        let size = PluginLibraryView.defaultNormalizedSize(
            preferred: CGSize(width: 260, height: 130), screen: CGSize(width: 1300, height: 650)
        )
        #expect(abs(size.width - 0.2) < 0.001)
        #expect(abs(size.height - 0.2) < 0.001)
        // No preferred size → 20% fallback.
        let fallback = PluginLibraryView.defaultNormalizedSize(preferred: nil, screen: CGSize(width: 1000, height: 1000))
        #expect(fallback == CGSize(width: 0.2, height: 0.2))
    }

    @Test func webviewNeedsNoRenderFunction() throws {
        // A webview plugin with no render() must still load.
        let source = """
        plugin.export = { mode: "webview", webview: { url: "https://example.com" } };
        """
        let instance = try #require(PluginInstance(pluginID: "w", source: source, overrides: [:]))
        #expect(instance.renderMode == .webview)
        #expect(instance.webviewConfig?.url == "https://example.com")
        instance.invalidate()
    }

    private func readExport(_ instance: PluginInstance, _ name: String) async -> String {
        await withCheckedContinuation { continuation in
            instance.queue.async {
                let v = instance.context.objectForKeyedSubscript("plugin")?
                    .objectForKeyedSubscript("export")?
                    .objectForKeyedSubscript(name)?
                    .call(withArguments: [])
                continuation.resume(returning: v?.toString() ?? "")
            }
        }
    }

    @Test func invalidateMidFetchDoesNotCrash() async throws {
        let source = """
        let properties = [];
        fetch('https://example.com/').then(function (r) { console.log('done ' + r.status); });
        function render(ctx) {}
        plugin.export = { properties, render };
        """
        let instance = try #require(PluginInstance(pluginID: "t", source: source, overrides: [:]))
        // Tear down immediately while the request is in flight.
        instance.invalidate()
        try await Task.sleep(for: .milliseconds(300))
        #expect(!instance.isErrored)
    }
}
