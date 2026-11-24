import XCTest
import UIKit

final class ClipDockUITests: XCTestCase {
    func testPasteDownloadsPageAndDirectFile() {
        let app = XCUIApplication()
        app.launch()
        let paste = app.buttons["pasteLink"]
        func pasteLink(_ path: String) {
            UIPasteboard.general.string = "http://127.0.0.1:18741/" + path
            XCTAssertTrue(paste.waitForExistence(timeout: 10))
            paste.tap()
        }

        pasteLink("watch")
        XCTAssertTrue(app.buttons.matching(NSPredicate(format: "label CONTAINS[c] %@", "ClipDock Sample"))
            .firstMatch.waitForExistence(timeout: 60))
        pasteLink("tone.wav")
        let tone = app.buttons.matching(NSPredicate(format: "label CONTAINS[c] %@", "tone.wav")).firstMatch
        XCTAssertTrue(tone.waitForExistence(timeout: 30))
        pasteLink("empty")
        XCTAssertTrue(app.staticTexts.matching(NSPredicate(format: "label CONTAINS %@", "取得できませんでした"))
            .firstMatch.waitForExistence(timeout: 30))
        app.terminate()
        app.launch()
        XCTAssertTrue(tone.waitForExistence(timeout: 10))
    }
}
