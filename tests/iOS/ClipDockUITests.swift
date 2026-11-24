import XCTest
import UIKit

final class ClipDockUITests: XCTestCase {
    func testLocalizedPasteScreen() {
        continueAfterFailure = false
        let app = XCUIApplication()
        for (language, heading, errorPrefix) in [
            ("ja", "リンクから保存", "動画ページや"),
            ("en", "Save from a link", "Copy an HTTP"),
            ("es", "Guardar desde un enlace", "Copia un enlace HTTP"),
            ("zh-Hans", "通过链接保存", "请复制视频网页")
        ] {
            app.launchArguments = ["-AppleLanguages", "(\(language))", "-AppleLocale", language]
            app.launch()
            XCTAssertTrue(app.staticTexts[heading].waitForExistence(timeout: 10), language)
            UIPasteboard.general.string = "ClipDock"
            app.buttons["pasteLink"].tap()
            XCTAssertTrue(app.staticTexts.matching(NSPredicate(format: "label BEGINSWITH %@", errorPrefix))
                .firstMatch.waitForExistence(timeout: 10), language)
            app.terminate()
        }
    }

    func testPasteDownloadsPageAndDirectFile() {
        continueAfterFailure = false
        let app = XCUIApplication()
        app.launchArguments = ["-AppleLanguages", "(ja)", "-AppleLocale", "ja_JP"]
        app.launch()
        let paste = app.buttons["pasteLink"]
        func pasteLink(_ path: String, asURL: Bool = false) {
            let link = "http://127.0.0.1:18741/" + path
            if asURL { UIPasteboard.general.url = URL(string: link) }
            else { UIPasteboard.general.string = link }
            XCTAssertTrue(paste.waitForExistence(timeout: 10))
            paste.tap()
        }

        pasteLink("watch", asURL: true)
        XCTAssertTrue(app.buttons.matching(NSPredicate(format: "label CONTAINS[c] %@", "ClipDock Sample"))
            .firstMatch.waitForExistence(timeout: 60), app.debugDescription)
        pasteLink("slow.wav")
        XCTAssertTrue(app.staticTexts["ダウンロード中…"].waitForExistence(timeout: 30))
        let cancel = app.buttons["キャンセル"]
        cancel.tap()
        XCTAssertTrue(cancel.waitForNonExistence(timeout: 30))
        XCTAssertFalse(app.buttons["slow.wav"].exists)
        pasteLink("tone.wav")
        let tone = app.buttons.matching(NSPredicate(format: "label CONTAINS[c] %@", "tone.wav")).firstMatch
        XCTAssertTrue(tone.waitForExistence(timeout: 30))
        pasteLink("split.mpd")
        XCTAssertTrue(app.buttons["split.mp4"].waitForExistence(timeout: 30), app.debugDescription)
        pasteLink("empty")
        XCTAssertTrue(app.staticTexts.matching(NSPredicate(format: "label CONTAINS %@", "取得できませんでした"))
            .firstMatch.waitForExistence(timeout: 30))
        app.terminate()
        app.launch()
        XCTAssertTrue(tone.waitForExistence(timeout: 10))
    }
}
