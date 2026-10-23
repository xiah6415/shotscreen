import XCTest
@testable import SuYanShot

final class UpdateTests: XCTestCase {
    func testVersionComparison() {
        XCTAssertTrue(AppVersion.isNewer("v0.2.0", than: "0.1.0"))
        XCTAssertTrue(AppVersion.isNewer("1.10", than: "1.9"))
        XCTAssertTrue(AppVersion.isNewer("1.0.1", than: "1"))
        XCTAssertFalse(AppVersion.isNewer("1.2", than: "1.2.0"))
        XCTAssertFalse(AppVersion.isNewer("0.1.0", than: "0.2.0"))
        XCTAssertFalse(AppVersion.isNewer("1.2.0-beta", than: "1.2.0"))
    }

    func testParsesLatestRelease() throws {
        let json = """
        {"tag_name": "v0.2.0", "body": "- New\\n", "html_url": "https://github.com/xiah6415/shotscreen/releases/tag/v0.2.0",
         "assets": [{"name": "notes.txt", "browser_download_url": "https://example.com/notes.txt"},
                    {"name": "Glean-0.2.0.zip", "browser_download_url": "https://example.com/Glean-0.2.0.zip"}]}
        """
        let release = try UpdateChecker.parse(Data(json.utf8))
        XCTAssertEqual(release.version, "0.2.0")
        XCTAssertEqual(release.notes, "- New")
        XCTAssertEqual(release.zipURL?.lastPathComponent, "Glean-0.2.0.zip")
    }
}
