import AppKit
import ScreenCaptureKit
import SwiftUI

/// Mission-Control-style overview for picking a window: a grid of live thumbnails of every listed window,
/// including covered ones and those on other desktops, with search. Click one to pick it; Esc goes back.
@MainActor
final class WindowGrid {
    var onPick: ((SCWindow) -> Void)?
    /// Esc / 返回: back to hover picking.
    var onClose: (() -> Void)?
    var isShown: Bool { panel != nil }

    private var panel: KeyablePanel?
    private var keyMonitor: Any?
    private var loader: Task<Void, Never>?
    private let model = WindowGridModel()

    func show(windows: [SCWindow], title: String, on screen: NSScreen) {
        close()
        model.items = windows.map(WindowGridModel.Item.init)
        model.query = ""
        model.title = title

        let panel = KeyablePanel(contentRect: screen.frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.level = .screenSaver
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.contentView = NSHostingView(rootView: WindowGridView(
            model: model,
            onPick: { [weak self] window in self?.onPick?(window) },
            onClose: { [weak self] in self?.dismiss() }))
        panel.makeKeyAndOrderFront(nil)
        self.panel = panel

        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self, event.window === self.panel, event.keyCode == 53 else { return event }
            self.dismiss()
            return nil
        }
        loader = Task { [model] in await Self.loadThumbnails(into: model) }
    }

    func close() {
        loader?.cancel()
        loader = nil
        if let keyMonitor { NSEvent.removeMonitor(keyMonitor) }
        keyMonitor = nil
        panel?.orderOut(nil)
        panel = nil
    }

    private func dismiss() {
        close()
        onClose?()
    }

    /// Small captures of each window, four at a time. macOS can't capture windows on other desktops or minimized ones;
/// those fall back to the app icon.
    private static func loadThumbnails(into model: WindowGridModel) async {
        let windows = model.items.map(\.window)
        await withTaskGroup(of: (CGWindowID, NSImage?).self) { group in
            var next = 0
            func enqueue() {
                guard next < windows.count else { return }
                let window = windows[next]
                next += 1
                group.addTask { (window.windowID, await thumbnail(of: window)) }
            }
            for _ in 0..<4 { enqueue() }
            while let (id, image) = await group.next() {
                if Task.isCancelled { group.cancelAll(); return }
                model.setThumbnail(image, for: id)
                enqueue()
            }
        }
    }

    private static func thumbnail(of window: SCWindow) async -> NSImage? {
        let filter = SCContentFilter(desktopIndependentWindow: window)
        let scale = min(1, 480 / max(1, window.frame.width)) * CGFloat(filter.pointPixelScale)
        let configuration = SCStreamConfiguration()
        configuration.width = max(2, Int(window.frame.width * scale))
        configuration.height = max(2, Int(window.frame.height * scale))
        configuration.showsCursor = false
        guard let image = try? await SCScreenshotManager.captureImage(contentFilter: filter, configuration: configuration) else { return nil }
        return NSImage(cgImage: image, size: NSSize(width: image.width, height: image.height))
    }
}

@MainActor
private final class WindowGridModel: ObservableObject {
    struct Item: Identifiable {
        let window: SCWindow
        var thumbnail: NSImage?
        var loaded = false
        var id: CGWindowID { window.windowID }
        var appName: String { window.owningApplication?.applicationName ?? "其他" }
        var title: String { window.title?.isEmpty == false ? window.title! : "（沒有標題）" }

        init(window: SCWindow) { self.window = window }
    }

    @Published var items: [Item] = []
    @Published var query = ""
    @Published var title = ""

    var filtered: [Item] {
        let q = query.trimmingCharacters(in: .whitespaces)
        guard !q.isEmpty else { return items }
        return items.filter { $0.appName.localizedCaseInsensitiveContains(q) || $0.title.localizedCaseInsensitiveContains(q) }
    }

    func setThumbnail(_ image: NSImage?, for id: CGWindowID) {
        guard let index = items.firstIndex(where: { $0.id == id }) else { return }
        items[index].thumbnail = image
        items[index].loaded = true
    }
}

private struct WindowGridView: View {
    @ObservedObject var model: WindowGridModel
    let onPick: (SCWindow) -> Void
    let onClose: () -> Void
    @FocusState private var searchFocused: Bool

    var body: some View {
        ZStack {
            Color.black.opacity(0.6).ignoresSafeArea().onTapGesture(perform: onClose)
            VStack(spacing: 18) {
                HStack(spacing: 14) {
                    Text(model.title).font(.title2.weight(.bold)).foregroundStyle(.white)
                    Text("\(model.filtered.count) 個視窗").foregroundStyle(.white.opacity(0.6))
                    Spacer()
                    TextField("搜尋 App 或視窗", text: $model.query)
                        .textFieldStyle(.roundedBorder)
                        .frame(width: 260)
                        .focused($searchFocused)
                    Button("返回", action: onClose).help("回到游標點選（Esc）")
                }
                if model.filtered.isEmpty {
                    Text("沒有符合的視窗").foregroundStyle(.white.opacity(0.7)).frame(maxHeight: .infinity)
                } else {
                    ScrollView {
                        LazyVGrid(columns: [GridItem(.adaptive(minimum: 250, maximum: 330), spacing: 18)], spacing: 18) {
                            ForEach(model.filtered) { item in
                                WindowCard(item: item).onTapGesture { onPick(item.window) }
                            }
                        }
                        .padding(.bottom, 40)
                    }
                }
            }
            .padding(.horizontal, 60)
            .padding(.top, 54)
        }
        .onAppear { searchFocused = true }
    }
}

private struct WindowCard: View {
    let item: WindowGridModel.Item
    @State private var hovering = false

    private var appIcon: NSImage? {
        item.window.owningApplication.flatMap { NSRunningApplication(processIdentifier: $0.processID)?.icon }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            ZStack {
                RoundedRectangle(cornerRadius: 10).fill(.white.opacity(0.07))
                if let thumbnail = item.thumbnail {
                    Image(nsImage: thumbnail).resizable().aspectRatio(contentMode: .fit).padding(6)
                        .shadow(color: .black.opacity(0.4), radius: 6, y: 3)
                } else if item.loaded, let appIcon {
                    Image(nsImage: appIcon).resizable().frame(width: 64, height: 64).opacity(0.8)
                } else {
                    ProgressView().controlSize(.small).tint(.white)
                }
            }
            .frame(height: 160)

            HStack(spacing: 6) {
                if let appIcon { Image(nsImage: appIcon).resizable().frame(width: 18, height: 18) }
                Text(item.appName).font(.system(size: 13, weight: .semibold)).foregroundStyle(.white).lineLimit(1)
                Spacer(minLength: 4)
                if !item.window.isOnScreen {
                    Text("其他桌面／已縮小 · 選了會先切過去").font(.system(size: 10, weight: .medium)).foregroundStyle(.white.opacity(0.85))
                        .padding(.horizontal, 6).padding(.vertical, 2).background(.white.opacity(0.15), in: Capsule())
                }
            }
            Text(item.title).font(.system(size: 12)).foregroundStyle(.white.opacity(0.7)).lineLimit(1)
        }
        .padding(10)
        .background(hovering ? Color.accentColor.opacity(0.45) : Color.white.opacity(0.06), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(hovering ? Color.accentColor : .clear, lineWidth: 2))
        .contentShape(RoundedRectangle(cornerRadius: 14))
        .onHover { hovering = $0 }
    }
}
