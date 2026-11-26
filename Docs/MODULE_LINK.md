# Why the modules need a cable

The steer, GPS and machine modules send AgOpenWeb timing-critical data over UDP: positions 10 or
more times a second, the wheel angle, the switches. **That link must be an Ethernet cable.** Wi-Fi
between the modules and the computer that runs AgOpenWeb is not supported, and no setting or
future version of the app can make it work.

This is the same rule as AgOpenGPS, for the same reason.

## Which setup is yours

| Setup | Module link | What Wi-Fi is for |
|-------|-------------|-------------------|
| A tablet or PC runs AgOpenWeb, with a Teensy or other microcontroller board (the v4.x boards, the AiO) | **Ethernet cable**, as with AgOpenGPS | Nothing the modules use. Internet for NTRIP is fine |
| AgOpenWeb runs on the board itself (a HAT on a single-board computer) | Inside the board: the data never crosses a network | The screen: a tablet or phone opens `http://<board>:5174` |

In the first setup, a tablet that is cabled to the modules' router **and** has joined that router's
Wi-Fi hears every module twice. Turn the Wi-Fi off, or join a different network with it.

## The data is late, not wrong

A position that arrives 1.5 s late says where the tractor was 1.5 s ago: 4 m back at 10 km/h. The
app can throw it away or steer on it. It cannot turn it into the current position, because the
current one has not arrived yet: it is still in the router.

That is why this cannot be fixed in the app. The delay happens in the network, before the app sees
anything.

## What Wi-Fi does to module data

The modules broadcast their data. A Wi-Fi router holds broadcasts while any device on its Wi-Fi is
saving power (a phone with its screen off is enough), and sends them out a few at a time after a
beacon. What it has no room for, it drops.

Measured on a tester's tablet, 22 November 2026: one module, the tablet on cable and on Wi-Fi to the
same router, 10 minutes (PR #900).

| | Cable | Wi-Fi |
|---|---|---|
| Datagrams received | 47,607 (79 a second) | 23,400 (39 a second) |
| How they arrive | as sent | 8 at a time, every 205 ms |
| Behind the cable | | 1.49 to 1.73 s (median 1.56 s) |

So over Wi-Fi the data was late, came in bursts, and half of it never came. On another day the same
router and tablet were only 0.3 s late: the delay comes and goes with what the other Wi-Fi devices
are doing, so a setup that seems to work on the bench can fail in the field.

## Common questions

**Can't the app buffer it and smooth it out?** A buffer makes late data later. And half the
datagrams were dropped by the router: there is nothing to buffer.

**Can't the app drop the late ones and use the rest?** They are all late, by a different amount each
time.

**The screen works over Wi-Fi. Why not the modules?** The screen only shows. The computer that runs
AgOpenWeb does the steering, 100 times a second, from the modules' data. A screen frame that comes
late draws the tractor a moment late and steers nothing. The screen's link is also a connection
that resends what is lost, to one device, which a router does not hold back the way it holds
broadcasts.

**My board has Wi-Fi (an ESP32, for example).** A module that sends steering or position data over
Wi-Fi has the same problem, whatever the board.
