import Foundation

/// The newest GitHub Release, as far as the update flow needs it.
struct ReleaseInfo: Equatable {
    var version: String
    var notes: String
    /// The release's web page, opened when installing in place isn't possible.
    var pageURL: URL
    /// The zipped app attached to the release; nil if the release has no .zip asset.
    var zipURL: URL?
}

enum AppVersion {
    /// The running app's CFBundleShortVersionString ("0" when run outside a bundle, e.g. `swift run`).
    static var current: String { Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0" }

    /// Numeric dot-separated comparison: "v1.10" is newer than "1.9", "1.2" equals "1.2.0".
    /// A pre-release suffix ("1.2.0-beta") is ignored.
    static func isNewer(_ candidate: String, than installed: String) -> Bool {
        let a = components(candidate), b = components(installed)
        for i in 0..<max(a.count, b.count) {
            let x = i < a.count ? a[i] : 0, y = i < b.count ? b[i] : 0
            if x != y { return x > y }
        }
        return false
    }

    static func components(_ version: String) -> [Int] {
        var trimmed = Substring(version.trimmingCharacters(in: .whitespaces))
        if trimmed.first == "v" || trimmed.first == "V" { trimmed = trimmed.dropFirst() }
        let core = trimmed.prefix { $0 != "-" && $0 != "+" }
        return core.split(separator: ".").map { Int($0) ?? 0 }
    }
}

enum UpdateCheckError: LocalizedError {
    case badResponse(Int)

    var errorDescription: String? {
        switch self {
        case let .badResponse(status): "GitHub 回應了 \(status)。"
        }
    }
}

/// Asks the GitHub Releases API for the latest published (non-draft, non-prerelease) release.
struct UpdateChecker {
    static let repository = "xiah6415/shotscreen"
    static let releasesPage = URL(string: "https://github.com/\(repository)/releases")!
    /// Developer override for testing against a local feed: `defaults write local.suyanshot.app updateFeedURL <url>`.
    private static var feedURL: URL {
        if let override = UserDefaults.standard.string(forKey: "updateFeedURL"), let url = URL(string: override) { return url }
        return URL(string: "https://api.github.com/repos/\(repository)/releases/latest")!
    }

    /// Nil when the repository has no release yet.
    func latestRelease() async throws -> ReleaseInfo? {
        var request = URLRequest(url: Self.feedURL, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 20)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("SuYanShot/\(AppVersion.current)", forHTTPHeaderField: "User-Agent")
        let (data, response) = try await URLSession.shared.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        if status == 404 { return nil }
        guard status == 200 else { throw UpdateCheckError.badResponse(status) }
        return try Self.parse(data)
    }

    static func parse(_ data: Data) throws -> ReleaseInfo {
        struct Asset: Decodable { let name: String; let browser_download_url: URL }
        struct Release: Decodable { let tag_name: String; let body: String?; let html_url: URL; let assets: [Asset] }
        let release = try JSONDecoder().decode(Release.self, from: data)
        var version = Substring(release.tag_name)
        if version.first == "v" || version.first == "V" { version = version.dropFirst() }
        return ReleaseInfo(version: String(version),
                           notes: release.body?.trimmingCharacters(in: .whitespacesAndNewlines) ?? "",
                           pageURL: release.html_url,
                           zipURL: release.assets.first { $0.name.lowercased().hasSuffix(".zip") }?.browser_download_url)
    }
}
