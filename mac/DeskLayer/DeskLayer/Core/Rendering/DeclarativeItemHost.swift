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
}

/// Root wrapper bound to the host's observable model.
struct RootNodeView: View {
    @ObservedObject var model: DeclarativeTreeModel

    var body: some View {
        Group {
            if let node = model.node {
                NodeView(node: node)
            } else {
                Color.clear
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
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

    init(instance: PluginInstance, frame: CGRect) {
        self.instance = instance
        hostingView = NSHostingView(rootView: RootNodeView(model: model))
        hostingView.frame = frame
        hostingView.wantsLayer = true
        hostingView.layer?.backgroundColor = .clear
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
                guard json != self.lastJSON else { return } // unchanged tree
                self.lastJSON = json
                guard let node = ViewNode.decode(fromJSON: json) else {
                    renderLog.error("[\(instance.pluginID, privacy: .public)] render() returned undecodable tree")
                    return
                }
                self.model.node = node
                self.publishThumbnailIfDue()
                self.onTreeJSON?(json)
            }
        }
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
