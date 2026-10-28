import AppKit
import SwiftUI

/// Nonactivating feedback; success never takes keyboard focus away from the user's destination app.
@MainActor
final class TextRecognitionHUD {
    private var panel: NSPanel?
    private var dismissal: Task<Void, Never>?

    func show(_ message: String, working: Bool = false, on screen: NSScreen) {
        hide()
        let visible = screen.visibleFrame
        let frame = CGRect(x: visible.midX - 170, y: visible.minY + 70, width: 340, height: 64)
        let panel = NSPanel(contentRect: frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.level = .floating
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.ignoresMouseEvents = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.contentView = NSHostingView(rootView: HStack(spacing: 10) {
            if working {
                ProgressView().controlSize(.small)
            } else {
                Image(systemName: "text.viewfinder")
            }
            Text(message).font(.system(size: 14, weight: .medium))
        }
        .frame(width: 340, height: 64)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16)))
        self.panel = panel
        panel.orderFrontRegardless()
        if !working {
            dismissal = Task { @MainActor [weak self] in
                do { try await Task.sleep(for: .seconds(3)) } catch { return }
                self?.hide()
            }
        }
    }

    func hide() {
        dismissal?.cancel()
        dismissal = nil
        panel?.orderOut(nil)
        panel = nil
    }
}
