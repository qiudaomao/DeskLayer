// swift-tools-version: 5.9
import PackageDescription

// Code shared between the DeskLayer app and the widget extension:
// the declarative view tree + SwiftUI interpreter, CSS color parsing,
// and the widget payload format exchanged via the App Group container.
let package = Package(
    name: "DeskLayerKit",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "DeskLayerKit", targets: ["DeskLayerKit"])
    ],
    targets: [
        .target(name: "DeskLayerKit")
    ]
)
