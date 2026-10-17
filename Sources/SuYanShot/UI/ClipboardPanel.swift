import AppKit
import Carbon.HIToolbox
import SwiftUI

/// Paste-style history strip along the bottom of the screen. It takes keyboard focus without activating the app,
/// so the app you were typing in stays frontmost and receives the paste.
@MainActor
final class ClipboardPanelController: NSObject {
    var onPaste: ((ClipboardItem, _ plainText: Bool) -> Void)?

    private let store: ClipboardStore
    private let model = ClipboardPanelModel()
    private var panel: KeyablePanel?
    private var keyMonitor: Any?
    private var resignObserver: NSObjectProtocol?
    private let height: CGFloat = 300

    init(store: ClipboardStore) { self.store = store }

    var isVisible: Bool { panel != nil }

    func toggle() { isVisible ? close() : show() }

    func show() {
        guard panel == nil else { return }
        model.query = ""
        model.pinnedOnly = false
        model.selectedID = store.items.first?.id

        let visible = NSScreen.underMouse?.visibleFrame ?? CGRect(x: 0, y: 0, width: 1280, height: 800)
        let target = CGRect(x: visible.minX + 12, y: visible.minY + 12, width: visible.width - 24, height: height)
        let panel = KeyablePanel(contentRect: target.offsetBy(dx: 0, dy: -30), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.level = .popUpMenu
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.contentView = NSHostingView(rootView: ClipboardPanelView(
            store: store, model: model,
            onPaste: { [weak self] item, plain in self?.paste(item, plainText: plain) }
        ))
        panel.alphaValue = 0
        panel.makeKeyAndOrderFront(nil)
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.18
            context.timingFunction = CAMediaTimingFunction(name: .easeOut)
            panel.animator().setFrame(target, display: true)
            panel.animator().alphaValue = 1
        }
        self.panel = panel

        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self, event.window === self.panel else { return event }
            return self.handle(event) ? nil : event
        }
        // Clicking anywhere else dismisses it.
        resignObserver = NotificationCenter.default.addObserver(forName: NSWindow.didResignKeyNotification, object: panel, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.close() }
        }
    }

    func close() {
        if let keyMonitor { NSEvent.removeMonitor(keyMonitor) }
        keyMonitor = nil
        if let resignObserver { NotificationCenter.default.removeObserver(resignObserver) }
        resignObserver = nil
        guard let panel else { return }
        self.panel = nil
        panel.orderOut(nil)
    }

    private var visibleItems: [ClipboardItem] { model.filter(store.items) }
    private var selected: ClipboardItem? { visibleItems.first { $0.id == model.selectedID } ?? visibleItems.first }

    private func paste(_ item: ClipboardItem, plainText: Bool) {
        close()
        onPaste?(item, plainText)
    }

    /// Arrows choose, Return pastes (⇧ for plain text), ⌘1–9 paste directly, ⌘P pins, ⌘⌫ deletes, Esc clears search then closes.
    private func handle(_ event: NSEvent) -> Bool {
        let flags = event.modifierFlags.intersection([.command, .shift, .option, .control])
        let items = visibleItems
        switch Int(event.keyCode) {
        case kVK_Escape:
            if model.query.isEmpty { close() } else { model.query = "" }
            return true
        case kVK_LeftArrow, kVK_RightArrow:
            guard !items.isEmpty else { return true }
            let current = items.firstIndex { $0.id == selected?.id } ?? 0
            let next = min(max(0, current + (Int(event.keyCode) == kVK_LeftArrow ? -1 : 1)), items.count - 1)
            model.selectedID = items[next].id
            return true
        case kVK_Return, kVK_ANSI_KeypadEnter:
            if let selected { paste(selected, plainText: flags.contains(.shift)) }
            return true
        case kVK_Delete where flags == .command, kVK_ForwardDelete where flags == .command:
            if let selected {
                let index = items.firstIndex { $0.id == selected.id } ?? 0
                store.delete(selected.id)
                let remaining = visibleItems
                model.selectedID = remaining.isEmpty ? nil : remaining[min(index, remaining.count - 1)].id
            }
            return true
        default:
            break
        }
        guard flags == .command, let key = event.charactersIgnoringModifiers?.lowercased() else { return false }
        if key == "p", let selected { store.togglePin(selected.id); return true }
        if let number = Int(key), (1...9).contains(number), number <= items.count {
            paste(items[number - 1], plainText: false)
            return true
        }
        return false
    }
}

final class KeyablePanel: NSPanel {
    override var canBecomeKey: Bool { true }
}

private final class ClipboardPanelModel: ObservableObject {
    @Published var query = ""
    @Published var pinnedOnly = false
    @Published var selectedID: UUID?

    func filter(_ items: [ClipboardItem]) -> [ClipboardItem] {
        let query = query.trimmingCharacters(in: .whitespaces)
        return items.filter { item in
            (!pinnedOnly || item.pinned) && (query.isEmpty || item.searchText.localizedCaseInsensitiveContains(query))
        }
    }
}

private struct ClipboardPanelView: View {
    @ObservedObject var store: ClipboardStore
    @ObservedObject var model: ClipboardPanelModel
    let onPaste: (ClipboardItem, Bool) -> Void
    @FocusState private var searchFocused: Bool

    private var items: [ClipboardItem] { model.filter(store.items) }

    var body: some View {
        VStack(spacing: 10) {
            HStack(spacing: 10) {
                HStack(spacing: 6) {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField("搜尋剪貼簿", text: $model.query)
                        .textFieldStyle(.plain)
                        .focused($searchFocused)
                }
                .padding(.horizontal, 8)
                .frame(width: 260, height: 28)
                .background(.quaternary.opacity(0.6), in: RoundedRectangle(cornerRadius: 8, style: .continuous))

                Toggle(isOn: $model.pinnedOnly) { Label("已釘選", systemImage: "pin") }
                    .toggleStyle(.button)

                if !store.isEnabled {
                    Label("記錄已暫停", systemImage: "pause.circle").font(.caption).foregroundStyle(.orange)
                }
                Spacer()
                Text("← → 選擇 · Return 貼上 · ⇧Return 純文字 · ⌘1–9 快速貼上 · ⌘P 釘選 · ⌘⌫ 刪除 · Esc 關閉")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }

            if items.isEmpty {
                Text(store.items.isEmpty ? "還沒有紀錄，複製一些東西試試看" : "沒有符合的項目")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollViewReader { proxy in
                    ScrollView(.horizontal, showsIndicators: false) {
                        LazyHStack(spacing: 12) {
                            ForEach(Array(items.enumerated()), id: \.element.id) { index, item in
                                ClipboardCard(item: item, store: store, index: index, isSelected: item.id == (model.selectedID ?? items.first?.id))
                                    .id(item.id)
                                    .onTapGesture(count: 2) { onPaste(item, false) }
                                    .onTapGesture { model.selectedID = item.id }
                                    .contextMenu {
                                        Button("貼上") { onPaste(item, false) }
                                        if item.kind == .richText { Button("貼上為純文字") { onPaste(item, true) } }
                                        Button(item.pinned ? "取消釘選" : "釘選") { store.togglePin(item.id) }
                                        Divider()
                                        Button("刪除", role: .destructive) { store.delete(item.id) }
                                    }
                            }
                        }
                        .padding(4)
                    }
                    .onChange(of: model.selectedID) { _, id in
                        guard let id else { return }
                        withAnimation(.easeOut(duration: 0.15)) { proxy.scrollTo(id, anchor: .center) }
                    }
                }
            }
        }
        .padding(14)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
        .onAppear { searchFocused = true }
        .onChange(of: items.map(\.id)) { _, ids in
            if let selected = model.selectedID, ids.contains(selected) { return }
            model.selectedID = ids.first
        }
    }
}

private struct ClipboardCard: View {
    let item: ClipboardItem
    let store: ClipboardStore
    let index: Int
    let isSelected: Bool

    private static let relative: RelativeDateTimeFormatter = {
        let formatter = RelativeDateTimeFormatter()
        formatter.locale = Locale(identifier: "zh_Hant_TW")
        formatter.unitsStyle = .short
        return formatter
    }()

    private var tint: Color {
        switch item.kind {
        case .text: .blue
        case .richText: .purple
        case .image: .green
        case .file: .orange
        }
    }

    private var footer: String {
        switch item.kind {
        case .text, .richText: "\((item.text ?? "").count) 字"
        case .image: item.imagePixelSize.map { "\(Int($0.width)) × \(Int($0.height))" } ?? ""
        case .file: "\(item.fileURLs?.count ?? 0) 個檔案"
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 6) {
                if let icon = AppIcons.icon(for: item.sourceBundleID) {
                    Image(nsImage: icon).resizable().frame(width: 16, height: 16)
                }
                Text(item.sourceAppName ?? item.kind.title).font(.system(size: 11, weight: .semibold)).lineLimit(1)
                Spacer(minLength: 4)
                if item.pinned { Image(systemName: "pin.fill").font(.system(size: 10)).foregroundStyle(tint) }
                Text(Self.relative.localizedString(for: item.createdAt, relativeTo: .now))
                    .font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1)
            }
            .padding(.horizontal, 10)
            .frame(height: 30)
            .background(tint.opacity(0.18))

            content
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                .padding(10)

            HStack {
                Text("\(item.kind.title) · \(footer)")
                Spacer()
                if index < 9 { Text("⌘\(index + 1)") }
            }
            .font(.system(size: 10))
            .foregroundStyle(.secondary)
            .padding(.horizontal, 10)
            .padding(.bottom, 8)
        }
        .frame(width: 210, height: 220)
        .background(Color(nsColor: .textBackgroundColor))
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .strokeBorder(isSelected ? Color.accentColor : Color.primary.opacity(0.1), lineWidth: isSelected ? 3 : 1)
        )
        .contentShape(RoundedRectangle(cornerRadius: 12))
    }

    @ViewBuilder private var content: some View {
        switch item.kind {
        case .text, .richText:
            Text(String((item.text ?? "").prefix(600)))
                .font(.system(size: 12))
                .lineLimit(9)
        case .image:
            if let thumbnail = store.thumbnail(for: item) {
                Image(nsImage: thumbnail)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                Image(systemName: "photo").font(.largeTitle).foregroundStyle(.secondary).frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        case .file:
            let urls = item.fileURLs ?? []
            VStack(alignment: .leading, spacing: 6) {
                if let first = urls.first {
                    Image(nsImage: NSWorkspace.shared.icon(forFile: first.path)).resizable().frame(width: 44, height: 44)
                }
                ForEach(urls.prefix(4), id: \.self) { url in
                    Text(url.lastPathComponent).font(.system(size: 12)).lineLimit(1).truncationMode(.middle)
                }
                if urls.count > 4 { Text("還有 \(urls.count - 4) 個…").font(.system(size: 11)).foregroundStyle(.secondary) }
            }
        }
    }
}

@MainActor
private enum AppIcons {
    private static var cache: [String: NSImage] = [:]

    static func icon(for bundleID: String?) -> NSImage? {
        guard let bundleID else { return nil }
        if let cached = cache[bundleID] { return cached }
        guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) else { return nil }
        let icon = NSWorkspace.shared.icon(forFile: url.path)
        cache[bundleID] = icon
        return icon
    }
}
