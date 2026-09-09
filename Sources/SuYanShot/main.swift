import AppKit

let application = NSApplication.shared
MainActor.assumeIsolated {
    let delegate = AppDelegate()
    application.delegate = delegate
    application.run()
}
