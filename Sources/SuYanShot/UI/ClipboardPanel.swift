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
    private let height: CGFloat = 322

    init(store: ClipboardStore) { self.store = store }

    var isVisible: Bool { panel != nil }

    func toggle() { isVisible ? close() : show() }

    func show() {
        guard panel == nil else { return }
        model.query = ""
        model.filter = .all
        model.tagEditor = nil
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

    private var visibleItems: [ClipboardItem] { model.filter(store.items, tags: store.tags) }
    private var selected: ClipboardItem? { visibleItems.first { $0.id == model.selectedID } ?? visibleItems.first }

    private func paste(_ item: ClipboardItem, plainText: Bool) {
        close()
        onPaste?(item, plainText)
    }

    /// Arrows choose, Tab switches filters, Return pastes (⇧ for plain text), ⌘1–9 paste directly,
    /// ⌘P toggles 常用, ⌘⌫ deletes, Esc clears search then closes.
    private func handle(_ event: NSEvent) -> Bool {
        // While a tag name is being typed, keys belong to that field (Esc cancels it).
        if model.tagEditor != nil {
            if Int(event.keyCode) == kVK_Escape { model.tagEditor = nil; return true }
            return false
        }
        let flags = event.modifierFlags.intersection([.command, .shift, .option, .control])
        let items = visibleItems
        switch Int(event.keyCode) {
        case kVK_Tab:
            model.cycleFilter(forward: !flags.contains(.shift), tags: store.tags)
            return true
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

private enum ClipboardFilter: Hashable {
    case all, favorites, text, image, file
    case tag(UUID)

    func matches(_ item: ClipboardItem) -> Bool {
        switch self {
        case .all: true
        case .favorites: item.pinned
        case .text: item.kind == .text || item.kind == .richText
        case .image: item.kind == .image
        case .file: item.kind == .file
        case let .tag(id): item.tags.contains(id)
        }
    }
}

private enum TagEditor: Equatable {
    /// Optionally add the new tag to this entry once it is created.
    case new(assignTo: UUID?)
    case rename(UUID)
}

private final class ClipboardPanelModel: ObservableObject {
    @Published var query = ""
    @Published var filter: ClipboardFilter = .all
    @Published var selectedID: UUID?
    @Published var tagEditor: TagEditor?
    @Published var tagDraft = ""

    static func filters(tags: [ClipboardTag]) -> [ClipboardFilter] {
        [.all, .favorites, .text, .image, .file] + tags.map { .tag($0.id) }
    }

    /// Search also matches tag names.
    func filter(_ items: [ClipboardItem], tags: [ClipboardTag]) -> [ClipboardItem] {
        let query = query.trimmingCharacters(in: .whitespaces)
        return items.filter { item in
            guard filter.matches(item) else { return false }
            guard !query.isEmpty else { return true }
            return item.searchText.localizedCaseInsensitiveContains(query)
                || tags.contains { item.tags.contains($0.id) && $0.name.localizedCaseInsensitiveContains(query) }
        }
    }

    func cycleFilter(forward: Bool, tags: [ClipboardTag]) {
        let all = Self.filters(tags: tags)
        let index = all.firstIndex(of: filter) ?? 0
        filter = all[(index + (forward ? 1 : all.count - 1)) % all.count]
    }

    func beginTagEditing(_ editor: TagEditor, draft: String = "") {
        tagDraft = draft
        tagEditor = editor
    }
}

private struct ClipboardPanelView: View {
    @ObservedObject var store: ClipboardStore
    @ObservedObject var model: ClipboardPanelModel
    let onPaste: (ClipboardItem, Bool) -> Void
    private enum Field { case search, tag }
    @FocusState private var focus: Field?

    private var items: [ClipboardItem] { model.filter(store.items, tags: store.tags) }

    var body: some View {
        VStack(spacing: 10) {
            HStack(spacing: 10) {
                HStack(spacing: 6) {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField("搜尋剪貼簿", text: $model.query)
                        .textFieldStyle(.plain)
                        .focused($focus, equals: .search)
                }
                .padding(.horizontal, 8)
                .frame(width: 260, height: 28)
                .background(.quaternary.opacity(0.6), in: RoundedRectangle(cornerRadius: 8, style: .continuous))

                filterBar

                if !store.isEnabled {
                    Label("記錄已暫停", systemImage: "pause.circle").font(.caption).foregroundStyle(.orange).fixedSize()
                }
            }

            if items.isEmpty {
                Text(emptyMessage)
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
                                    .contextMenu { cardMenu(item) }
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

            Text("← → 選擇 · Tab 切換標籤 · Return 貼上 · ⇧Return 純文字 · ⌘1–9 快速貼上 · ⌘P 常用 · ⌘⌫ 刪除 · Esc 關閉")
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(14)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
        .onAppear { focus = .search }
        .onChange(of: model.tagEditor) { _, editor in focus = editor == nil ? .search : .tag }
        .onChange(of: items.map(\.id)) { _, ids in
            if let selected = model.selectedID, ids.contains(selected) { return }
            model.selectedID = ids.first
        }
        .onChange(of: store.tags.map(\.id)) { _, ids in
            // The tag being viewed was deleted.
            if case let .tag(id) = model.filter, !ids.contains(id) { model.filter = .all }
        }
    }

    private var emptyMessage: String {
        if store.items.isEmpty { return "還沒有紀錄，複製一些東西試試看" }
        if !model.query.trimmingCharacters(in: .whitespaces).isEmpty { return "沒有符合的項目" }
        switch model.filter {
        case .favorites: return "還沒有常用的紀錄，按 ⌘P 或右鍵 →「加入常用」"
        case .tag: return "這個標籤還沒有紀錄，右鍵卡片 →「標籤」就能加入"
        default: return "沒有符合的項目"
        }
    }

    /// 全部 · 常用 · 文字 · 圖片 · 檔案, then the user's tags and ＋ to make a new one.
    private var filterBar: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 6) {
                FilterChip(title: "全部", symbol: "tray.full", isOn: model.filter == .all) { model.filter = .all }
                FilterChip(title: "常用", symbol: "star", isOn: model.filter == .favorites) { model.filter = .favorites }
                FilterChip(title: "文字", symbol: "text.alignleft", isOn: model.filter == .text) { model.filter = .text }
                FilterChip(title: "圖片", symbol: "photo", isOn: model.filter == .image) { model.filter = .image }
                FilterChip(title: "檔案", symbol: "doc", isOn: model.filter == .file) { model.filter = .file }
                Divider().frame(height: 18)
                ForEach(store.tags) { tag in
                    if model.tagEditor == .rename(tag.id) {
                        tagNameField
                    } else {
                        FilterChip(title: tag.name, color: Color(nsColor: tag.color), isOn: model.filter == .tag(tag.id)) { model.filter = .tag(tag.id) }
                            .contextMenu {
                                Button("重新命名…") { model.beginTagEditing(.rename(tag.id), draft: tag.name) }
                                Button("刪除標籤", role: .destructive) { store.deleteTag(tag.id) }
                            }
                    }
                }
                if case .new = model.tagEditor {
                    tagNameField
                } else {
                    Button { model.beginTagEditing(.new(assignTo: nil)) } label: {
                        Image(systemName: "plus").font(.system(size: 11, weight: .semibold)).frame(width: 26, height: 24)
                            .background(.quaternary.opacity(0.6), in: Capsule())
                    }
                    .buttonStyle(.plain)
                    .help("新增標籤")
                }
            }
            .padding(.vertical, 2)
        }
    }

    private var tagNameField: some View {
        TextField("標籤名稱", text: $model.tagDraft)
            .textFieldStyle(.plain)
            .font(.system(size: 12))
            .focused($focus, equals: .tag)
            .onSubmit(commitTag)
            .padding(.horizontal, 10)
            .frame(width: 130, height: 24)
            .background(.background, in: Capsule())
            .overlay(Capsule().strokeBorder(Color.accentColor, lineWidth: 1.5))
    }

    private func commitTag() {
        switch model.tagEditor {
        case let .new(assignTo):
            store.createTag(named: model.tagDraft, addingTo: assignTo)
        case let .rename(id):
            store.renameTag(id, to: model.tagDraft)
        case nil:
            break
        }
        model.tagEditor = nil
    }

    @ViewBuilder private func cardMenu(_ item: ClipboardItem) -> some View {
        Button("貼上") { onPaste(item, false) }
        if item.kind == .richText { Button("貼上為純文字") { onPaste(item, true) } }
        Button(item.pinned ? "移出常用" : "加入常用") { store.togglePin(item.id) }
        Menu("標籤") {
            ForEach(store.tags) { tag in
                Toggle(tag.name, isOn: Binding(get: { item.tags.contains(tag.id) }, set: { _ in store.toggleTag(tag.id, on: item.id) }))
            }
            if !store.tags.isEmpty { Divider() }
            Button("新增標籤…") { model.beginTagEditing(.new(assignTo: item.id)) }
        }
        Divider()
        Button("刪除", role: .destructive) { store.delete(item.id) }
    }
}

private struct FilterChip: View {
    let title: String
    var symbol: String?
    var color: Color?
    let isOn: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 5) {
                if let color { Circle().fill(color).frame(width: 8, height: 8) }
                if let symbol { Image(systemName: isOn ? Self.filled(symbol) : symbol).font(.system(size: 11)) }
                Text(title).font(.system(size: 12, weight: isOn ? .semibold : .regular)).lineLimit(1)
            }
            .padding(.horizontal, 10)
            .frame(height: 24)
            .background(isOn ? Color.accentColor.opacity(0.22) : Color.primary.opacity(0.06), in: Capsule())
            .foregroundStyle(isOn ? Color.accentColor : .primary)
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
    }

    /// The ".fill" variant when SF Symbols has one (e.g. text.alignleft has none).
    private static func filled(_ symbol: String) -> String {
        NSImage(systemSymbolName: symbol + ".fill", accessibilityDescription: nil) == nil ? symbol : symbol + ".fill"
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
                if item.pinned { Image(systemName: "star.fill").font(.system(size: 10)).foregroundStyle(.yellow) }
                Text(Self.relative.localizedString(for: item.createdAt, relativeTo: .now))
                    .font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1)
            }
            .padding(.horizontal, 10)
            .frame(height: 30)
            .background(tint.opacity(0.18))

            content
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                .padding(10)

            HStack(spacing: 6) {
                let tags = item.tags.compactMap(store.tag)
                if tags.isEmpty {
                    Text("\(item.kind.title) · \(footer)")
                } else {
                    ForEach(tags.prefix(3)) { tag in
                        HStack(spacing: 3) {
                            Circle().fill(Color(nsColor: tag.color)).frame(width: 7, height: 7)
                            Text(tag.name).lineLimit(1)
                        }
                    }
                    if tags.count > 3 { Text("+\(tags.count - 3)") }
                }
                Spacer(minLength: 2)
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
