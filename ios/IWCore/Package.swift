// swift-tools-version: 5.9
import PackageDescription

// Platform-independent protocol + crypto, testable with `swift test` on macOS (no simulator needed).
let package = Package(
    name: "IWCore",
    platforms: [.iOS(.v16), .macOS(.v13)],
    products: [.library(name: "IWCore", targets: ["IWCore"])],
    targets: [
        .target(name: "IWCore"),
        .testTarget(name: "IWCoreTests", dependencies: ["IWCore"]),
    ]
)
