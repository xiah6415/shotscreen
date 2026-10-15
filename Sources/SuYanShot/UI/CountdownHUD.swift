import AppKit
import SwiftUI

/// Centered countdown shown before a delayed capture. Click it to cancel.
@MainActor
final class CountdownHUD {
    private var panel: NSPanel?
    private var timer: Timer?
    private let model = CountdownModel()

    func start(seconds: Int, completion: @escaping () -> Void) {
        cancel()
        model.remaining = seconds
        let size = NSSize(width: 150, height: 150)
        let visible = NSScreen.underMouse?.visibleFrame ?? CGRect(x: 0, y: 0, width: 800, height: 600)
        let frame = CGRect(x: visible.midX - size.width / 2, y: visible.midY - size.height / 2, width: size.width, height: size.height)
        let panel = NSPanel(contentRect: frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.level = .floating
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.contentView = NSHostingView(rootView: CountdownView(model: model) { [weak self] in self?.cancel() })
        panel.orderFrontRegardless()
        self.panel = panel

        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick(completion) }
        }
    }

    func cancel() {
        timer?.invalidate()
        timer = nil
        panel?.orderOut(nil)
        panel = nil
    }

    private func tick(_ completion: @escaping () -> Void) {
        withAnimation { model.remaining -= 1 }
        guard model.remaining <= 0 else { return }
        cancel()
        // Give the window server a moment to remove the HUD before capturing.
        Task { @MainActor in
            try? await Task.sleep(for: .milliseconds(200))
            completion()
        }
    }
}

private final class CountdownModel: ObservableObject {
    @Published var remaining = 0
}

private struct CountdownView: View {
    @ObservedObject var model: CountdownModel
    let onCancel: () -> Void

    var body: some View {
        VStack(spacing: 4) {
            Text("\(model.remaining)")
                .font(.system(size: 64, weight: .bold, design: .rounded))
                .monospacedDigit()
                .contentTransition(.numericText(countsDown: true))
            Text("點一下取消")
                .font(.system(size: 11, weight: .medium))
                .foregroundStyle(.secondary)
        }
        .frame(width: 150, height: 150)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 28, style: .continuous))
        .contentShape(RoundedRectangle(cornerRadius: 28, style: .continuous))
        .onTapGesture(perform: onCancel)
    }
}
