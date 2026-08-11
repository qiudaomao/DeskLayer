//
//  ManagerRootView.swift
//  DeskLayer
//
//  Native macOS shell: sidebar (plugin library) | desktop canvas, with the
//  property editor in a system inspector panel (hideable, standard
//  material) — the same structure as Xcode/Freeform.
//

import Combine
import SwiftUI

@MainActor
final class ManagerSelection: ObservableObject {
    /// A placed item on the desktop canvas…
    @Published var itemID: UUID? {
        // Clears every library selection, not just pluginID: the inspector
        // checks the store branches first, so a leftover store selection
        // would keep showing while the user clicks items on the canvas.
        didSet { if itemID != nil { pluginID = nil; storeID = nil; storePlugin = nil } }
    }
    /// …or a plugin picked in the library. The inspector shows whichever is
    /// selected; they're mutually exclusive.
    @Published var pluginID: String? {
        didSet { if pluginID != nil { itemID = nil; storeID = nil; storePlugin = nil } }
    }
    /// …or a store category (shows the store's details).
    @Published var storeID: String? {
        didSet { if storeID != nil { itemID = nil; pluginID = nil; storePlugin = nil } }
    }
    /// …or a not-yet-installed plugin listed by a store: (storeURL, name).
    @Published var storePlugin: StorePluginRef? {
        didSet { if storePlugin != nil { itemID = nil; pluginID = nil; storeID = nil } }
    }
    @Published var displayUUID: String?
}

nonisolated struct StorePluginRef: Hashable {
    let storeID: String
    let name: String
}

struct ManagerRootView: View {
    @EnvironmentObject private var store: LayoutStore
    @EnvironmentObject private var registry: PluginRegistry
    @EnvironmentObject private var screens: ScreenManager
    @EnvironmentObject private var coordinator: RuntimeCoordinator
    @StateObject private var selection = ManagerSelection()
    @State private var isInspectorShown = true
    @State private var columnVisibility = NavigationSplitViewVisibility.all

    var body: some View {
        NavigationSplitView(columnVisibility: $columnVisibility) {
            PluginLibraryView()
                .navigationSplitViewColumnWidth(min: 180, ideal: 220)
        } detail: {
            DesktopCanvasView()
        }
        // Attached to the split view (not inside detail) so toggling never
        // re-partitions the columns — the sidebar must not blink.
        .inspector(isPresented: $isInspectorShown) {
            InspectorView()
                .inspectorColumnWidth(min: 240, ideal: 280, max: 400)
        }
        .environmentObject(selection)
        .onAppear {
            if selection.displayUUID == nil {
                selection.displayUUID = NSScreen.main.flatMap(ScreenManager.displayUUID(for:))
                    ?? screens.controllers.keys.first
            }
        }
        .onChange(of: selection.itemID) { _, newValue in
            // Selecting an item reveals the inspector, Finder-style.
            if newValue != nil { isInspectorShown = true }
        }
        .onChange(of: selection.pluginID) { _, newValue in
            if newValue != nil { isInspectorShown = true }
        }
        .onChange(of: selection.storeID) { _, newValue in
            if newValue != nil { isInspectorShown = true }
        }
        .onChange(of: selection.storePlugin) { _, newValue in
            if newValue != nil { isInspectorShown = true }
        }
        .toolbar {
            // Right-aligned, Xcode-style: actions at the trailing edge.
            ToolbarItemGroup(placement: .primaryAction) {
                Button {
                    coordinator.isUserPaused.toggle()
                } label: {
                    Label(
                        coordinator.isUserPaused ? "Resume" : "Pause",
                        systemImage: coordinator.isUserPaused ? "play.fill" : "pause.fill"
                    )
                }
                .help(coordinator.isUserPaused ? "Resume rendering" : "Pause rendering")

                Button {
                    isInspectorShown.toggle()
                } label: {
                    Label("Inspector", systemImage: "sidebar.trailing")
                }
                .help(isInspectorShown ? "Hide Inspector" : "Show Inspector")
            }
        }
    }
}
