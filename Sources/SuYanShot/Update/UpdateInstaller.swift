import AppKit
import CryptoKit
import Security

enum UpdateInstallError: LocalizedError {
    case noDownload
    case notInstalledAsApp
    case unzipFailed
    case noAppInArchive
    case wrongBundle(String?)
    case notNewer(String)
    case installedAppUnsigned
    case signatureMismatch(OSStatus)

    var errorDescription: String? {
        switch self {
        case .noDownload: "這個版本沒有附上 App 的壓縮檔。"
        case .notInstalledAsApp: "目前執行的不是安裝好的 App。"
        case .unzipFailed: "沒辦法解壓縮下載的檔案。"
        case .noAppInArchive: "壓縮檔裡找不到 App。"
        case let .wrongBundle(id): "壓縮檔裡的 App 不是拾光（\(id ?? "沒有 bundle id")）。"
        case let .notNewer(version): "壓縮檔裡的版本 \(version) 沒有比目前的新。"
        case .installedAppUnsigned: "目前的 App 沒有用固定的憑證簽名，沒辦法確認新版是同一個來源。"
        case let .signatureMismatch(status): "新版沒有通過簽名檢查（不是同一張憑證簽的，或檔案被改過；\(status)），為了安全沒有安裝。"
        }
    }
}

/// Downloads a release zip, checks that it holds this app signed with the same certificate, and swaps it in.
struct UpdateInstaller {
    /// The bundle being replaced: the running app.
    let installedApp: URL

    init(installedApp: URL = Bundle.main.bundleURL) { self.installedApp = installedApp }

    /// Downloads and unzips the release next to the installed app and returns the verified new .app.
    func prepare(_ release: ReleaseInfo) async throws -> URL {
        guard installedApp.pathExtension == "app" else { throw UpdateInstallError.notInstalledAsApp }
        guard let zipURL = release.zipURL else { throw UpdateInstallError.noDownload }
        // Same volume as the installed app, so the final swap is a rename.
        let work = try FileManager.default.url(for: .itemReplacementDirectory, in: .userDomainMask, appropriateFor: installedApp, create: true)
        do {
            let (downloaded, response) = try await URLSession.shared.download(from: zipURL)
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard status == 200 else { throw UpdateCheckError.badResponse(status) }
            let zip = work.appendingPathComponent("update.zip")
            try FileManager.default.moveItem(at: downloaded, to: zip)
            let expanded = work.appendingPathComponent("expanded", isDirectory: true)
            try unzip(zip, to: expanded)
            let app = try Self.findApp(in: expanded)
            try verify(app)
            return app
        } catch {
            try? FileManager.default.removeItem(at: work)
            throw error
        }
    }

    /// Replaces the installed app with the verified one from `prepare` and returns where it now lives.
    /// A release whose bundle has a new file name (素顏 Shot.app → Glean.app) is renamed to match, if that name is free.
    func install(_ app: URL) throws -> URL {
        let work = app.deletingLastPathComponent().deletingLastPathComponent()
        var installed = try FileManager.default.replaceItemAt(installedApp, withItemAt: app) ?? installedApp
        let renamed = installed.deletingLastPathComponent().appendingPathComponent(app.lastPathComponent)
        if renamed.lastPathComponent != installed.lastPathComponent, !FileManager.default.fileExists(atPath: renamed.path),
           (try? FileManager.default.moveItem(at: installed, to: renamed)) != nil {
            installed = renamed
        }
        // The rest of the work folder (zip, empty expanded folder).
        try? FileManager.default.removeItem(at: work)
        return installed
    }

    /// Opens the installed app again once this process has exited, then quits.
    @MainActor
    func relaunch(_ app: URL) throws {
        let waiter = Process()
        waiter.executableURL = URL(fileURLWithPath: "/bin/sh")
        waiter.arguments = ["-c", "while kill -0 \(ProcessInfo.processInfo.processIdentifier) 2>/dev/null; do sleep 0.2; done; /usr/bin/open \"$0\"",
                            app.path]
        try waiter.run()
        NSApp.terminate(nil)
    }

    /// Same bundle id, a newer version, and a valid signature from the installed app's leaf certificate.
    func verify(_ app: URL) throws {
        let bundleID = Bundle(url: installedApp)?.bundleIdentifier
        let candidate = Bundle(url: app)
        guard let bundleID, candidate?.bundleIdentifier == bundleID else { throw UpdateInstallError.wrongBundle(candidate?.bundleIdentifier) }
        let version = candidate?.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
        let installedVersion = Bundle(url: installedApp)?.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
        guard AppVersion.isNewer(version, than: installedVersion) else { throw UpdateInstallError.notNewer(version) }

        guard let leafHash = Self.leafCertificateSHA1(of: installedApp) else { throw UpdateInstallError.installedAppUnsigned }
        var requirement: SecRequirement?
        let text = "identifier \"\(bundleID)\" and certificate leaf = H\"\(leafHash)\""
        guard SecRequirementCreateWithString(text as CFString, [], &requirement) == errSecSuccess, let requirement,
              let code = Self.staticCode(app) else { throw UpdateInstallError.signatureMismatch(errSecCSUnimplemented) }
        let flags = SecCSFlags(rawValue: kSecCSCheckAllArchitectures | kSecCSStrictValidate | kSecCSCheckNestedCode)
        let status = SecStaticCodeCheckValidity(code, flags, requirement)
        guard status == errSecSuccess else { throw UpdateInstallError.signatureMismatch(status) }
    }

    /// Hex SHA-1 of the signing certificate itself, the form a `certificate leaf = H"…"` requirement uses.
    /// Nil for ad-hoc signed or unsigned bundles.
    static func leafCertificateSHA1(of app: URL) -> String? {
        var info: CFDictionary?
        guard let code = staticCode(app),
              SecCodeCopySigningInformation(code, SecCSFlags(rawValue: kSecCSSigningInformation), &info) == errSecSuccess,
              let certificates = (info as? [String: Any])?[kSecCodeInfoCertificates as String] as? [SecCertificate],
              let leaf = certificates.first else { return nil }
        let data = SecCertificateCopyData(leaf) as Data
        return Insecure.SHA1.hash(data: data).map { String(format: "%02X", $0) }.joined()
    }

    private static func staticCode(_ url: URL) -> SecStaticCode? {
        var code: SecStaticCode?
        guard SecStaticCodeCreateWithPath(url as CFURL, [], &code) == errSecSuccess else { return nil }
        return code
    }

    private func unzip(_ zip: URL, to folder: URL) throws {
        let ditto = Process()
        ditto.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        ditto.arguments = ["-x", "-k", zip.path, folder.path]
        try ditto.run()
        ditto.waitUntilExit()
        guard ditto.terminationStatus == 0 else { throw UpdateInstallError.unzipFailed }
    }

    private static func findApp(in folder: URL) throws -> URL {
        let items = try FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)
        let apps = items.filter { $0.pathExtension == "app" }
        guard apps.count == 1 else { throw UpdateInstallError.noAppInArchive }
        return apps[0]
    }
}
