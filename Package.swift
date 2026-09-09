// swift-tools-version: 5.10
import PackageDescription

let package = Package(
    name: "SuYanShot",
    platforms: [.macOS(.v14)],
    products: [.executable(name: "SuYanShot", targets: ["SuYanShot"])],
    targets: [
        .executableTarget(name: "SuYanShot"),
        .testTarget(name: "SuYanShotTests", dependencies: ["SuYanShot"])
    ]
)
