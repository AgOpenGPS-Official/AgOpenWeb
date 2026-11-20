# NTRIP and RTCM forwarding

How AgOpenWeb connects to a caster, falls back and forwards corrections. The short version is in `CLAUDE.md`.

## Connection format
`NtripSession` (`Services/Ntrip/`) makes the connection and asks with NTRIP 2.0:
```
GET /mountpoint HTTP/1.1
Host: caster.example.com:2101
Ntrip-Version: Ntrip/2.0
User-Agent: NTRIP AgOpenWeb
Authorization: Basic base64(username:password)
Ntrip-GGA: $GPGGA,...
Connection: close
```
A caster that did not understand it (a reply that is no NTRIP or HTTP reply, or HTTP 400 or 505) gets the
NTRIP 1.0 request (`GET /mountpoint HTTP/1.0`, no Host or version header, the position sent right after
the answer) once; never after a 401, 403, 404, an error text or a source table. The version that delivered
corrections is remembered per service. `NtripResponse.Classify` sorts every reply into an `NtripCategory`
(wrong login and unknown mount point end the session, the rest is retried after 1, 2, 4, 8, 15 s, and a
network change ends the wait, and ends a stream that then stays quiet for 3 s, as on a move from Wi-Fi to the phone's hotspot); `NtripConnectionTester` (the Test button) opens its connection the same way.
Every wait has a timeout (`NtripTimeouts`). Design: `Plans/Completed/NTRIP_MAIN_FALLBACK_PLAN.md`.

**Main and fallback service.** Two exclusive modes (`Connections.NtripProfilePerField`, `config.set|conn.ntripProfilePerField`).
The default is one profile (the default profile) with an optional fallback service (`NtripProfile.Fallback`): a field load
changes nothing. With the setting on, a loaded field switches the profile and the fallback is not used. The client
(`NtripClientService`, rules in `NtripFailover`) switches to the fallback by itself only with the profile's Auto-switch on:
after `SwitchAfterFailures` failed attempts, or at once for a wrong login or mount point. The operator switches in both
directions at any time (`ntrip.switch|main|fallback`, `INtripClientService.SwitchToAsync`). It **never goes back by itself**:
while the fallback is in use after a failure, the main service is tried slowly in the background and a `MainBack` notice
says when it answers; not when both are one login on one caster. Every switch raises `Notice` and says that RTK may be lost.
In the web client the Network IO card keeps to the first lines and the switch; NTRIP Settings (`renderNtripSettings` in
`js/network.js`: headline, advice, checks, the mode) shows the rest, from the `NtripSessionDto` that ends the Status frame;
`ntrip.use|id` connects with a profile now (`MainViewModel.UseNtripProfileAsync`; the default too in the one-profile mode); the session frame carries
the profile name and the service in use (`ConnectionState.NtripProfileName/MainText/FallbackText`). A new default profile connects at once; the
*NTRIP active / disabled* button (`conn.ntripEnabled`) connects the default profile even with its auto-connect off. `ntrip.mountpoints` asks the caster for its
source table (`NtripSourcetable`), answered in its own frame; a profile's fallback ends its entry in the profiles frame; `ntrip.save` carries it (`NtripProfileFields`), `ntrip.test` can test it. A switch is a toast, "the main service
is back" is the host's confirmation dialog.

## RTCM forwarding
`NtripClientService` frames the caster's stream into RTCM 3 messages (`RtcmFramer`, after
`ChunkedDecoder` if the reply is chunked) and queues whole messages (`RtcmQueue`): 256-byte
datagrams about 25 ms apart, never closer than 10 ms; on a backlog the newest observation
epoch and newest station data win, and nothing is cut mid-message. Datagrams go to the GPS
module's own address when its position sentences have been heard in the last 10 s, else to
the subnet broadcast (or always, with Network IO → "Broadcast corrections"). Network IO shows
the receiver's correction age, warnings and a message table; bug report dumps carry
`ntrip_rtcm.txt`. Design, bench results and open checks: `Plans/RTCM_FORWARDING_PLAN.md`;
bench scripts: `Tools/rtcm-bench`.
