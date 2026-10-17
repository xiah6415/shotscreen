import AppKit
import CoreImage.CIFilterBuiltins
import SwiftUI

/// Starts phone sharing and shows the QR code to scan. Closing the window keeps sharing running;
/// it stops with the button or after 30 idle minutes.
@MainActor
final class PhoneShareWindowController: NSObject {
    private var window: NSWindow?
    private let share: PhoneShare

    init(share: PhoneShare) { self.share = share }

    func show() {
        share.start()
        if window == nil {
            let window = NSWindow(contentRect: .zero, styleMask: [.titled, .closable], backing: .buffered, defer: false)
            window.title = "在手機上開啟剪貼簿"
            window.isReleasedWhenClosed = false
            window.contentView = NSHostingView(rootView: PhoneShareView(share: share))
            window.setContentSize(window.contentView?.fittingSize ?? NSSize(width: 360, height: 520))
            window.center()
            self.window = window
        }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}

private struct PhoneShareView: View {
    @ObservedObject var share: PhoneShare

    var body: some View {
        VStack(spacing: 16) {
            if let url = share.url {
                if let qr = QRCode.image(for: url.absoluteString) {
                    Image(nsImage: qr)
                        .interpolation(.none)
                        .resizable()
                        .frame(width: 220, height: 220)
                        .padding(12)
                        .background(.white, in: RoundedRectangle(cornerRadius: 12))
                }
                Text("手機和這台 Mac 連同一個 Wi-Fi，用相機掃描 QR code。")
                    .multilineTextAlignment(.center)
                Text(url.absoluteString)
                    .font(.system(size: 11, design: .monospaced))
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
                    .lineLimit(2)
                    .multilineTextAlignment(.center)
                Label(status, systemImage: "dot.radiowaves.left.and.right")
                    .font(.caption)
                    .foregroundStyle(.green)
            } else if let problem = share.problem {
                Image(systemName: "wifi.exclamationmark").font(.system(size: 40)).foregroundStyle(.orange)
                Text(problem).multilineTextAlignment(.center)
            } else if share.isRunning {
                ProgressView()
            } else {
                Image(systemName: "iphone.slash").font(.system(size: 40)).foregroundStyle(.secondary)
                Text("已停止分享。").foregroundStyle(.secondary)
            }

            Text("只有同一個網路裡、拿到這個網址的裝置看得到。30 分鐘沒有使用會自動停止。")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)

            HStack {
                if share.isRunning {
                    Button("換一組網址") { share.regenerateLink() }
                        .help("舊的網址會立刻失效")
                    Button("停止分享", role: .destructive) { share.stop() }
                        .buttonStyle(.borderedProminent)
                        .tint(.red)
                } else {
                    Button("開始分享") { share.start() }
                        .buttonStyle(.borderedProminent)
                }
            }
        }
        .padding(24)
        .frame(width: 360)
    }

    private var status: String {
        guard let last = share.lastActivity else { return "分享中，等待手機連線" }
        let formatter = RelativeDateTimeFormatter()
        formatter.locale = Locale(identifier: "zh_Hant_TW")
        return "分享中 · 手機上次連線 \(formatter.localizedString(for: last, relativeTo: .now))"
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
