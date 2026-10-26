# ISOBUS VT: reaching the implement bus

**Status:** plan, agreed in outline 2026-10-25; nothing below the "Built" section is built yet.
**Date:** 2026-10-25
**Purpose:** How AgOpenWeb's virtual terminal reaches an implement's CAN bus in every setup a
farmer may have, what is trusted, and how the operator connects without typing addresses. The
HAT is one setup among several (two units exist, both on the bench), and no network gateway is
installed anywhere yet, so the choices here are open.

The terminal itself (SharpIsoTerm's `VirtualTerminalStack`, the VT cards) is described in
`CLAUDE.md` ("ISOBUS virtual terminal"). The link protocol is specified in SharpIsoTerm
`docs/TRANSPORT.md`.

---

## 1. Setups

| # | Where AgOpenWeb runs | How it reaches the bus | Platforms | State |
|---|---|---|---|---|
| A | On the unit (HAT CM4); the tablet is a browser | `aow-linkd` on loopback | Linux arm64 | works; not packaged (§5) |
| B | Tablet or PC | USB adapter on that device, slcan (CANable and clones) | Windows, macOS, Linux, Android | **built** (AOW #433, library PR #3) |
| C | Tablet or PC | USB adapter, gs_usb (candleLight) or PEAK PCAN-USB | Windows, macOS, Linux, Android | later |
| D | Tablet or PC | A network gateway speaking the AOW link: `aow-linkd`, a reference ESP32 gateway (§4), later boards | every platform, including iOS | client built; discovery and the HAT's listening not (§3) |
| E | Linux PC with a CAN interface of its own (`can0`) | SocketCAN directly | Linux | later, small |

iOS has only D: its apps cannot reach a USB adapter.

GVRET (SavvyCAN's protocol) is not a transport for the terminal. Only AiO firmware v26 serves it,
for a board not yet released, and it has no liveness or stale-frame rules (TRANSPORT.md §3). A
compatibility client could come later if gateways that speak it turn up in the field.

## 2. What is trusted

**The machine network.** A device on the tractor's own network (a cable, or the Wi-Fi of the
gateway or the unit) may put frames on the implement bus. This is the trust the steer and machine
PGNs already have: every AgOpenGPS module takes them from whoever sends them on 192.168.5.x. The VT
does not need a higher bar than steering.

- **No pairing code.** It would be a step every gateway needs a farmer-friendly way to show and
  every operator has to understand, against a threat the cab doesn't have. Joining the network
  (its Wi-Fi password, once) is the pairing, a step farmers already know from printers and
  cameras. This closes TRANSPORT.md's open item on a pairing key (its Security section is to say so).
- **A gateway never listens on an uplink** (a farm or office network the unit also joins). It
  listens on loopback, on its cable, or on its own access point.
- **One terminal holds the bus.** The gateway takes one control client (TRANSPORT.md); a second
  one is refused with CLOSE busy. This is what stops two tablets in the yard fighting over an
  implement.

## 3. Connecting without addresses

Today the operator types the gateway's address into the card's gear. Instead:

- **Gateways announce themselves** by DNS-SD as `_aowlink._tcp` (already allowed by
  TRANSPORT.md), with the gateway's name and the protocol version in TXT. The gear lists the
  gateways found, by name ("AOW HAT 1234", "ISOBUS gateway"); one tap connects. Typing an address
  stays as the fallback. USB adapters are listed as now.
- **The setting keeps the choice by name and address**, so the terminal reconnects at start-up as
  it does now, and a gateway that comes back on another address is found again by its name.
- **Busy is said in words.** CLOSE busy carries the name of the terminal holding the bus; the card
  shows "In use by <name>" and stops retrying until the operator asks again, instead of retrying
  every second.

Per platform:

| Platform | Browsing DNS-SD | Needs |
|---|---|---|
| iOS | `NWBrowser` / `NSNetServiceBrowser` | `NSLocalNetworkUsageDescription` and `NSBonjourServices` (`_aowlink._tcp`) in Info.plist; the system asks the user once. No multicast entitlement (that is only for raw broadcast). |
| Android | `NsdManager` | nothing new (the head already holds a multicast lock) |
| Windows, macOS, Linux | a small mDNS query in .NET (no built-in API) | a dependency or ~200 lines of our own; to decide |

Where the browsing lives: in SharpIsoTerm beside `AowLink` (`AowLinkBrowser`), so any host
gets it; AgOpenWeb's service lists the results in the link message as it lists serial ports.

## 4. Gateways

- **`aow-linkd` (HAT).** Switched on from the ISOBUS page as now. When on, it listens on the
  machine side (the access point or the module cable), never on an uplink, and announces itself.
  Where it listens is chosen on the page, not in `/etc/aow-hat/linkd.env`. With AgOpenWeb on the
  unit (setup A) it also listens on loopback. Its CLOSE busy names the holder. Work in
  AgOpenWeb-HAT.
- **A reference gateway for cheap hardware.** An ESP32 with a CAN transceiver, on its own access
  point or a cable, speaking the AOW link and announcing itself; the firmware published with the
  protocol. Which board to recommend is open (§7).
- **Later boards** (the v26 AiO board when released): the AOW link next to their GVRET.

`bench/aowlink_check.py` (SharpIsoTerm) checks any gateway against the protocol; a new
gateway passes it before it is recommended.

## 5. Packaging

SharpIsoTerm is private and unpublished, so the terminal is built into AgOpenWeb only with
`-p:SharpIsoTermDir=…`; no nightly or release has it. Setup A in particular needs it in the Linux
arm64 bundle. Until the library is published, options are a private package feed the release
workflow can read, or building it in the HAT installer from a checkout. To decide with whoever
owns the library's publication.

## 6. Order

1. **Discovery** (§3): `_aowlink._tcp` announced by `aow-linkd` and the bench gateway;
   `AowLinkBrowser` in the library; the gear lists gateways; iOS plist entries. Tested with the
   iPad and the Galaxy Tab against the HAT on the bench network, with no address typed.
2. **The HAT's listening** (§4): the machine-side interface from the ISOBUS page, CLOSE busy with
   the holder's name, the card's "In use by". Ends the bench's SSH relay through the Mac.
3. **Packaging** (§5), for setup A and for the nightlies.
4. **The reference gateway** (§4), after the board is chosen.
5. **gs_usb and PEAK** (setup C), **SocketCAN** (setup E).

## 7. Open

- The reference gateway's hardware: an ESP32 board with a CAN transceiver that farmers can buy
  ready-made, or a board already known in the AgOpenGPS world.
- The desktop mDNS query: a dependency or our own.
- Packaging (§5).
- Whether a second tablet may take the bus over from the first (an explicit "take over" on the
  card) or must wait for it to let go. Starting with wait.

## Built

- **2026-10-25, discovery (§6 step 1, client side):** `AowLinkBrowser` in SharpIsoTerm (own
  mDNS query on the desktop and Android, `NSNetServiceBrowser` on iOS, so the desktop mDNS question
  in §7 is settled: our own, no dependency); the gear lists gateways by name with Refresh, keeps
  the name (`VtGatewayName`) and follows the gateway to a new address while not connected; the
  iOS plist entries. Tried with a gateway announced by hand (`dns-sd -R`) on the Mac (headless
  app: listed, picked, followed to a new port) and the Galaxy Tab (listed at the Mac's LAN address). Gateways announcing themselves comes with step 2: `aow-linkd` announces only
  once it listens beyond loopback.

- **2026-10-25:** one to four terminals, one card each (AOW #429); the gateway client `AowLink`
  (library PR #1, #2); the slcan USB adapter on Windows, macOS, Linux and Android, and the
  terminal on the Android and iOS heads (AOW #433, library PR #3). On the bench: a CANable on the
  Mac and on the Galaxy Tab, and the iPad and Galaxy Tab through the HAT's gateway (relayed by SSH
  from the Mac, because `aow-linkd` listens on loopback only: §6 step 2 ends that).
