#!/usr/bin/env python3
"""Drive a headless AgOpenWeb from a browser: point NTRIP at the relay, open a field, wait.

    drive.py SECONDS MOUNT CREDS_FILE [RELAY_PORT=2102] [FIELD=RtcmBench]

Needs Playwright (pip install playwright) and the app on http://localhost:5174. Takes a bug
report at the end, so the run has the app's own dump as well.
"""
import sys, time
from playwright.sync_api import sync_playwright

secs, mount = int(sys.argv[1]), sys.argv[2]
user, _, password = open(sys.argv[3]).read().strip().partition(":")
relay_port = sys.argv[4] if len(sys.argv) > 4 else "2102"
field = sys.argv[5] if len(sys.argv) > 5 else "RtcmBench"
with sync_playwright() as p:
    browser = p.chromium.launch(); page = browser.new_page()
    page.goto("http://localhost:5174")
    page.wait_for_function("typeof iHoldControl!=='undefined' && iHoldControl===true && statusBar", timeout=30000)
    page.evaluate("a=>transport.send('ntrip.save|\\tBench\\t127.0.0.1\\t'+a[3]+'\\t'+a[2]+'\\t'+a[0]+'\\t'+a[1]+'\\t1\\t1\\t')",
                  [user, password, mount, relay_port])
    page.wait_for_timeout(800)
    page.evaluate("f=>transport.send('field.new|'+f)", field); page.wait_for_timeout(1500)
    page.evaluate("f=>transport.send('field.openOnly|'+f)", field)
    end = time.time() + secs
    while time.time() < end:
        page.wait_for_timeout(5000)
    print("ntrip:", page.evaluate("[statusBar.ntripConnected,statusBar.ntripStatus]"))
    page.evaluate("transport.send('app.bugReport|rtcm bench\\tbench run')")
    page.wait_for_timeout(4000); browser.close()
