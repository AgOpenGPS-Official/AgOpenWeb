# Add-on cards: a guide for developers

An add-on reaches the operator in AgOpenWeb through a **card** beside the map. There are two
kinds, and an add-on uses one or the other:

| | **VT card** | **Web card** |
|---|---|---|
| For | ISOBUS implements (a sprayer, a seeder, a baler) | Anything with a web page: a rate controller, a section or machine module, a camera, the HAT's own pages |
| What the operator sees | The implement's own screens (its object pool), as on any ISO 11783-6 virtual terminal | Your web page, live, in a frame |
| What you build | An implement ECU that speaks ISOBUS VT, or a gateway that carries CAN frames to AgOpenWeb | A web server on your device, at an address on the machine network |
| How AgOpenWeb finds it | Through a gateway (announced by DNS-SD) or a USB CAN adapter | A bookmark the operator adds; your device's `_aowcard._tcp` announcement once listing is built (see [Discovery](#discovery)) |

The two kinds stay separate: no web pages in a VT card, no VT screens in a web card. Both kinds
share the card frame, the floating and split layouts, and the drawer, so most of
[Where a card sits](#where-a-card-sits) applies to both.

What a card is **not**: a way to control AgOpenWeb. An add-on never sends AgOpenWeb commands. A
device that needs guidance data (speed, sections, position) takes it from the modules' protocol
on the machine network ([PGN.md](../PGN.md)), as every AgOpenGPS module does.

---

## Web cards

### How a page gets into a card

1. The operator adds a **bookmark** (a name and a URL) in a web card's ⚙. The bookmarks are kept
   on the AgOpenWeb host, so every browser in the cab sees the same list.
2. The operator picks the bookmark in the card's header. Up to four web cards can be open, each
   with its own page.
3. The page loads **from your device, on its own origin**. AgOpenWeb never proxies it: your page's
   requests, WebSockets and relative URLs go straight to your device, as when it is opened in a
   browser tab.

In a URL, `{host}` stands for the AgOpenWeb host as the browser reaches it. `http://{host}:8080/`
therefore works from the tablet and from the launcher window alike, for an add-on that runs on the
same computer as AgOpenWeb (the HAT's pages do this). For a device of its own, use its address on
the machine network.

The header's ↗ opens the page in the system browser, for anything the frame can't do.

### What the frame allows

The page is in an `<iframe>` with

```html
sandbox="allow-scripts allow-same-origin allow-forms allow-popups allow-downloads"
referrerpolicy="no-referrer"
```

| Works | Doesn't work |
|---|---|
| Scripts, `fetch`, WebSockets to your device | `alert()`, `confirm()`, `prompt()`: use your own in-page dialog |
| Cookies and `localStorage` on your origin | Navigating the top window (`target="_top"`, `window.top.location`) |
| Forms, file downloads | Camera, microphone, location, full screen (no permissions are delegated to the frame) |
| Links in a new window (`target="_blank"`), which open outside the card | Reading or scripting AgOpenWeb's page: it is another origin |

### Your server

- **Allow framing.** Don't send `X-Frame-Options: DENY` / `SAMEORIGIN`, and don't send a
  `Content-Security-Policy` whose `frame-ancestors` leaves AgOpenWeb out. A browser can't tell
  AgOpenWeb that a page refused the frame; the operator only sees a blank card (and the hint to
  use ↗).
- **Plain HTTP on the machine network is fine**, and is what most modules serve. AgOpenWeb itself
  is served over HTTP. HTTPS works only with a certificate the browser trusts: a self-signed one
  gives a blank card, with no way for the operator to accept it inside the frame.
- **Announce the page** as `_aowcard._tcp` ([Discovery](#discovery)), and until AgOpenWeb lists
  announced pages, serve it at a fixed address: a module on DHCP whose address moves breaks a
  typed bookmark.
- **iOS** asks the operator once for local network access the first time a card loads a page on
  the LAN. That prompt is the system's; nothing is needed from your page.

### Design the page for the cab

- **Sizes.** Your page must work at all of these:

  | Where | Size (CSS px) |
  |---|---|
  | Floating card, the four sizes of ⤢ | 480 × 320, 640 × 420, 800 × 520, 1000 × 640 |
  | Split layout, one card in the pane | ⅓ to ½ of the window's width, its full height |
  | Split layout, two cards in a tab | the same width, half the height |
  | Drawer pulled to Full | the window's full width, less the guidance strip |

  On a 1280 × 800 tablet the smallest of these is about 420 × 350. Use a responsive layout; don't
  assume a phone or a desktop.
- **Touch first.** A tablet in a moving cab, often with gloves: targets of at least 44 px, no
  hover-only controls, no tiny scrollbars.
- **Daylight and night.** Strong contrast. AgOpenWeb doesn't tell your page its day/night theme
  yet (see [Later](#later)); a dark page is easier at night and still readable in sun.
- **Keep running in the background.** The operator may close the drawer or switch tabs. AgOpenWeb
  hides the card without moving it, so your page keeps its state and doesn't reload, but a hidden
  frame may be throttled by the browser. Alarms that matter belong on the machine network
  (hardware messages, PGN 221) or the device itself, not only in the page.

### Observers

Several browsers may be connected; only the one that holds control can change anything. AgOpenWeb
can't see inside your page, so a browser **without** control gets a cover over the card: the page
stays live, taps go nowhere. If your device has its own write protection, it is still yours to
enforce: the cover is a courtesy in AgOpenWeb's UI, not security. Anyone on the machine network
can open your page in a browser tab.

---

## VT cards

A VT card is an ISO 11783-6 **virtual terminal** on the implement bus. To your implement,
AgOpenWeb is a VT like any other: your ECU announces its working set, uploads its object pool (or
loads a stored version) and runs its screens. You write nothing for AgOpenWeb; an implement that
works with a standard VT works here.

The terminal is [SharpIsoTerm](https://github.com/chriskinal/SharpIsoTerm), built on
AgIsoStack++'s VT server.

### What the terminal reports

| | |
|---|---|
| VT version | 5 |
| Data mask | 480 × 480 px (the card scales it to fit) |
| Soft keys | 80 × 80 px; 12 shown at once (two columns of six), up to 64 per soft key mask; no navigation keys |
| Fonts | all small and large sizes reported; drawn with a scaled font, so close to but not exactly the standard's bitmap cells |
| Address | 0x26 for the first terminal, 0x27 for the second, and so on |
| Function instance | 0 for the first terminal, 1 for the second, and so on |
| Pools | stored by the implement's NAME and version label, kept across restarts (Store Version / Load Version) |

**Several terminals.** The operator can run one to four terminals, as several displays in one
cab, one card each. Your implement chooses its terminal in its own settings (most have a "VT
number" or "preferred VT"); AgOpenWeb doesn't move implements between terminals.

### Not supported yet

- Drawn as labelled boxes: animations, graphics contexts, scaled graphics, VT 6 window masks and
  key groups, and other newer objects. An implement that needs VT 6 features may fall back or
  refuse.
- **Auxiliary control** (assigning implement functions to joystick buttons).
- On Show and On Hide macros don't run on a mask change (other macros do).

[SharpIsoTerm's GUIDE.md](https://github.com/chriskinal/SharpIsoTerm/blob/main/docs/GUIDE.md)
("Limits") has the current list.

### Reaching the bus

AgOpenWeb runs on a tablet or PC, so the implement bus reaches it one of two ways:

- **A USB CAN adapter** on the device itself, speaking slcan (a CANable or a clone). Windows,
  macOS, Linux and Android; not iOS, whose apps can't reach USB adapters.
- **A gateway on the machine network**, speaking the **AOW link**: the HAT's `aow-linkd`, the ESP32
  reference gateway, or your own. Every platform, iOS included.

### Building a gateway

The AOW link is a small binary protocol over one TCP connection, written so an ESP32 can implement
it in a few hundred lines. The specification is
[SharpIsoTerm `docs/TRANSPORT.md`](https://github.com/chriskinal/SharpIsoTerm/blob/main/docs/TRANSPORT.md);
in short:

- **TCP, port 2323** by default, `TCP_NODELAY`. The gateway listens; AgOpenWeb connects.
- **Messages:** `type:u8`, `len:u8`, body. HELLO first (both ways, magic `AOWL`, version 1), then
  FRAME (one CAN frame), HEARTBEAT every 100 ms both ways, STATUS per channel from the gateway,
  CLOSE with a reason last. Skip unknown types by `len`.
- **1 second of silence ends the link**, on both sides, and AgOpenWeb reconnects. This is also the
  stale-frame rule: no frame reaches the bus more than about 1.1 s after it was sent.
- **One control client at a time.** A second one gets CLOSE busy, with the name of the terminal
  holding the bus, which the operator sees as "In use by …".
- **The gateway knows nothing of ISOBUS.** It passes frames, drains the bus when a client
  connects, and sends nothing to the bus while none is connected.
- **Listen only on the machine network:** its cable, its own access point, or loopback. Never on
  an uplink to a farm or office network. The link has no pairing or password by design: joining
  the machine network is the pairing, the same trust the steer and machine PGNs have.

**Announce it** by DNS-SD, so the operator picks it by name in the VT card's ⚙ instead of typing an
address:

- service type `_aowlink._tcp`, on the port you listen on;
- TXT `v=1` (the protocol version);
- an instance name the operator will recognise ("Sprayer gateway", "AOW HAT 1234").

AgOpenWeb keeps the gateway by name, so it finds it again when its address changes. On an ESP32:

```cpp
#include <ESPmDNS.h>

MDNS.begin("aowlink-1234");               // host name
MDNS.setInstanceName("Sprayer gateway");  // what the operator sees
MDNS.addService("aowlink", "tcp", 2323);
MDNS.addServiceTxt("aowlink", "tcp", "v", "1");
```

**Check it** with SharpIsoTerm's `bench/aowlink_check.py HOST [PORT]` (hello, status, heartbeat
rate, busy, monitors, the silence rule, versions, bad input, unknown messages) before you
recommend it. `gateway/esp32/` in SharpIsoTerm is a complete gateway to start from (PlatformIO,
MIT).

---

## Where a card sits

The operator chooses the layout, per browser, in Screen & Alerts › VT & Web Cards:

- **Floating** (the default): cards over the map, dragged and sized by hand.
- **Split, left or right:** the open VT and web cards in a pane beside the map, two to a tab.
  The pane is a **drawer**: Closed (a handle on the window's edge), Split (⅓ to ½ of the window)
  or Full (over the map, with a guidance strip along the top).

Only the operator moves the drawer. Nothing opens it by itself, and above 2 km/h it can't be pulled
to Full. Your add-on can't open, raise or resize its card.

---

## Discovery

AgOpenWeb **doesn't scan the network** for add-ons, on any address or port. Add-ons announce
themselves with standard mDNS / DNS-SD, and AgOpenWeb lists what is announced.

- **VT gateways:** built. Announce `_aowlink._tcp` as above.
- **Web cards:** `_aowcard._tcp`, below. The format is fixed; AgOpenWeb's side (listing the pages
  in the bookmark editor) is not built yet, so the operator adds a bookmark by hand for now. A
  device that announces itself today will be listed when it is.

### Announcing a web page: `_aowcard._tcp`

One DNS-SD service instance per page:

| Record | Content |
|---|---|
| Service type | `_aowcard._tcp` |
| Instance name | What the operator sees in the list, UTF-8, at most 63 bytes: "Sprayer rate control", "Camera rear". Unique on the network; give each unit something the operator can tell apart (a serial or the last digits of the MAC). |
| SRV | The host and the **port your web server listens on**: any port, 80 or not. |
| TXT | The keys below. |

TXT keys AgOpenWeb reads:

| Key | | Content | Default |
|---|---|---|---|
| `v` | required | The version of this TXT format: `1`. An instance with another or no `v` is not listed. | |
| `path` | optional | The page's path on your server, starting with `/`; may carry a query (`/rate?view=cab`). | `/` |
| `scheme` | optional | `http` or `https`. Use `https` only with a certificate the browser trusts (see [Your server](#your-server)). | `http` |

AgOpenWeb builds the page's URL as `scheme://host:port/path` from the SRV record and these keys.
The rules, from RFC 6763:

- Keys are case-insensitive and ASCII; values are UTF-8. Each `key=value` string is at most
  255 bytes; keep the whole TXT record under 400 bytes.
- AgOpenWeb **ignores keys it doesn't know**, so you may add your own (a firmware version, a
  model). Keys beginning with `aow` are reserved for this format.
- A device with several pages (a setup page and a run page) announces one instance per page,
  with different instance names; they may share a port.
- Announce only on the machine network, as a gateway does, and only while the page is served.

AgOpenWeb keeps a page added from the list by its instance name, so the bookmark follows the
device to a new address.

On an ESP32 (Arduino):

```cpp
#include <ESPmDNS.h>

MDNS.begin("ratectl-1234");                 // host name
MDNS.setInstanceName("Sprayer rate control"); // what the operator sees
MDNS.addService("aowcard", "tcp", 80);
MDNS.addServiceTxt("aowcard", "tcp", "v", "1");
MDNS.addServiceTxt("aowcard", "tcp", "path", "/");
```

On Linux with Avahi, a file in `/etc/avahi/services/`, for example `aowcard.service`:

```xml
<?xml version="1.0" standalone="no"?>
<!DOCTYPE service-group SYSTEM "avahi-service.dtd">
<service-group>
  <name>Sprayer rate control</name>
  <service>
    <type>_aowcard._tcp</type>
    <port>8080</port>
    <txt-record>v=1</txt-record>
    <txt-record>path=/</txt-record>
  </service>
</service-group>
```

To check what your device announces: `dns-sd -B _aowcard._tcp` then `dns-sd -L "<instance name>"
_aowcard._tcp` on a Mac, or `avahi-browse -r _aowcard._tcp` on Linux.

## Later

Planned in [Plans/ADD_ON_CARDS_PLAN.md](../Plans/ADD_ON_CARDS_PLAN.md) §7, when an add-on asks for
them:

1. **Query parameters** on load: `aow_theme=day|night`, `aow_lang=de`, `aow_units=metric|imperial`.
   A page that doesn't use them can ignore them.
2. **`postMessage`** both ways, with the origin checked: to your page the theme, language, units
   and whether this browser holds control, on each change; from your page a title, an attention
   badge (an alarm while the card is closed or hidden) and a preferred size.

Neither exists yet: don't rely on them.
