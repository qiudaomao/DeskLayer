// swift-tools-version: 5.9
import PackageDescription

// Sparkle, wrapped for the app.
//
// It lives in a local package rather than as a direct project dependency
// because this project's format (objectVersion 77) refuses to open with an
// XCRemoteSwiftPackageReference in it — any remote reference, regardless of
// its version requirement, makes xcodebuild report "Unable to read project".
// A local package resolves the remote dependency through SPM instead, which
// works, and keeps Sparkle out of the widget extension (DeskLayerKit is
// shared with the appex; this is not).
let package = Package(
    name: "DeskLayerUpdater",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "DeskLayerUpdater", targets: ["DeskLayerUpdater"])
    ],
    dependencies: [
        .package(url: "https://github.com/sparkle-project/Sparkle", from: "2.6.0")
    ],
    targets: [
        .target(
            name: "DeskLayerUpdater",
            dependencies: [.product(name: "Sparkle", package: "Sparkle")]
        )
    ]
)
