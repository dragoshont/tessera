// swift-tools-version: 6.1
import PackageDescription

let package = Package(
    name: "TesseraMacHost",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "TesseraHostCore", targets: ["TesseraHostCore"]),
        .library(name: "TesseraHostMac", targets: ["TesseraHostMac"]),
        .executable(name: "TesseraHostLoginItem", targets: ["TesseraHostLoginItem"]),
        .executable(name: "TesseraHostControl", targets: ["TesseraHostControl"]),
    ],
    targets: [
        .target(name: "TesseraHostCore"),
        .target(name: "TesseraHostMac", dependencies: ["TesseraHostCore"]),
        .executableTarget(name: "TesseraHostLoginItem", dependencies: ["TesseraHostCore", "TesseraHostMac"]),
        .executableTarget(name: "TesseraHostControl", dependencies: ["TesseraHostCore", "TesseraHostMac"]),
        .testTarget(name: "TesseraHostCoreTests", dependencies: ["TesseraHostCore"]),
        .testTarget(name: "TesseraHostMacTests", dependencies: ["TesseraHostCore", "TesseraHostMac"]),
    ],
    swiftLanguageModes: [.v6]
)