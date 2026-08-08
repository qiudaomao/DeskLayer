//
//  ManagerRootView.swift
//  DeskLayer
//
//  Xcode-style 3-pane layout: plugin library | virtual desktop | inspector.
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

    var body: some View {
        NavigationSplitView {
            PluginLibraryView()
                .navigationSplitViewColumnWidth(min: 180, ideal: 220)
        } content: {
            DesktopCanvasView()
                .navigationSplitViewColumnWidth(min: 420, ideal: 640)
        } detail: {
            InspectorView()
                .navigationSplitViewColumnWidth(min: 240, ideal: 280)
        }
        .frame(minWidth: 960, minHeight: 600)
        .environmentObject(selection)
        .onAppear {
            if selection.displayUUID == nil {
                selection.displayUUID = NSScreen.main.flatMap(ScreenManager.displayUUID(for:))
                    ?? screens.controllers.keys.first
            }
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
        }
    }
}
