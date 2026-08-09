//
//  DeclarativeItemHost.swift
//  DeskLayer
//
//  Runs one declarative item: calls the plugin's render() on its queue at
//  the declared fps (or only on property changes when no fps is declared),
//  decodes the JSON tree off-main, skips unchanged trees, and publishes
//  into an NSHostingView living in the desktop window.
//

import AppKit
import Combine
import DeskLayerKit
import ImageIO
import SwiftUI
import os

@MainActor
final class DeclarativeTreeModel: ObservableObject {
    @Published var node: ViewNode?
    /// Set once by the host; forwards interactive events into plugin JS.
    var onAction: NodeActionHandler?
}

/// Root wrapper bound to the host's observable model.
struct RootNodeView: View {
    @ObservedObject var model: DeclarativeTreeModel

    var body: some View {
        Group {
            if let node = model.node {
                NodeView(node: node, onAction: model.onAction)
            } else {
                Color.clear
            }
        }
        // Top-leading, not the default centre: an item is placed by its
        // top-left corner, and until the frame catches up with a newly
        // measured content size the tree is laid out in the old bounds —
        // centred, that reads as the whole plugin sliding as it grows.
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }
}

@MainActor
final class DeclarativeItemHost {
    let instance: PluginInstance
    let hostingView: NSHostingView<RootNodeView>
    private let model = DeclarativeTreeModel()
    private var timer: Timer?
    private var lastJSON: String?
    private var lastThumbnailTime: CFTimeInterval = 0

    var isPaused = false
    /// Throttled preview for the manager's virtual desktop (main thread).
    var onThumbnail: ((CGImage) -> Void)?
    /// Fires on main whenever a changed tree is committed (widget publishing).
    var onTreeJSON: ((String) -> Void)?
    /// The content's natural size after a render, when it differs from the
    /// item's frame. SwiftUI lays out at its ideal size and NSHostingView
    /// doesn't clip, so without this the desktop would draw outside the rect
    /// the manager shows — the two would disagree.
    var onContentSize: ((CGSize) -> Void)?
    private var lastReportedSize: CGSize = .zero

    init(instance: PluginInstance, frame: CGRect) {
        self.instance = instance
        hostingView = NSHostingView(rootView: RootNodeView(model: model))
        hostingView.frame = frame
        hostingView.wantsLayer = true
        hostingView.layer?.backgroundColor = .clear
        // Interactive elements call back into plugin JS on its own queue,
        // then a re-render reflects any state change. Only reachable in
        // floating windows — the wallpaper layer ignores mouse events.
        model.onAction = { [weak self] id, payload in
            // SwiftUI delivers taps/edits on the main thread.
            MainActor.assumeIsolated { self?.handleAction(id: id, payload: payload) }
        }
    }

    private func handleAction(id: Int, payload: String) {
        let instance = instance
        instance.queue.async { [weak self] in
            instance.invokeAction(id: id, payloadJSON: payload)
            DispatchQueue.main.async { self?.renderOnce() }
        }
    }

    func start() {
        renderOnce()
        // Static plugins (no declared fps/interval, or fps 0) re-render only
        // on property edits. Any finite cadence — 60fps down to hours — ticks.
        guard instance.hasDeclaredCadence, instance.renderInterval.isFinite else { return }
        timer = Timer.scheduledTimer(withTimeInterval: instance.renderInterval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, !self.isPaused else { return }
                self.renderOnce()
            }
        }
        timer?.tolerance = min(instance.renderInterval * 0.1, 30)
    }

    func stop() {
        timer?.invalidate()
        timer = nil
        hostingView.removeFromSuperview()
    }

    func renderOnce() {
        let instance = instance
        instance.queue.async { [weak self] in
            guard let json = instance.callRenderTree() else { return }
            DispatchQueue.main.async {
                guard let self else { return }
                if json != self.lastJSON {
                    self.lastJSON = json
                    guard let node = ViewNode.decode(fromJSON: json) else {
                        renderLog.error("[\(instance.pluginID, privacy: .public)] render() returned undecodable tree")
                        return
                    }
                    self.model.node = node
                    self.onTreeJSON?(json)
                    self.publishThumbnailIfDue()
                    // SwiftUI lays the new tree out after this turn of the
                    // run loop, so the measurement below still sees the old
                    // one. Measure again once it has, or the frame trails the
                    // content by a whole render — seconds, on a slow cadence.
                    DispatchQueue.main.async { self.reportContentSizeIfChanged() }
                }
                // Safety net: an unchanged tree still gets measured, so a
                // frame that missed its correction can never stay wrong.
                self.reportContentSizeIfChanged()
            }
        }
    }

    /// SwiftUI's ideal size for the current tree. Reported only when it
    /// actually changes, so this can't feed back into a resize loop.
    private func reportContentSizeIfChanged() {
        guard let onContentSize else { return }
        // Flush any pending SwiftUI layout so this measures the tree that is
        // on screen rather than the one before it.
        hostingView.layoutSubtreeIfNeeded()
        let ideal = hostingView.fittingSize
        guard ideal.width > 1, ideal.height > 1 else { return }
        guard abs(ideal.width - lastReportedSize.width) > 1
                || abs(ideal.height - lastReportedSize.height) > 1 else { return }
        lastReportedSize = ideal
        onContentSize(ideal)
    }

    private func publishThumbnailIfDue() {
        guard let onThumbnail else { return }
        let now = CACurrentMediaTime()
        guard now - lastThumbnailTime > 0.5 else { return }
        lastThumbnailTime = now
        let renderer = ImageRenderer(content: RootNodeView(model: model)
            .frame(width: hostingView.frame.width, height: hostingView.frame.height))
        renderer.scale = 2
        if let image = renderer.cgImage {
            onThumbnail(image)
        }
    }

    /// Debug: current tree as PNG over the same stderr channel canvas
    /// items use (sandbox container is unreadable from outside).
    func writeDebugSnapshot(to url: URL) {
        let renderer = ImageRenderer(content: RootNodeView(model: model)
            .frame(width: hostingView.frame.width, height: hostingView.frame.height))
        renderer.scale = 2
        guard let image = renderer.cgImage,
              let destination = CGImageDestinationCreateWithURL(url as CFURL, "public.png" as CFString, 1, nil)
        else { return }
        CGImageDestinationAddImage(destination, image, nil)
        CGImageDestinationFinalize(destination)
        if let png = try? Data(contentsOf: url) {
            FileHandle.standardError.write(Data("SNAPSHOT:\(instance.pluginID):\(png.base64EncodedString())\n".utf8))
        }
    }
}
