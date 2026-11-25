#!/usr/bin/python3
import json
from pathlib import Path
import subprocess

def open_office(*_):
    subprocess.Popen(["/usr/lib/csweet/office-installer/CSweet.Office.Node", "--open-office"], start_new_session=True)

open_office()
try:
    import gi
    gi.require_version("Gtk", "3.0")
    try:
        gi.require_version("AyatanaAppIndicator3", "0.1")
        from gi.repository import AyatanaAppIndicator3 as Indicator
    except (ValueError, ImportError):
        gi.require_version("AppIndicator3", "0.1")
        from gi.repository import AppIndicator3 as Indicator
    from gi.repository import Gtk, Gio
except (ImportError, ValueError):
    raise SystemExit(0)

app = Gtk.Application(application_id="com.csweet.OfficeTray", flags=Gio.ApplicationFlags.FLAGS_NONE)
def activate(application):
    if hasattr(application, "indicator"):
        return
    item = Indicator.Indicator.new("csweet-office", "computer", Indicator.IndicatorCategory.APPLICATION_STATUS)
    menu = Gtk.Menu()
    status = Gtk.MenuItem(label="Office status")
    status.set_sensitive(False)
    menu.append(status)
    def update_status(*_):
        configured = Path("/etc/systemd/system/csweet-office-node.service").exists()
        text = "Open Office for service status" if configured else "Office is awaiting setup"
        if configured:
            try:
                state = json.loads(Path("/run/csweet-office-status/health.json").read_text())["Connection"]
                if state in ("Connected", "Connecting", "Reconnecting"):
                    text = "Last reported: " + state
            except (OSError, ValueError, KeyError):
                pass
        status.set_label(text)
    update_status()
    menu.connect("show", update_status)
    for text, action in [("Open C-Sweet Office", open_office), ("Quit indicator", lambda *_: application.quit())]:
        entry = Gtk.MenuItem(label=text)
        entry.connect("activate", action)
        menu.append(entry)
    menu.show_all()
    item.set_menu(menu)
    item.set_status(Indicator.IndicatorStatus.ACTIVE)
    application.indicator = item
    application.hold()
app.connect("activate", activate)
app.run(None)
