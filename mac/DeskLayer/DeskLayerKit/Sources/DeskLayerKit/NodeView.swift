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
import SwiftUI
import os

private let nodeLog = Logger(subsystem: "com.qiudaomao.DeskLayer", category: "nodeview")

public struct NodeView: View {
    let node: ViewNode

    public init(node: ViewNode) {
        self.node = node
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
                    NodeView(node: child)
                }
            })
        case "VStack":
            return AnyView(VStack(spacing: spacing) {
                ForEach(Array(children.enumerated()), id: \.offset) { _, child in
                    NodeView(node: child)
                }
            })
        case "HStack":
            return AnyView(HStack(spacing: spacing) {
                ForEach(Array(children.enumerated()), id: \.offset) { _, child in
                    NodeView(node: child)
                }
            })
        case "Text":
            return AnyView(Text(node.text ?? ""))
        case "Image":
            let name = node.text ?? ""
            if NSImage(systemSymbolName: name, accessibilityDescription: nil) != nil {
                return AnyView(Image(systemName: name))
            }
            return errorPlaceholder("no image \"\(name)\"")
        case "Spacer":
            return AnyView(Spacer())
        default:
            return errorPlaceholder("unknown \(node.type)")
        }
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
            let width = modifier.args.count > 0 ? modifier.args[0].doubleValue : nil
            let height = modifier.args.count > 1 ? modifier.args[1].doubleValue : nil
            return AnyView(view.frame(
                width: width.map { CGFloat($0) },
                height: height.map { CGFloat($0) }
            ))
        case "opacity":
            return AnyView(view.opacity(modifier.firstDouble ?? 1))
        case "spacing":
            return view // consumed by stack construction
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
