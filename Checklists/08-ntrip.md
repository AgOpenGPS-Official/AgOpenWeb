# 08 NTRIP

**Owner:** _
**Setup:** an RTK receiver and a caster login (a base or a VRS service). Hardware only; the
simulator has no corrections.

| # | Check | HW | Notes / issue |
|---|---|---|---|
| 8.1 | **Profile**: create, edit, delete | | |
| 8.2 | **Test** the login; the **source table** lists the mount points | | |
| 8.3 | **Connects on start** with auto-connect on; the NTRIP on/off button | | |
| 8.4 | The status in Network IO: connected, bytes, the reason for a failure | | |
| 8.5 | **RTK fix** within the usual time; the correction age shown | | |
| 8.6 | No gaps in the fix at field speed over half an hour | | |
| 8.7 | **VRS caster**: the receiver's position is sent (also in single and DGPS) | | |
| 8.8 | A wrong login or mount point says so and stops; other failures retry | | |
| 8.9 | A network change (Wi-Fi to phone hotspot) re-connects | | |
| 8.10 | Closing the field keeps the corrections flowing | | |
| 8.11 | **Broadcast corrections** on and off: the receiver gets them either way | | |
| 8.12 | A Bug Report Dump with NTRIP running has the connection and the RTCM messages in it | | |

**Signed off:** _ (name, date)
