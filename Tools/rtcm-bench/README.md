# RTCM forwarding bench

Scripts for measuring how RTCM corrections travel from an NTRIP caster, through AgOpenWeb, to
a GPS module and its receiver, when the network misbehaves. They produced the baseline in
[Plans/RTCM_FORWARDING_PLAN.md](../../Plans/RTCM_FORWARDING_PLAN.md) and are the acceptance
test for each phase of that plan.

You need a real caster, a module with a receiver on the bench, Python 3, and (for the app
runs) Playwright in some Python environment.

> The app sends its steer and machine configuration to every module on the network when it
> starts, and the firmware saves it. Run these against a bench board, with `AGOPENWEB_DATA`
> pointing at a scratch folder.

## The two legs

```
caster ──TCP──► relay.py ──TCP──► AgOpenWeb ──UDP 2233──► module ──serial──► receiver
                (stall, trickle)       │                     │
                              record_udp.py 2233     record_udp.py 9999
                              (what was forwarded)   (fix quality, differential age)
```

- **Caster to app** (`run-app.sh`): the relay holds the stream and releases it in a burst, or
  slows it down. TCP hides packet loss from the app as exactly that.
- **App to module** (`forward.py`): a stand-in forwarder that paces datagrams as the app does
  and drops a share of them. Run it with the app's NTRIP off.

## Run the app through a bad caster link

```sh
export CASTER_HOST=192.168.1.50 CASTER_MOUNT=mymount CASTER_CREDS=~/.ntrip-test
export AGOPENWEB_DATA=/tmp/aow-bench PYTHON=~/venv/bin/python
dotnet build Platforms/AgOpenWeb.Desktop/AgOpenWeb.Desktop.csproj
Tools/rtcm-bench/run-app.sh /tmp/run1 '[[240,"stall",15,0],[300,"trickle",30,600]]' 400
```

`CASTER_CREDS` is a file holding `user:password`. The schedule is a list of
`[start_s, kind, duration_s, param]`, timed from the app's first connection. Leave a few
minutes before the first episode: the receiver has to reach RTK Fixed first.

## Drop datagrams on the way to the module

```sh
python3 Tools/rtcm-bench/record_udp.py 9999 /tmp/run2/nmea.bin &
python3 Tools/rtcm-bench/forward.py $CASTER_HOST 2101 $CASTER_MOUNT $CASTER_CREDS 192.168.5.126 \
    '[[240,60,0.02],[360,60,0.10]]' /tmp/run2/events.log
python3 Tools/rtcm-bench/analyze.py /tmp/run2
```

The destination is a module address (unicast) or the subnet's `.255` (broadcast). The schedule
is a list of `[start_s, duration_s, loss]`.

## What `analyze.py` prints

- The episodes, and the app's own drop messages.
- The forwarded stream, re-framed as the receiver gets it: whole messages, checksum failures
  and stray bytes in each episode and in the 20 s after it.
- The receiver's fix quality and differential age: every change of fix, and per episode the
  qualities seen, the largest age and the seconds spent outside RTK Fixed.

## Files

| File | What it is |
|------|------------|
| `run-app.sh` | Headless app + relay + both recorders + browser driver + analysis |
| `relay.py` | TCP relay with scheduled stalls and slow-downs |
| `forward.py` | Stand-in forwarder with scheduled datagram loss |
| `record_udp.py` | Timestamped capture of a UDP port |
| `drive.py` | Points the app's NTRIP at the relay and opens a field (Playwright) |
| `analyze.py` | Summary of a run directory |
| `rtcm.py` | RTCM 3 framing and CRC-24Q, as `RtcmFramer` does it |
