//
//  DeskLayerTests.swift
//  DeskLayerTests
//

import AppKit
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
        let port = UInt16(8000 + Int(ProcessInfo.processInfo.processIdentifier % 900))
        // The listener is handler-driven now: the first addHandler below
        // brings it up on this port, the last removal takes it down.
        let server = HookServer(port: port)
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

    @Test func inspectorSizeSnapsBackToDeclaredLimits() {
        // The Clock's shape: aspect-locked, 140–700 on both axes.
        let ratio = PluginMetadata.extract(from: """
        function render(ctx) {}
        plugin.export = { width: 300, height: 300, scaleMode: "ratio",
                          minWidth: 140, maxWidth: 700,
                          minHeight: 140, maxHeight: 700, render };
        """)
        // Over the maximum snaps down; the locked axis follows.
        #expect(ratio.resolvedSize(entered: CGSize(width: 900, height: 300), edited: .width)
                == CGSize(width: 700, height: 700))
        // Under the minimum snaps up.
        #expect(ratio.resolvedSize(entered: CGSize(width: 50, height: 300), edited: .width)
                == CGSize(width: 140, height: 140))
        // Editing height drives width the same way.
        #expect(ratio.resolvedSize(entered: CGSize(width: 300, height: 9000), edited: .height)
                == CGSize(width: 700, height: 700))
        // In range, the entered size stands.
        #expect(ratio.resolvedSize(entered: CGSize(width: 420, height: 300), edited: .width)
                == CGSize(width: 420, height: 420))

        // Free scaling: each axis is clamped on its own, the other untouched.
        let free = PluginMetadata.extract(from: """
        function render(ctx) {}
        plugin.export = { width: 300, height: 200, scaleMode: "free",
                          maxWidth: 500, minHeight: 100, render };
        """)
        #expect(free.resolvedSize(entered: CGSize(width: 800, height: 250), edited: .width)
                == CGSize(width: 500, height: 250))
        #expect(free.resolvedSize(entered: CGSize(width: 300, height: 20), edited: .height)
                == CGSize(width: 300, height: 100))

        // No declared limits: only the floor that stops an item vanishing.
        let plain = PluginMetadata.extract(from: "function render(ctx){}; plugin.export = { render };")
        #expect(plain.resolvedSize(entered: CGSize(width: 0, height: 4), edited: .width)
                == CGSize(width: 8, height: 8))
    }

    /// The store-origin map is global to the process and these tests run in
    /// parallel, so each removes only its own plugin rather than the key.
    private func forgetStoreOrigin(_ pluginID: String) {
        let key = "DeskLayer.pluginStoreOrigins"
        var map = (UserDefaults.standard.dictionary(forKey: key) as? [String: String]) ?? [:]
        map.removeValue(forKey: pluginID)
        UserDefaults.standard.set(map, forKey: key)
    }

    private func rememberStoreOrigin(_ pluginID: String, store: String) {
        let key = "DeskLayer.pluginStoreOrigins"
        var map = (UserDefaults.standard.dictionary(forKey: key) as? [String: String]) ?? [:]
        map[pluginID] = store
        UserDefaults.standard.set(map, forKey: key)
    }

    @MainActor
    @Test func storeFallsBackToMirrorWhenPrimaryFails() async throws {
        // The China/GitHub case: the primary address is unreachable, so both
        // the catalog and the plugin have to come from the mirror.
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("dl-mirror-\(UUID().uuidString)", isDirectory: true)
        let installDir = dir.appendingPathComponent("Plugins", isDirectory: true)
        try FileManager.default.createDirectory(at: installDir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        let pluginURL = dir.appendingPathComponent("Mirrored.js")
        try """
        let properties = [];
        function render(ctx) {}
        plugin.export = { version: "1.0.0", properties, render };
        """.write(to: pluginURL, atomically: true, encoding: .utf8)

        let missing = dir.appendingPathComponent("gone.json").absoluteString
        let missingPlugin = dir.appendingPathComponent("gone.js").absoluteString
        let catalogURL = dir.appendingPathComponent("mirror-catalog.json")
        try """
        {"name": "Mirror Store",
         "website": "https://example.com/store",
         "plugins": [
          {"name": "Mirrored", "url": "\(missingPlugin)",
           "mirrors": ["\(pluginURL.absoluteString)"], "version": "1.0.0"}
        ]}
        """.write(to: catalogURL, atomically: true, encoding: .utf8)

        let registry = PluginStoreRegistry()
        // Primary 404s; the mirror carries the catalog.
        let added = await registry.addStore(urlString: missing,
                                            mirrors: [catalogURL.absoluteString])
        #expect(added)
        let entry = try #require(registry.stores.first)
        #expect(entry.catalog?.name == "Mirror Store")
        #expect(entry.catalog?.website == "https://example.com/store")
        // The address that worked is remembered and tried first next time.
        #expect(entry.lastGoodURL == catalogURL.absoluteString)
        #expect(entry.candidateURLs.first == catalogURL.absoluteString)
        #expect(entry.fetchedAt != nil)
        #expect(entry.isFresh())

        // Installing walks the plugin's mirrors the same way.
        let plugin = try #require(entry.catalog?.plugins.first)
        let error = await registry.install(plugin, from: "Mirror Store", into: installDir)
        #expect(error == nil)
        #expect(FileManager.default.fileExists(
            atPath: installDir.appendingPathComponent("Mirrored.js").path))

        registry.removeStore(entry.id)
        forgetStoreOrigin("Mirrored")
    }

    // MARK: - Plugin authoring (LLM)

    @Test func validatorAcceptsShippedPluginsAndRejectsBrokenOnes() {
        // The gate for generated code. Canvas takes ctx, declarative doesn't.
        let canvas = "function render(ctx) {}\nplugin.export = { render };"
        let declarative = "render = () => view([]);\nplugin.export = { render };"
        let webview = "plugin.export = { webview: { url: \"https://example.com\" } };"
        #expect(PluginMetadata.validate(source: canvas) == .ok(mode: "canvas"))
        #expect(PluginMetadata.validate(source: declarative) == .ok(mode: "declarative"))
        #expect(PluginMetadata.validate(source: webview) == .ok(mode: "webview"))

        // Each of these is a mistake a model actually makes.
        #expect(PluginMetadata.validate(source: "").isOK == false)          // empty
        #expect(PluginMetadata.validate(source: "let x = ;").isOK == false) // syntax
        #expect(PluginMetadata.validate(source: "function render(ctx) {}").isOK == false) // no export
        #expect(PluginMetadata.validate(source: "plugin.export = { version: \"1\" };").isOK == false)
        #expect(PluginMetadata.validate(source: "plugin.export = { render: 42 };").isOK == false)
        #expect(PluginMetadata.validate(source: "throw new Error('x');").isOK == false)

        // The message is fed back to the model, so it has to say something.
        let why = PluginMetadata.validate(source: "plugin.export = { version: \"1\" };").message
        #expect(why.contains("render"))
    }

    @MainActor
    @Test func toolWritesStayInsideStaging() {
        let tools = PluginTools(registry: PluginRegistry())
        defer { tools.cleanUp() }

        // A plain name lands in staging as <name>.js.
        let ok = try? #require(tools.stagedURL(for: "Weather Card"))
        #expect(ok?.lastPathComponent == "Weather Card.js")
        #expect(ok?.deletingLastPathComponent().path == tools.stagingURL.standardizedFileURL.path)
        // .js already present isn't doubled.
        #expect(tools.stagedURL(for: "Clock.js")?.lastPathComponent == "Clock.js")

        // Nothing may escape the staging directory.
        for escape in ["../evil", "../../etc/passwd", "/etc/passwd", "..", ".", "", "   ",
                       "a/../../b", "/tmp/x.js"] {
            let url = tools.stagedURL(for: escape)
            if let url {
                #expect(url.deletingLastPathComponent().standardizedFileURL.path
                        == tools.stagingURL.standardizedFileURL.path,
                        "\(escape) escaped staging")
            }
        }
        // The obvious traversals resolve to a bare name or are refused.
        #expect(tools.stagedURL(for: "../../etc/passwd")?.lastPathComponent == "passwd.js")
        #expect(tools.stagedURL(for: "..") == nil)
        #expect(tools.stagedURL(for: "") == nil)
    }

    @MainActor
    @Test func toolsReportValidationBackToTheModel() {
        let tools = PluginTools(registry: PluginRegistry())
        defer { tools.cleanUp() }

        let broken = ToolCall(id: "1", function: .init(
            name: "write_plugin",
            arguments: "{\"name\": \"Probe\", \"source\": \"plugin.export = { version: 1 };\"}"))
        let firstReply = tools.run(broken)
        #expect(firstReply.contains("not valid"))
        #expect(firstReply.contains("render"))

        let fixed = ToolCall(id: "2", function: .init(
            name: "write_plugin",
            arguments: "{\"name\": \"Probe\", \"source\": \"render = () => view([]); plugin.export = { render };\"}"))
        #expect(tools.run(fixed).contains("Valid"))
        #expect(tools.run(ToolCall(id: "3", function: .init(
            name: "validate_plugin", arguments: "{\"name\": \"Probe\"}"))).contains("Valid"))

        // Unknown tools and missing arguments are answers, not crashes.
        #expect(tools.run(ToolCall(id: "4", function: .init(name: "nope", arguments: "{}")))
            .hasPrefix("error:"))
        #expect(tools.run(ToolCall(id: "5", function: .init(name: "read_file", arguments: "{}")))
            .hasPrefix("error:"))
        // Reading a plugin that doesn't exist points at list_plugins.
        #expect(tools.run(ToolCall(id: "6", function: .init(
            name: "read_file", arguments: "{\"name\": \"NoSuchPlugin\"}"))).contains("list_plugins"))
    }

    @MainActor
    @Test func editingInstallsUnderTheRightName() {
        let session = PluginAuthorSession(registry: PluginRegistry())

        // A fresh plugin keeps whatever the model called it.
        #expect(session.installName(written: "Weather", subject: .newPlugin) == "Weather")

        // Replacing lands on the original even when the model renamed it —
        // otherwise "make the bars thinner" would quietly create a second
        // plugin and leave the one on the desktop untouched.
        #expect(session.installName(written: "Weather v2", subject: .replace("Weather")) == "Weather")
        #expect(session.installName(written: "Weather", subject: .replace("Weather")) == "Weather")

        // A copy must never land on its base, whatever the model called it.
        #expect(session.installName(written: "Weather", subject: .copy(of: "Weather")) == "Weather 2")
        #expect(session.installName(written: "Weather Bright", subject: .copy(of: "Weather")) == "Weather Bright")
    }

    @Test func renameMovesTheFileAndRefusesStorePlugins() throws {
        // A folder of this test's own — the real plugins folder is never
        // touched, so a plain test run can't rename anything the user has.
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("dl-rename-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let source = "render = () => text(\"hi\"); plugin.export = { render };"
        for name in ["Mine", "Taken"] {
            try source.write(to: dir.appendingPathComponent("\(name).js"), atomically: true, encoding: .utf8)
        }
        let mine = PluginDescriptor(id: "Mine", sourceURL: dir.appendingPathComponent("Mine.js"))
        let ids = ["Mine", "Taken", "Store Plugin"]

        // A store plugin refuses before anything moves.
        let fromStore = PluginDescriptor(id: "Store Plugin",
                                         sourceURL: dir.appendingPathComponent("Store Plugin.js"),
                                         origin: .store("Official"))
        #expect(PluginRegistry.performRename(of: fromStore, to: "X", existingIDs: ids)
                == .fromStore("Official"))

        // So do names that can't work, or that another plugin holds.
        #expect(PluginRegistry.performRename(of: mine, to: "  ", existingIDs: ids) == .invalidName)
        #expect(PluginRegistry.performRename(of: mine, to: "../escape", existingIDs: ids) == .invalidName)
        #expect(PluginRegistry.performRename(of: mine, to: "taken", existingIDs: ids) == .nameTaken)
        #expect(PluginRegistry.performRename(of: mine, to: "Mine", existingIDs: ids) == .unchanged)
        #expect(FileManager.default.fileExists(atPath: dir.appendingPathComponent("Mine.js").path))

        // The rename itself: the file moves, ".js" in the typed name is fine.
        #expect(PluginRegistry.performRename(of: mine, to: "Better.js", existingIDs: ids)
                == .renamed("Better"))
        #expect(FileManager.default.fileExists(atPath: dir.appendingPathComponent("Better.js").path))
        #expect(!FileManager.default.fileExists(atPath: dir.appendingPathComponent("Mine.js").path))

        // A .deskplugin folder moves as a folder.
        let bundleDir = dir.appendingPathComponent("Bundled.deskplugin", isDirectory: true)
        try FileManager.default.createDirectory(at: bundleDir, withIntermediateDirectories: true)
        try source.write(to: bundleDir.appendingPathComponent("main.js"), atomically: true, encoding: .utf8)
        let bundled = PluginDescriptor(id: "Bundled",
                                       sourceURL: bundleDir.appendingPathComponent("main.js"),
                                       assetsURL: bundleDir)
        #expect(PluginRegistry.performRename(of: bundled, to: "Bundle Two", existingIDs: ["Bundled"])
                == .renamed("Bundle Two"))
        #expect(FileManager.default.fileExists(
            atPath: dir.appendingPathComponent("Bundle Two.deskplugin/main.js").path))
    }

    @Test func renameNormalizesTheNameAndRefusesUnusableOnes() {
        // "Name" and "Name.js" mean the same thing to a user.
        #expect(PluginRegistry.normalizedName("  Weather  ") == "Weather")
        #expect(PluginRegistry.normalizedName("Weather.js") == "Weather")
        #expect(PluginRegistry.normalizedName("Weather.JS") == "Weather")
        // Spaces and dots inside the name are fine — it is a file name, not
        // an identifier.
        #expect(PluginRegistry.normalizedName("My Clock v2.1") == "My Clock v2.1")

        // Anything that wouldn't be one plain file in the plugins folder.
        #expect(PluginRegistry.normalizedName("") == nil)
        #expect(PluginRegistry.normalizedName("   ") == nil)
        #expect(PluginRegistry.normalizedName(".js") == nil)
        #expect(PluginRegistry.normalizedName("../etc/passwd") == nil)
        #expect(PluginRegistry.normalizedName("a/b") == nil)
        #expect(PluginRegistry.normalizedName("a:b") == nil)
        #expect(PluginRegistry.normalizedName(".hidden") == nil)
    }

    @Test func renamedPluginKeepsItsPlacedItems() {
        // Items point at a plugin by id, so a rename that didn't follow would
        // leave every placed copy rendering nothing.
        var layout = Layout(items: [
            LayoutItem(pluginID: "Weather", displayUUID: "A", normalizedFrame: .zero),
            LayoutItem(pluginID: "Weather", displayUUID: "B", normalizedFrame: .zero),
            LayoutItem(pluginID: "Clock", displayUUID: "A", normalizedFrame: .zero),
        ])
        #expect(layout.repoint(pluginID: "Weather", to: "Weather Bright") == true)
        #expect(layout.items.map(\.pluginID) == ["Weather Bright", "Weather Bright", "Clock"])

        // Nothing placed: the store can skip the save.
        #expect(layout.repoint(pluginID: "Nobody", to: "X") == false)
    }

    @MainActor
    @Test func storePluginsAreCopiedNeverReplaced() {
        let session = PluginAuthorSession(registry: PluginRegistry())
        rememberStoreOrigin("dl-store-owned", store: "Official")
        defer { forgetStoreOrigin("dl-store-owned") }
        forgetStoreOrigin("dl-hand-written")

        // A plugin the user wrote or imported is theirs to overwrite.
        #expect(session.resolved(.replace("dl-hand-written")) == .replace("dl-hand-written"))

        // One that came from a store is not: the store's next update would
        // overwrite the rewrite and take the user's changes with it.
        #expect(session.resolved(.replace("dl-store-owned")) == .copy(of: "dl-store-owned"))
        // Everything else passes through untouched.
        #expect(session.resolved(.copy(of: "dl-store-owned")) == .copy(of: "dl-store-owned"))
        #expect(session.resolved(.newPlugin) == .newPlugin)
    }

    @Test func modelListDecodingAndEndpoint() {
        #expect(LLMSettings(baseURL: "https://api.openai.com/v1").modelsURL?.absoluteString
                == "https://api.openai.com/v1/models")
        // A base pasted as the full completions URL still lists models.
        #expect(LLMSettings(baseURL: "https://x.example/v1/chat/completions").modelsURL?.absoluteString
                == "https://x.example/v1/models")
        #expect(LLMSettings(baseURL: "").modelsURL == nil)

        // The fetched list is part of the saved settings, so the picker is
        // populated on the next launch without asking the endpoint again.
        var settings = LLMSettings()
        settings.cachedModels = ["a", "b"]
        let data = try? JSONEncoder().encode(settings)
        let decoded = data.flatMap { try? JSONDecoder().decode(LLMSettings.self, from: $0) }
        #expect(decoded?.cachedModels == ["a", "b"])
        // The listing itself, in the shapes providers actually send: an "id"
        // per the spec, a "name" from gateways that renamed it, and entries
        // with neither, which are dropped rather than failing the fetch.
        let body = """
        {"object": "list", "data": [
            {"id": "gpt-4o", "object": "model"},
            {"name": "llama3"},
            {"object": "model"}
        ]}
        """
        let list = try? JSONDecoder().decode(ChatClient.ModelListResponse.self, from: Data(body.utf8))
        #expect(list?.data.map(\.id) == ["gpt-4o", "llama3", ""])
        // A body with no data at all decodes to an empty list, not a throw.
        #expect((try? JSONDecoder().decode(ChatClient.ModelListResponse.self,
                                           from: Data("{}".utf8)))?.data.isEmpty == true)

        // And settings written before the field existed still load.
        let old = #"{"baseURL": "https://x.example/v1"}"#
        #expect((try? JSONDecoder().decode(LLMSettings.self, from: Data(old.utf8)))?.cachedModels == [])
    }

    @Test func toolCallDecodingToleratesProviderQuirks() {
        // Arguments as a string — what the spec says.
        let spec = """
        {"id": "a", "type": "function",
         "function": {"name": "write_plugin", "arguments": "{\\"name\\": \\"X\\"}"}}
        """
        let asString = try? JSONDecoder().decode(ToolCall.self, from: Data(spec.utf8))
        #expect(asString?.function.name == "write_plugin")
        #expect(JSONValue.string("name", in: asString?.function.arguments ?? "") == "X")

        // Arguments as an object — what some providers actually send.
        let object = """
        {"id": "b", "function": {"name": "read_file", "arguments": {"name": "plugin.d.ts"}}}
        """
        let asObject = try? JSONDecoder().decode(ToolCall.self, from: Data(object.utf8))
        #expect(asObject?.function.name == "read_file")
        #expect(JSONValue.string("name", in: asObject?.function.arguments ?? "") == "plugin.d.ts")

        // A missing id is synthesised rather than failing the whole turn.
        let noID = #"{"function": {"name": "list_plugins", "arguments": "{}"}}"#
        #expect((try? JSONDecoder().decode(ToolCall.self, from: Data(noID.utf8)))?.id.isEmpty == false)
    }

    @Test func llmSettingsBuildTheEndpointAndSurviveOldFiles() {
        #expect(LLMSettings(baseURL: "https://api.openai.com/v1").completionsURL?.absoluteString
                == "https://api.openai.com/v1/chat/completions")
        // Trailing slash, and a base that already names the endpoint.
        #expect(LLMSettings(baseURL: "http://localhost:11434/v1/").completionsURL?.absoluteString
                == "http://localhost:11434/v1/chat/completions")
        #expect(LLMSettings(baseURL: "https://x.example/v1/chat/completions").completionsURL?.absoluteString
                == "https://x.example/v1/chat/completions")
        #expect(LLMSettings(baseURL: "").completionsURL == nil)
        #expect(LLMSettings(baseURL: "", model: "m").isConfigured == false)

        // Settings written before a field existed still load.
        let old = #"{"baseURL": "https://x.example/v1"}"#
        let decoded = try? JSONDecoder().decode(LLMSettings.self, from: Data(old.utf8))
        #expect(decoded?.baseURL == "https://x.example/v1")
        #expect(decoded?.model.isEmpty == false)
        #expect(decoded?.maxTurns == 12)
    }

    @Test func storePersistenceSurvivesDamage() {
        // The failure that ate a user's store list: one entry a build can't
        // decode used to fail the whole array, load() silently kept [], and
        // the next save overwrote the only copy. Decode is per-entry now.
        let good = #"{"url": "https://a.example/catalog.json", "mirrors": ["https://m.example/c.json"]}"#
        let noURL = #"{"catalog": {"name": "X", "plugins": []}}"#          // url is required
        let wrongShape = #""just a string""#
        let blob = Data("[\(noURL), \(good), \(wrongShape)]".utf8)

        let salvaged = PluginStoreRegistry.salvage(blob)
        #expect(salvaged.map(\.url) == ["https://a.example/catalog.json"])
        #expect(salvaged.first?.mirrors == ["https://m.example/c.json"])
        // And load() can tell partial loss (rescue the blob) from a clean
        // read or a legitimately empty list (don't).
        #expect(PluginStoreRegistry.entryCount(in: blob) == 3)
        #expect(PluginStoreRegistry.entryCount(in: Data("[]".utf8)) == 0)
        #expect(PluginStoreRegistry.salvage(Data("not json".utf8)).isEmpty)

        // A malformed plugin inside a cached catalog drops that plugin, not
        // the catalog — and not, transitively, every store.
        let catalog = #"""
        [{"url": "https://a.example/catalog.json",
          "catalog": {"name": "S", "plugins": [
            {"name": "Good", "url": "https://a.example/Good.js"},
            {"name": "NoURL"},
            42
        ]}}]
        """#
        let entries = PluginStoreRegistry.salvage(Data(catalog.utf8))
        #expect(entries.count == 1)
        #expect(entries.first?.catalog?.plugins.map(\.name) == ["Good"])
    }

    @MainActor
    @Test func publishStampsTheSignedInAuthor() {
        // The template ships author: "DeskLayer"; the published copy should
        // carry the publisher instead, so the store listing and the
        // downloaded source agree.
        let single = #"plugin.export = { author: "DeskLayer", render };"#
        #expect(PublishPluginSheet.stampAuthor(in: single, username: "qiudaomao")
                == #"plugin.export = { author: "qiudaomao", render };"#)
        // Single-quoted literals too.
        #expect(PublishPluginSheet.stampAuthor(in: "author: 'Old Name',", username: "q")
                == #"author: "q","#)
        // Quotes in a username never break the literal.
        #expect(PublishPluginSheet.stampAuthor(in: single, username: #"a"b"#)
                == #"plugin.export = { author: "a\"b", render };"#)

        // Unclear shapes are left alone rather than guessed at.
        let none = "plugin.export = { render };"
        #expect(PublishPluginSheet.stampAuthor(in: none, username: "q") == none)
        let two = #"author: "A"; other = { author: "B" }"#
        #expect(PublishPluginSheet.stampAuthor(in: two, username: "q") == two)
        #expect(PublishPluginSheet.stampAuthor(in: single, username: "  ") == single)
    }

    @MainActor
    @Test func publishThumbnailIsPixelExact() {
        // Render a 720x520 PNG (the shape a Retina-captured preview has),
        // then check the thumbnail is measured in PIXELS: NSImage.lockFocus
        // used to draw at the screen's 2x scale, produce 960px, blow the
        // 256KB cap, and silently drop the thumbnail from the publish.
        let width = 720, height = 520
        let context = CGContext(data: nil, width: width, height: height,
                                bitsPerComponent: 8, bytesPerRow: 0,
                                space: CGColorSpaceCreateDeviceRGB(),
                                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        for x in stride(from: 0, to: width, by: 8) {
            context.setFillColor(CGColor(red: Double(x) / Double(width), green: 0.4, blue: 0.6, alpha: 1))
            context.fill(CGRect(x: x, y: 0, width: 8, height: height))
        }
        let cg = context.makeImage()!
        let png = NSBitmapImageRep(cgImage: cg).representation(using: .png, properties: [:])!

        let thumb = PublishPluginSheet.thumbnail(from: png)
        #expect(thumb != nil)
        if let thumb, let rep = NSBitmapImageRep(data: thumb) {
            #expect(rep.pixelsWide == 480)
            #expect(rep.pixelsHigh == 346)
            #expect(thumb.count <= 256 * 1024)
        }
        // Garbage in, nil out — never a corrupt upload.
        #expect(PublishPluginSheet.thumbnail(from: Data("not a png".utf8)) == nil)
    }

    @Test func hiddenStoreEntrySurvivesOldFormatAndFiltersFromSidebar() {
        // The community store registers hidden: update checks see its
        // catalog, the sidebar's store categories don't list it. Entries
        // written before the flag existed must still decode (as visible).
        let old = #"[{"url": "https://a.example/catalog.json"}]"#
        let decoded = PluginStoreRegistry.salvage(Data(old.utf8))
        #expect(decoded.first?.isHidden == false)

        let entry = PluginStoreEntry(url: PluginStoreRegistry.communityCatalogURL,
                                     catalog: StoreCatalog(name: "DeskLayer Community", plugins: []),
                                     isHidden: true)
        let data = try? JSONEncoder().encode([entry])
        let back = data.map { PluginStoreRegistry.salvage($0) } ?? []
        #expect(back.first?.isHidden == true)
        // What the sidebar shows and what the update path scans differ by
        // exactly this filter.
        #expect(back.filter { !$0.isHidden }.isEmpty)
        #expect(back.count == 1)
        // And the recorded origin ("DeskLayer Community") matches the hidden
        // entry's display name, which is how storeSource finds the catalog.
        #expect(back.first?.displayName == "DeskLayer Community")
    }

    @Test func galleryAndCommentsDecode() {
        // The gallery endpoint's shape, as served (thumbnail optional).
        let page = """
        {"plugins": [{"name": "HelloCard", "slug": "hellocard",
          "url": "https://s.example/h.js", "version": "1.0.0", "author": "q",
          "cheers": 2, "comments": 1, "downloads": 42, "verified": true,
          "publishedAt": "2026-08-12T16:03:07.763Z",
          "thumbnail": "https://s.example/thumb.png"}],
         "page": 1, "pages": 3, "total": 55}
        """
        struct Page: Decodable { var plugins: [GalleryPlugin]; var pages: Int }
        let decoded = try? JSONDecoder().decode(Page.self, from: Data(page.utf8))
        #expect(decoded?.pages == 3)
        let plugin = decoded?.plugins.first
        #expect(plugin?.slug == "hellocard")
        #expect(plugin?.downloads == 42)
        #expect(plugin?.thumbnail == "https://s.example/thumb.png")
        #expect(plugin?.publishedDate != nil)
        // The install path reuses the catalog shape.
        #expect(plugin?.asStorePlugin.name == "HelloCard")
        #expect(plugin?.asStorePlugin.verified == true)

        let comments = """
        {"comments": [{"id": 13, "author": "q",
          "createdAt": "2026-08-12T23:24:32.573Z", "likes": 0,
          "text": "**bold** move"}], "page": 1, "pages": 1, "total": 1,
         "topicUrl": "https://bbs.example/t/11"}
        """
        let list = try? JSONDecoder().decode(CommunityAccount.CommentsPage.self,
                                             from: Data(comments.utf8))
        #expect(list?.comments.first?.id == 13)
        #expect(list?.comments.first?.createdDate != nil)
        #expect(list?.topicUrl == "https://bbs.example/t/11")
    }

    @Test func communityCatalogExtrasDecode() {
        // The community store adds cheers/comments/verified/topicUrl. They
        // must decode when present and stay nil for ordinary catalogs —
        // and, per the lossy rule, never break an old-format entry.
        let body = """
        {"name": "DeskLayer Community", "website": "https://bbs.byteplayer.app",
         "plugins": [
           {"name": "HelloCard", "url": "https://s.example/h.js", "version": "1.0.0",
            "author": "someone", "cheers": 12, "comments": 3, "verified": true,
            "topicUrl": "https://bbs.byteplayer.app/t/hellocard/11"},
           {"name": "Plain", "url": "https://s.example/p.js"}
        ]}
        """
        let catalog = try? JSONDecoder().decode(StoreCatalog.self, from: Data(body.utf8))
        #expect(catalog?.plugins.count == 2)
        let rich = catalog?.plugins.first
        #expect(rich?.cheers == 12)
        #expect(rich?.comments == 3)
        #expect(rich?.verified == true)
        #expect(rich?.topicUrl == "https://bbs.byteplayer.app/t/hellocard/11")
        let plain = catalog?.plugins.last
        #expect(plain?.cheers == nil && plain?.verified == nil)

        // Round-trips through the store persistence without loss.
        let entry = PluginStoreEntry(url: "https://s.example/catalog.json", catalog: catalog)
        let data = try? JSONEncoder().encode([entry])
        let back = data.map { PluginStoreRegistry.salvage($0) } ?? []
        #expect(back.first?.catalog?.plugins.first?.cheers == 12)
        #expect(back.first?.catalog?.plugins.first?.verified == true)
    }

    @Test func storeCatalogCachesForADay() {
        var entry = PluginStoreEntry(url: "https://example.com/catalog.json")
        entry.catalog = StoreCatalog(name: "S", plugins: [])
        // Never fetched, or fetched longer ago than the window: stale.
        #expect(entry.isFresh() == false)
        entry.fetchedAt = Date().addingTimeInterval(-(PluginStoreEntry.cacheLifetime + 60))
        #expect(entry.isFresh() == false)
        // Inside the window: served from cache, no request on launch.
        entry.fetchedAt = Date().addingTimeInterval(-60)
        #expect(entry.isFresh())
        // A cached timestamp with no catalog is not usable either.
        entry.catalog = nil
        #expect(entry.isFresh() == false)
    }

    @Test func storeCatalogDecodesAndInstalls() async throws {
        // Serve a catalog + plugin from a temp directory via file:// URLs.
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("dl-store-\(UUID().uuidString)", isDirectory: true)
        let installDir = dir.appendingPathComponent("Plugins", isDirectory: true)
        try FileManager.default.createDirectory(at: installDir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        let pluginURL = dir.appendingPathComponent("Greeting.js")
        try """
        let properties = [];
        function render(ctx) {}
        plugin.export = { version: "0.9.0", properties, render };
        """.write(to: pluginURL, atomically: true, encoding: .utf8)

        let catalogURL = dir.appendingPathComponent("catalog.json")
        try """
        {"name": "Demo Store", "plugins": [
          {"name": "Greeting", "description": "Says hello.",
           "preview": "https://example.com/p.png",
           "url": "\(pluginURL.absoluteString)", "version": "0.9.0", "author": "Demo"}
        ]}
        """.write(to: catalogURL, atomically: true, encoding: .utf8)

        let registry = await PluginStoreRegistry()
        let added = await registry.addStore(urlString: catalogURL.absoluteString)
        #expect(added)
        let entry = await registry.stores.first
        #expect(entry?.catalog?.name == "Demo Store")
        #expect(entry?.catalog?.plugins.first?.description == "Says hello.")
        #expect(entry?.catalog?.plugins.first?.preview == "https://example.com/p.png")
        #expect(entry?.displayName == "Demo Store")

        // Installing writes the plugin into the plugins folder…
        let plugin = try #require(entry?.catalog?.plugins.first)
        let error = await registry.install(plugin, from: "Demo Store", into: installDir)
        #expect(error == nil)
        let installed = installDir.appendingPathComponent("Greeting.js")
        #expect(FileManager.default.fileExists(atPath: installed.path))
        // …and remembers which store it came from, so it groups there.
        #expect(PluginStoreRegistry.storeName(forPlugin: "Greeting") == "Demo Store")

        // A bad URL is rejected rather than added.
        let bad = await registry.addStore(urlString: dir.appendingPathComponent("nope.json").absoluteString)
        #expect(bad == false)

        // Cleanup the recorded origin so other runs start clean.
        forgetStoreOrigin("Greeting")
    }

    @Test func pluginOriginClassification() {
        // Nothing ships with the app: a plugin belongs to the store it came
        // from, and everything else is simply installed.
        // A name of this test's own, so a parallel store test can't race it.
        rememberStoreOrigin("OriginProbe", store: "Demo Store")
        defer { forgetStoreOrigin("OriginProbe") }

        #expect(PluginStoreRegistry.storeName(forPlugin: "OriginProbe") == "Demo Store")
        #expect(PluginStoreRegistry.storeName(forPlugin: "MyOwnPlugin") == nil)

        // The group title is localized, so compare against the same lookup
        // rather than an English literal — this suite also runs on machines
        // whose language isn't English.
        #expect(PluginOrigin.user.title == String(localized: "Installed"))
        // A store's name comes from its catalog and is never translated.
        #expect(PluginOrigin.store("Demo Store").title == "Demo Store")
        #expect(PluginOrigin.user.isRemovable)
        #expect(PluginOrigin.store("Demo Store").isRemovable)
        #expect(PluginOrigin.localCases == [.user])
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
