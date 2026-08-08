//
//  DesktopCanvasView.swift
//  DeskLayer
//
//  Middle pane: a scaled virtual desktop per display. Items are dragged
//  in from the library, moved and resized in place; every gesture writes
//  normalized frames through the coordinator, so the real wallpaper
//  follows live (moves reposition the CALayer; resizes rebuild on release).
//
//  Coordinates: normalizedFrame is 0…1 with a BOTTOM-left origin (AppKit);
//  SwiftUI is top-left, so y flips at the boundary: viewY = 1 - y - height.
//

import AppKit
import SwiftUI

struct DesktopCanvasView: View {
    @EnvironmentObject private var store: LayoutStore
    @EnvironmentObject private var screens: ScreenManager
    @EnvironmentObject private var coordinator: RuntimeCoordinator
    @EnvironmentObject private var selection: ManagerSelection

    var body: some View {
        VStack(spacing: 0) {
            if screens.controllers.count > 1 {
                Picker("Display", selection: displayBinding) {
                    ForEach(Array(screens.controllers.values), id: \.displayUUID) { controller in
                        Text(controller.screen.localizedName).tag(controller.displayUUID as String?)
                    }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .padding(8)
            }
            GeometryReader { geometry in
                let screenSize = currentScreenSize
                let canvas = fittedRect(container: geometry.size, aspect: screenSize)
                ZStack(alignment: .topLeading) {
                    WallpaperBackground(displayUUID: selection.displayUUID)
                        .frame(width: canvas.width, height: canvas.height)
                        .clipShape(RoundedRectangle(cornerRadius: 6))
                        .overlay(
                            RoundedRectangle(cornerRadius: 6)
                                .strokeBorder(.separator, lineWidth: 1)
                        )
                        .onTapGesture { selection.itemID = nil }

                    ForEach(itemsOnCurrentDisplay) { item in
                        CanvasItemView(item: item, canvasSize: canvas.size)
                    }
                }
                .frame(width: canvas.width, height: canvas.height)
                .position(x: geometry.size.width / 2, y: geometry.size.height / 2)
                .dropDestination(for: String.self) { pluginIDs, location in
                    // location is local to the ZStack, whose frame == canvas.
                    drop(pluginIDs: pluginIDs, at: location, canvas: canvas)
                }
            }
            .padding(12)
        }
        .navigationTitle("Desktop")
    }

    // MARK: - Helpers

    private var displayBinding: Binding<String?> {
        Binding(get: { selection.displayUUID }, set: { selection.displayUUID = $0 })
    }

    private var currentScreenSize: CGSize {
        guard let uuid = selection.displayUUID,
              let controller = screens.controller(forDisplayUUID: uuid)
        else { return NSScreen.main?.frame.size ?? CGSize(width: 16, height: 10) }
        return controller.screen.frame.size
    }

    private var itemsOnCurrentDisplay: [LayoutItem] {
        // Floating items appear too (dashed) so they stay selectable here.
        store.layout.items.filter { $0.displayUUID == selection.displayUUID }
    }

    /// Aspect-fit `aspect` inside `container`, centered.
    private func fittedRect(container: CGSize, aspect: CGSize) -> CGRect {
        guard aspect.width > 0, aspect.height > 0, container.width > 0, container.height > 0 else {
            return .zero
        }
        let scale = min(container.width / aspect.width, container.height / aspect.height)
        let size = CGSize(width: aspect.width * scale, height: aspect.height * scale)
        return CGRect(
            x: (container.width - size.width) / 2,
            y: (container.height - size.height) / 2,
            width: size.width,
            height: size.height
        )
    }

    private func drop(pluginIDs: [String], at location: CGPoint, canvas: CGRect) -> Bool {
        guard let displayUUID = selection.displayUUID else { return false }
        var added = false
        for pluginID in pluginIDs {
            let defaultSize = CGSize(width: 0.2, height: 0.2)
            let nx = min(max(location.x / canvas.width - defaultSize.width / 2, 0), 1 - defaultSize.width)
            let nyTop = min(max(location.y / canvas.height - defaultSize.height / 2, 0), 1 - defaultSize.height)
            let item = LayoutItem(
                pluginID: pluginID,
                displayUUID: displayUUID,
                normalizedFrame: CGRect(
                    x: nx,
                    y: 1 - nyTop - defaultSize.height, // flip to bottom-left origin
                    width: defaultSize.width,
                    height: defaultSize.height
                ),
                zOrder: (store.layout.items.map(\.zOrder).max() ?? 0) + 1
            )
            store.add(item)
            selection.itemID = item.id
            added = true
        }
        return added
    }
}

// MARK: - Item

private struct CanvasItemView: View {
    let item: LayoutItem
    let canvasSize: CGSize

    @EnvironmentObject private var coordinator: RuntimeCoordinator
    @EnvironmentObject private var selection: ManagerSelection

    /// Live gesture frame (view space); nil when idle.
    @State private var dragFrame: CGRect?
    /// Frame captured at drag start. DragGesture.translation is cumulative
    /// from the start, so it must be added to this fixed anchor — not to the
    /// live viewFrame, which setFrame() updates mid-drag (that double-counted
    /// and made the item outrun the cursor).
    @State private var dragAnchor: CGRect?

    var body: some View {
        let frame = dragFrame ?? viewFrame
        let isSelected = selection.itemID == item.id

        ZStack(alignment: .bottomTrailing) {
            // Live frame from the actual running plugin when available;
            // placeholder tint for offline/errored/booting items.
            if let thumbnail = coordinator.thumbnails[item.id] {
                Image(decorative: thumbnail, scale: 1)
                    .resizable()
                    .clipShape(RoundedRectangle(cornerRadius: 4))
            } else {
                RoundedRectangle(cornerRadius: 4)
                    .fill(.blue.opacity(isSelected ? 0.35 : 0.2))
            }
            RoundedRectangle(cornerRadius: 4)
                .strokeBorder(
                    isSelected ? Color.accentColor : .secondary.opacity(0.6),
                    style: StrokeStyle(
                        lineWidth: isSelected ? 2 : 1,
                        dash: item.target == .floatingWindow ? [5, 3] : []
                    )
                )
            VStack {
                if coordinator.thumbnails[item.id] == nil {
                    Text(item.pluginID)
                        .font(.caption)
                        .lineLimit(1)
                        .foregroundStyle(.white)
                        .shadow(radius: 2)
                }
                if coordinator.errorMessage(for: item.id) != nil {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .foregroundStyle(.yellow)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)

            if isSelected {
                ResizeHandle()
                    .gesture(resizeGesture)
            }
        }
        .frame(width: frame.width, height: frame.height)
        .offset(x: frame.minX, y: frame.minY)
        .onTapGesture { selection.itemID = item.id }
        .gesture(moveGesture)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(item.pluginID) item")
        .accessibilityAddTraits(.isButton)
        .accessibilityAction { selection.itemID = item.id }
    }

    // MARK: Geometry

    private var viewFrame: CGRect {
        CGRect(
            x: item.normalizedFrame.minX * canvasSize.width,
            y: (1 - item.normalizedFrame.minY - item.normalizedFrame.height) * canvasSize.height,
            width: item.normalizedFrame.width * canvasSize.width,
            height: item.normalizedFrame.height * canvasSize.height
        )
    }

    private func normalized(from frame: CGRect) -> CGRect {
        CGRect(
            x: frame.minX / canvasSize.width,
            y: 1 - (frame.minY + frame.height) / canvasSize.height,
            width: frame.width / canvasSize.width,
            height: frame.height / canvasSize.height
        )
    }

    // MARK: Gestures

    private var moveGesture: some Gesture {
        DragGesture()
            .onChanged { value in
                selection.itemID = item.id
                let anchor = dragAnchor ?? viewFrame
                if dragAnchor == nil { dragAnchor = anchor }
                var frame = anchor
                frame.origin.x = min(max(anchor.minX + value.translation.width, 0), canvasSize.width - anchor.width)
                frame.origin.y = min(max(anchor.minY + value.translation.height, 0), canvasSize.height - anchor.height)
                dragFrame = frame
                coordinator.setFrame(itemID: item.id, normalizedFrame: normalized(from: frame), commit: false)
            }
            .onEnded { _ in
                if let frame = dragFrame {
                    coordinator.setFrame(itemID: item.id, normalizedFrame: normalized(from: frame), commit: true)
                }
                dragFrame = nil
                dragAnchor = nil
            }
    }

    private var resizeGesture: some Gesture {
        DragGesture()
            .onChanged { value in
                let anchor = dragAnchor ?? viewFrame
                if dragAnchor == nil { dragAnchor = anchor }
                var frame = anchor
                frame.size.width = min(max(anchor.width + value.translation.width, 24), canvasSize.width - anchor.minX)
                frame.size.height = min(max(anchor.height + value.translation.height, 24), canvasSize.height - anchor.minY)
                dragFrame = frame
                coordinator.setFrame(itemID: item.id, normalizedFrame: normalized(from: frame), commit: false)
            }
            .onEnded { _ in
                if let frame = dragFrame {
                    coordinator.setFrame(itemID: item.id, normalizedFrame: normalized(from: frame), commit: true)
                }
                dragFrame = nil
                dragAnchor = nil
            }
    }
}

private struct ResizeHandle: View {
    var body: some View {
        Circle()
            .fill(Color.accentColor)
            .frame(width: 12, height: 12)
            .overlay(Circle().strokeBorder(.white, lineWidth: 1.5))
            .padding(3)
            .contentShape(Rectangle())
    }
}

// MARK: - Wallpaper thumbnail

private struct WallpaperBackground: View {
    let displayUUID: String?
    @EnvironmentObject private var screens: ScreenManager

    var body: some View {
        if let image = wallpaperImage {
            Image(nsImage: image)
                .resizable()
                .aspectRatio(contentMode: .fill)
        } else {
            LinearGradient(
                colors: [Color(red: 0.12, green: 0.16, blue: 0.28), Color(red: 0.05, green: 0.06, blue: 0.12)],
                startPoint: .top,
                endPoint: .bottom
            )
        }
    }

    /// Sandbox note: user-chosen wallpaper files usually aren't readable;
    /// system wallpapers are. Falls back to a gradient either way.
    private var wallpaperImage: NSImage? {
        guard let uuid = displayUUID,
              let controller = screens.controller(forDisplayUUID: uuid),
              let url = NSWorkspace.shared.desktopImageURL(for: controller.screen)
        else { return nil }
        return NSImage(contentsOf: url)
    }
}
