import AppKit
import CoreImage.CIFilterBuiltins
import SwiftUI

/// 手機分享設定: the on/off switch, the fixed address (with QR code), password, signed-in devices,
/// the local name, and which networks sharing may run on.
@MainActor
final class PhoneShareWindowController: NSObject {
    private var window: NSWindow?
    private let share: PhoneShare

    init(share: PhoneShare) { self.share = share }

    func show() {
        if window == nil {
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 460, height: 640), styleMask: [.titled, .closable], backing: .buffered, defer: false)
            window.title = "手機分享設定"
            window.isReleasedWhenClosed = false
            window.contentView = NSHostingView(rootView: PhoneShareView(share: share, auth: share.auth))
            window.center()
            self.window = window
        }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}

private struct PhoneShareView: View {
    @ObservedObject var share: PhoneShare
    @ObservedObject var auth: PhoneShareAuth
    @State private var password = ""
    @State private var confirmation = ""
    @State private var passwordMessage: String?
    @State private var nameDraft = ""

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                Toggle(isOn: Binding(get: { share.isEnabled }, set: { share.setEnabled($0) })) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("手機分享").font(.headline)
                        Text("打開後，拾光啟動時會自動開始分享。").font(.caption).foregroundStyle(.secondary)
                    }
                }
                .toggleStyle(.switch)

                statusRow

                if let url = share.url, share.isServing {
                    addressSection(url)
                }

                Divider()
                passwordSection

                if !auth.devices.isEmpty {
                    Divider()
                    devicesSection
                }

                Divider()
                nameSection

                Divider()
                networkSection

                Text("連線沒有加密，所以只建議在自己家裡、信任的網路使用。手機能看到剪貼簿紀錄，也能傳文字或照片到這台 Mac。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(22)
        }
        .frame(width: 460, height: 640)
        .onAppear { nameDraft = share.localName }
    }

    @ViewBuilder private var statusRow: some View {
        let (text, color, symbol): (String, Color, String) = switch share.status {
        case .off: ("已關閉", .secondary, "iphone.slash")
        case .needsPassword: ("先在下面設定密碼，才會開始分享。", .orange, "key")
        case .noNetwork: ("沒有連上網路，等連上後會自動開始。", .orange, "wifi.slash")
        case .otherNetwork: ("目前的網路不在允許清單，已暫停分享。回到允許的網路會自動恢復。", .orange, "wifi.exclamationmark")
        case .starting: ("啟動中…", .secondary, "hourglass")
        case .running: (share.lastActivity.map { "分享中 · 手機上次使用 \(Self.relative($0))" } ?? "分享中，等待手機連線", .green, "dot.radiowaves.left.and.right")
        case let .failed(message): ("無法開始分享：\(message)", .red, "exclamationmark.triangle")
        }
        Label(text, systemImage: symbol)
            .font(.callout)
            .foregroundStyle(color)
            .fixedSize(horizontal: false, vertical: true)
    }

    private func addressSection(_ url: URL) -> some View {
        HStack(alignment: .top, spacing: 16) {
            if let qr = QRCode.image(for: url.absoluteString) {
                Image(nsImage: qr).interpolation(.none).resizable().frame(width: 132, height: 132)
                    .padding(8).background(.white, in: RoundedRectangle(cornerRadius: 10))
            }
            VStack(alignment: .leading, spacing: 8) {
                Text("在手機上打開").font(.subheadline.weight(.semibold))
                Text(url.absoluteString).font(.system(size: 15, weight: .semibold, design: .monospaced)).textSelection(.enabled)
                Text("每台 Mac 上的拾光都用這個網址，所以換 Mac 或給別人用，同一個捷徑都能用。用 Safari 打開後，按「分享 → 加入主畫面」就會變成一個 App。")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                if let ipURL = share.ipURL {
                    Text("打不開的話改用：\(ipURL.absoluteString)").font(.caption2).foregroundStyle(.secondary).textSelection(.enabled)
                }
            }
        }
    }

    private var passwordSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(auth.hasPassword ? "更改密碼" : "設定密碼").font(.subheadline.weight(.semibold))
            HStack {
                SecureField("新密碼（至少 \(PhoneShareAuth.minimumPasswordLength) 個字）", text: $password)
                SecureField("再輸入一次", text: $confirmation)
            }
            .textFieldStyle(.roundedBorder)
            HStack {
                Button(auth.hasPassword ? "更改密碼" : "設定密碼") { savePassword() }
                    .disabled(password.isEmpty)
                if let passwordMessage { Text(passwordMessage).font(.caption).foregroundStyle(.secondary) }
            }
            if auth.hasPassword {
                Text("更改密碼後，所有裝置都要重新登入。").font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    private func savePassword() {
        guard password.count >= PhoneShareAuth.minimumPasswordLength else { passwordMessage = "密碼太短了"; return }
        guard password == confirmation else { passwordMessage = "兩次輸入的不一樣"; return }
        share.setPassword(password)
        password = ""
        confirmation = ""
        passwordMessage = "已儲存"
    }

    private var devicesSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("已登入的裝置").font(.subheadline.weight(.semibold))
                Spacer()
                Button("全部登出") { auth.revokeAll() }
            }
            ForEach(auth.devices) { device in
                HStack {
                    Image(systemName: "iphone")
                    VStack(alignment: .leading, spacing: 1) {
                        Text(device.name)
                        Text("最近使用 \(Self.relative(device.lastSeen))").font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button("移除") { auth.revoke(device.id) }
                }
            }
        }
    }

    private var nameSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("網址名稱").font(.subheadline.weight(.semibold))
            HStack(spacing: 4) {
                Text("http://")
                TextField("glean", text: $nameDraft).textFieldStyle(.roundedBorder).frame(width: 140)
                Text(".local:\(String(PhoneShare.port))")
                Spacer()
                Button("套用") { share.setLocalName(nameDraft); nameDraft = share.localName }
                    .disabled(LocalNameAdvertiser.sanitize(nameDraft) == share.localName)
            }
            .font(.system(.body, design: .monospaced))
            Text("維持預設的 glean，換 Mac 時捷徑才不用改。同一個網路裡有兩台 Mac 都開著時，第二台會自動改用 glean-2。")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
    }

    private var networkSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Toggle("只在允許的網路開放", isOn: Binding(get: { share.restrictToHomeNetworks }, set: { share.setRestrictToHomeNetworks($0) }))
            if share.restrictToHomeNetworks {
                ForEach(share.homeNetworks) { network in
                    HStack {
                        Image(systemName: network.id == share.currentNetwork?.id ? "wifi" : "network")
                        Text(network.label + (network.id == share.currentNetwork?.id ? "（目前）" : ""))
                        Spacer()
                        Button("移除") { share.removeNetwork(network.id) }
                    }
                }
                if let current = share.currentNetwork, !share.homeNetworks.contains(where: { $0.id == current.id }) {
                    Button("允許目前的網路（\(current.label)）") { share.allowCurrentNetwork() }
                }
                Text("以路由器辨認網路。在咖啡廳、公司等其他網路會自動暫停分享。")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private static func relative(_ date: Date) -> String {
        let formatter = RelativeDateTimeFormatter()
        formatter.locale = Locale(identifier: "zh_Hant_TW")
        return formatter.localizedString(for: date, relativeTo: .now)
    }
}

private enum QRCode {
    static func image(for string: String) -> NSImage? {
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(string.utf8)
        filter.correctionLevel = "M"
        guard let output = filter.outputImage else { return nil }
        let scaled = output.transformed(by: CGAffineTransform(scaleX: 10, y: 10))
        guard let cgImage = CIContext().createCGImage(scaled, from: scaled.extent) else { return nil }
        return NSImage(cgImage: cgImage, size: NSSize(width: scaled.extent.width, height: scaled.extent.height))
    }
}
