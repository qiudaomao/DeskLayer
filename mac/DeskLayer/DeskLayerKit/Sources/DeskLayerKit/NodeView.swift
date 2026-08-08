//
//  NodeView.swift
//  DeskLayerKit
//
//  Recursive interpreter: ViewNode tree → native SwiftUI. Unknown node
//  types render a visible error placeholder; unknown modifiers show a
//  small warning badge. Never crashes on plugin input. Used by the app's
//  wallpaper/floating hosts and by the widget extension.
//

import AppKit
import AVKit
import SwiftUI
import os

private let nodeLog = Logger(subsystem: "com.qiudaomao.DeskLayer", category: "nodeview")

/// Invoked when an interactive element fires: (actionId, jsonPayload). The
/// payload is a JSON object — `{"x":..,"y":..}` for taps, `{"text":".."}` for
/// text fields, `{}` for a plain button. The app supplies a handler that calls
/// back into plugin JS; the widget passes nil (widgets are non-interactive).
public typealias NodeActionHandler = @Sendable (Int, String) -> Void

public struct NodeView: View {
    let node: ViewNode
    let onAction: NodeActionHandler?

    public init(node: ViewNode, onAction: NodeActionHandler? = nil) {
        self.node = node
        self.onAction = onAction
    }

    public var body: some View {
        node.modifiers.map { applyModifiers($0, to: base) } ?? base
    }

    // MARK: - Node types

    private var base: AnyView {
        let children = node.children ?? []
        switch node.type {
        case "Root", "ZStack":
            return AnyView(ZStack {
                ForEach(Array(children.enumerated()), id: \.offset) { _, child in
                    NodeView(node: child, onAction: onAction)
                }
            })
        case "VStack":
            return AnyView(VStack(spacing: spacing) {
                ForEach(Array(children.enumerated()), id: \.offset) { _, child in
                    NodeView(node: child, onAction: onAction)
                }
            })
        case "HStack":
            return AnyView(HStack(spacing: spacing) {
                ForEach(Array(children.enumerated()), id: \.offset) { _, child in
                    NodeView(node: child, onAction: onAction)
                }
            })
        case "Text":
            return AnyView(Text(node.text ?? ""))
        case "Image":
            let name = node.text ?? ""
            // http(s)/file URL → remote/async image; otherwise an SF Symbol.
            if let url = URL(string: name), let scheme = url.scheme,
               ["http", "https", "file"].contains(scheme.lowercased()) {
                return AnyView(AsyncImage(url: url) { phase in
                    switch phase {
                    case .success(let image): image.resizable().scaledToFit()
                    case .failure: Image(systemName: "photo").foregroundStyle(.secondary)
                    case .empty: ProgressView()
                    @unknown default: Color.clear
                    }
                })
            }
            if NSImage(systemSymbolName: name, accessibilityDescription: nil) != nil {
                return AnyView(Image(systemName: name))
            }
            return errorPlaceholder("no image \"\(name)\"")
        case "Spacer":
            return AnyView(Spacer())
        case "Button":
            let id = actionID(named: "onTap")
            return AnyView(Button(node.text ?? "") {
                if let id { onAction?(id, "{}") }
            }.buttonStyle(.borderless))
        case "Rect":
            // Plain colored rectangle — the primitive for custom bars,
            // dividers, and rules. Color comes from .background(...).
            return AnyView(Rectangle().fill(Color.clear))
        case "Spinner":
            return AnyView(ProgressView().controlSize(.small))
        case "ProgressBar":
            let value = Double(node.text ?? "") ?? 0
            return AnyView(ProgressView(value: min(max(value, 0), 1)))
        case "TextField":
            return AnyView(NodeTextField(
                placeholder: node.text ?? "",
                initial: modifierString(named: "value") ?? "",
                actionID: actionID(named: "onChange"),
                onAction: onAction
            ))
        case "Video":
            let urlString = node.text ?? ""
            let loops = modifierString(named: "loop") == "true"
            let muted = modifierString(named: "muted") != "false"
            if let url = URL(string: urlString), url.scheme != nil {
                return AnyView(NodeVideo(url: url, loops: loops, muted: muted))
            }
            return errorPlaceholder("bad video url")
        default:
            return errorPlaceholder("unknown \(node.type)")
        }
    }

    /// String value of the first arg of a named modifier.
    private func modifierString(named name: String) -> String? {
        node.modifiers?.first { $0.name == name }?.firstString
    }

    /// The action id carried by a modifier (onTap / onTapGesture), if any.
    private func actionID(named name: String) -> Int? {
        guard let modifier = node.modifiers?.first(where: { $0.name == name }),
              let value = modifier.firstDouble else { return nil }
        return Int(value)
    }

    /// `.spacing(n)` is consumed by stack construction, not a view modifier.
    private var spacing: CGFloat? {
        guard let modifier = node.modifiers?.first(where: { $0.name == "spacing" }),
              let value = modifier.firstDouble else { return nil }
        return CGFloat(value)
    }

    private func errorPlaceholder(_ message: String) -> AnyView {
        nodeLog.error("NodeView: \(message, privacy: .public)")
        return AnyView(
            Label(message, systemImage: "exclamationmark.triangle.fill")
                .font(.caption)
                .foregroundStyle(.yellow)
                .padding(4)
                .background(.red.opacity(0.3), in: RoundedRectangle(cornerRadius: 4))
        )
    }

    // MARK: - Modifiers (applied in plugin-declared order)

    private func applyModifiers(_ modifiers: [NodeModifier], to view: AnyView) -> AnyView {
        var current = view
        for modifier in modifiers {
            current = apply(modifier, to: current)
        }
        return current
    }

    private func apply(_ modifier: NodeModifier, to view: AnyView) -> AnyView {
        switch modifier.name {
        case "textColor", "foregroundColor":
            return AnyView(view.foregroundStyle(color(modifier.firstString) ?? .primary))
        case "fontSize", "font":
            let size = modifier.firstDouble ?? 13
            return AnyView(view.font(.system(size: CGFloat(size))))
        case "bold":
            return AnyView(view.bold())
        case "padding":
            if let amount = modifier.firstDouble {
                return AnyView(view.padding(CGFloat(amount)))
            }
            return AnyView(view.padding())
        case "background":
            return AnyView(view.background(color(modifier.firstString) ?? .clear))
        case "cornerRadius":
            let radius = CGFloat(modifier.firstDouble ?? 8)
            return AnyView(view.clipShape(RoundedRectangle(cornerRadius: radius)))
        case "frame":
            // frame(w, h) or frame(w, h, "leading"|"center"|"trailing")
            let width = modifier.args.count > 0 ? modifier.args[0].doubleValue : nil
            let height = modifier.args.count > 1 ? modifier.args[1].doubleValue : nil
            let alignment: Alignment
            switch modifier.args.count > 2 ? modifier.args[2].stringValue : nil {
            case "leading": alignment = .leading
            case "trailing": alignment = .trailing
            default: alignment = .center
            }
            return AnyView(view.frame(
                width: width.map { CGFloat($0) },
                height: height.map { CGFloat($0) },
                alignment: alignment
            ))
        case "lineLimit":
            return AnyView(view.lineLimit(Int(modifier.firstDouble ?? 1)))
        case "opacity":
            return AnyView(view.opacity(modifier.firstDouble ?? 1))
        case "onTapGesture":
            guard let id = modifier.firstDouble.map({ Int($0) }) else { return view }
            return AnyView(view.gesture(SpatialTapGesture().onEnded { value in
                onAction?(id, "{\"x\":\(value.location.x),\"y\":\(value.location.y)}")
            }))
        case "spacing",       // consumed by stack construction
             "onTap",         // consumed by Button
             "onChange",      // consumed by TextField
             "value", "loop", "muted": // consumed by TextField / Video
            return view
        default:
            nodeLog.error("NodeView: unknown modifier \(modifier.name, privacy: .public)")
            return AnyView(view.overlay(alignment: .topTrailing) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .font(.system(size: 9))
                    .foregroundStyle(.yellow)
                    .help("unknown modifier: \(modifier.name)")
            })
        }
    }

    private func color(_ string: String?) -> Color? {
        guard let string, let cgColor = CSSColor.parse(string) else { return nil }
        return Color(cgColor: cgColor)
    }
}

// MARK: - Text input

/// A text field whose edits are reported to the plugin via onChange. Local
/// @State holds the draft; when the plugin re-renders with a different value
/// the tree differs, this view is rebuilt, and the draft re-seeds.
private struct NodeTextField: View {
    let placeholder: String
    let initial: String
    let actionID: Int?
    let onAction: NodeActionHandler?
    @State private var text: String = ""

    var body: some View {
        TextField(placeholder, text: $text)
            .textFieldStyle(.roundedBorder)
            .onAppear { text = initial }
            .onChange(of: text) { _, newValue in
                guard let actionID else { return }
                let payload = ["text": newValue]
                let json = (try? JSONSerialization.data(withJSONObject: payload))
                    .flatMap { String(data: $0, encoding: .utf8) } ?? "{}"
                onAction?(actionID, json)
            }
    }
}

// MARK: - Video

/// Plays a video URL. Optionally muted (default) and looping.
private struct NodeVideo: View {
    let url: URL
    let loops: Bool
    let muted: Bool
    @State private var player: AVPlayer?
    @State private var looper: NSObjectProtocol?

    var body: some View {
        VideoPlayer(player: player)
            .onAppear {
                let player = AVPlayer(url: url)
                player.isMuted = muted
                player.actionAtItemEnd = loops ? .none : .pause
                if loops {
                    looper = NotificationCenter.default.addObserver(
                        forName: .AVPlayerItemDidPlayToEndTime,
                        object: player.currentItem, queue: .main
                    ) { _ in
                        player.seek(to: .zero)
                        player.play()
                    }
                }
                player.play()
                self.player = player
            }
            .onDisappear {
                player?.pause()
                if let looper { NotificationCenter.default.removeObserver(looper) }
            }
    }
}
