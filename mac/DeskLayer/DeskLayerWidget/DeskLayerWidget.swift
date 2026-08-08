//
//  DeskLayerWidget.swift
//  DeskLayerWidget
//
//  Real macOS widget showing a DeskLayer item. The widget process never
//  runs plugin JS: the app publishes payloads (view-tree JSON for
//  declarative plugins, PNG snapshots for canvas plugins) into the App
//  Group container, and this extension just renders them — declarative
//  trees through the same NodeView interpreter the wallpaper uses.
//

import AppIntents
import DeskLayerKit
import SwiftUI
import WidgetKit

@main
struct DeskLayerWidgetBundle: WidgetBundle {
    var body: some Widget {
        DeskLayerItemWidget()
    }
}

// MARK: - Configuration (pick which item to show)

struct WidgetItemEntity: AppEntity {
    static let typeDisplayRepresentation: TypeDisplayRepresentation = "DeskLayer Item"
    static let defaultQuery = WidgetItemQuery()

    var id: String
    var name: String

    var displayRepresentation: DisplayRepresentation {
        DisplayRepresentation(title: "\(name)")
    }
}

struct WidgetItemQuery: EntityQuery {
    func entities(for identifiers: [String]) async throws -> [WidgetItemEntity] {
        WidgetPayloadStore.readAll()
            .filter { identifiers.contains($0.itemID) }
            .map { WidgetItemEntity(id: $0.itemID, name: $0.pluginID) }
    }

    func suggestedEntities() async throws -> [WidgetItemEntity] {
        WidgetPayloadStore.readAll().map { WidgetItemEntity(id: $0.itemID, name: $0.pluginID) }
    }

    func defaultResult() async -> WidgetItemEntity? {
        try? await suggestedEntities().first
    }
}

struct SelectItemIntent: WidgetConfigurationIntent {
    static let title: LocalizedStringResource = "Select Item"
    static let description = IntentDescription("Choose which DeskLayer item to show.")

    @Parameter(title: "Item")
    var item: WidgetItemEntity?
}

// MARK: - Timeline

struct ItemEntry: TimelineEntry {
    let date: Date
    let payload: WidgetPayload?
    let image: NSImage?
}

struct Provider: AppIntentTimelineProvider {
    func placeholder(in context: Context) -> ItemEntry {
        ItemEntry(date: .now, payload: nil, image: nil)
    }

    func snapshot(for configuration: SelectItemIntent, in context: Context) async -> ItemEntry {
        entry(for: configuration)
    }

    func timeline(for configuration: SelectItemIntent, in context: Context) async -> Timeline<ItemEntry> {
        // The app pokes WidgetCenter on changes; this is the fallback cadence.
        Timeline(
            entries: [entry(for: configuration)],
            policy: .after(.now.addingTimeInterval(15 * 60))
        )
    }

    private func entry(for configuration: SelectItemIntent) -> ItemEntry {
        let all = WidgetPayloadStore.readAll()
        let payload = configuration.item.flatMap { chosen in all.first { $0.itemID == chosen.id } } ?? all.first
        var image: NSImage?
        if let payload, payload.kind == .canvas,
           let url = WidgetPayloadStore.imageURL(for: payload.itemID) {
            image = NSImage(contentsOf: url)
        }
        return ItemEntry(date: .now, payload: payload, image: image)
    }
}

// MARK: - Views

struct ItemWidgetView: View {
    let entry: ItemEntry

    var body: some View {
        Group {
            if let payload = entry.payload {
                switch payload.kind {
                case .declarative:
                    if let json = payload.treeJSON, let node = ViewNode.decode(fromJSON: json) {
                        NodeView(node: node)
                    } else {
                        unavailable
                    }
                case .canvas:
                    if let image = entry.image {
                        Image(nsImage: image)
                            .resizable()
                            .aspectRatio(contentMode: .fit)
                    } else {
                        unavailable
                    }
                }
            } else {
                unavailable
            }
        }
        .containerBackground(for: .widget) {
            Color(red: 0.06, green: 0.07, blue: 0.12)
        }
    }

    private var unavailable: some View {
        VStack(spacing: 6) {
            Image(systemName: "square.3.layers.3d.down.left")
                .font(.system(size: 28))
                .foregroundStyle(.secondary)
            Text("Open DeskLayer to publish items")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
        }
    }
}

struct DeskLayerItemWidget: Widget {
    let kind = "DeskLayerItemWidget"

    var body: some WidgetConfiguration {
        AppIntentConfiguration(kind: kind, intent: SelectItemIntent.self, provider: Provider()) { entry in
            ItemWidgetView(entry: entry)
        }
        .configurationDisplayName("DeskLayer")
        .description("Shows a DeskLayer plugin item.")
        .supportedFamilies([.systemSmall, .systemMedium, .systemLarge])
    }
}
