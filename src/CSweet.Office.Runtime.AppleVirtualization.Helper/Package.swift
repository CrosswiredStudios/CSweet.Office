// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "CSweet.Office.Runtime.AppleVirtualization.Helper",
    platforms: [.macOS(.v14)],
    products: [
        .executable(
            name: "CSweet.Office.Runtime.AppleVirtualization.Helper",
            targets: ["CSweetAppleVirtualizationHelper"])
    ],
    targets: [
        .executableTarget(
            name: "CSweetAppleVirtualizationHelper",
            path: "Sources/CSweetAppleVirtualizationHelper")
    ]
)
