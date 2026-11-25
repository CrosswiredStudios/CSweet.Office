import AppKit

final class OfficeMenu: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var item: NSStatusItem?
    func menuWillOpen(_ menu: NSMenu) {
        let manager = FileManager.default
        let configured = manager.fileExists(atPath: "/Library/LaunchDaemons/com.csweet.office.node.plist")
        var text = configured ? "Open Office for service status" : "Office is awaiting setup"
        if configured, let data = manager.contents(atPath: "/Library/Application Support/CSweet/OfficeStatus/health.json"),
           let report = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let state = report["Connection"] as? String, ["Connected", "Connecting", "Reconnecting"].contains(state) {
            text = "Last reported: " + state
        }
        menu.item(at: 0)?.title = text
    }
    @objc func openOffice() {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/Library/Application Support/CSweet/Office/InstallerPayload/CSweet.Office.Node")
        process.arguments = ["--open-office"]
        do { try process.run() } catch { NSAlert(error: error).runModal() }
    }
    func applicationDidFinishLaunching(_ notification: Notification) {
        let status = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        status.button?.title = "Office"
        status.button?.toolTip = "C-Sweet Office"
        let menu = NSMenu()
        menu.delegate = self
        menu.addItem(withTitle: "Office status", action: nil, keyEquivalent: "")
        let open = menu.addItem(withTitle: "Open C-Sweet Office", action: #selector(openOffice), keyEquivalent: "")
        open.target = self
        menu.addItem(withTitle: "Quit menu icon", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "")
        status.menu = menu
        item = status
        openOffice()
    }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { openOffice(); return true }
}
let app = NSApplication.shared
let menu = OfficeMenu()
app.delegate = menu
app.setActivationPolicy(.accessory)
app.run()
