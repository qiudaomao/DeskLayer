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
    @Published var itemID: UUID?
    @Published var displayUUID: String?
}

struct ManagerRootView: View {
    @EnvironmentObject private var store: LayoutStore
    @EnvironmentObject private var registry: PluginRegistry
    @EnvironmentObject private var screens: ScreenManager
    @EnvironmentObject private var coordinator: RuntimeCoordinator
    @StateObject private var selection = ManagerSelection()
    @State private var isInspectorShown = true

    var body: some View {
        NavigationSplitView {
            PluginLibraryView()
                .navigationSplitViewColumnWidth(min: 180, ideal: 220)
        } detail: {
            DesktopCanvasView()
                .inspector(isPresented: $isInspectorShown) {
                    InspectorView()
                        .inspectorColumnWidth(min: 240, ideal: 280, max: 400)
                }
        }
        .frame(minWidth: 900, minHeight: 560)
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
        .toolbar {
            ToolbarItem {
                Button {
                    coordinator.isUserPaused.toggle()
                } label: {
                    Label(
                        coordinator.isUserPaused ? "Resume" : "Pause",
                        systemImage: coordinator.isUserPaused ? "play.fill" : "pause.fill"
                    )
                }
                .help(coordinator.isUserPaused ? "Resume rendering" : "Pause rendering")
            }
            ToolbarItem {
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
