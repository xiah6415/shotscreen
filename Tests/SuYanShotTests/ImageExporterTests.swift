import XCTest
@testable import SuYanShot

final class ImageExporterTests: XCTestCase {
    func testDefaultFilenameIsStableAndPNG() {
        var components = DateComponents()
        components.year = 2026; components.month = 9; components.day = 9
        components.hour = 14; components.minute = 30; components.second = 25
        components.timeZone = .current
        let date = Calendar.current.date(from: components)!
        XCTAssertEqual(ImageExporter().defaultFilename(at: date), "Screenshot 2026-09-09 at 14.30.25.png")
    }

    func testDelayOptions() {
        XCTAssertEqual(DelayOption.allCases.map(\.rawValue), [3, 5, 10])
    }
}
