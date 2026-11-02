# 09 Modules and PGNs

**Owner:** _ (someone with an AiO board, and the HAT if you have one)
**Setup:** the module network; a way to watch UDP (AgIO's PGN view, Wireshark, or the modules'
own serial output). Bench only: AgOpenWeb's settings are saved by the boards.

| # | Check | HW | Notes / issue |
|---|---|---|---|
| 9.1 | **Discovery**: the modules are found; the subnet scan and Auto Set | | |
| 9.2 | Module cards in Network IO: name, address, last heard | | |
| 9.3 | **Steer** PGN 254 (speed, status, steer angle, cross-track, sections) as AgOpenGPS sends | | |
| 9.4 | **Steer settings** PGN 252 and **config** PGN 251 as AgOpenGPS sends | | |
| 9.5 | **From steer** PGN 253: WAS angle, heading, roll, switch states shown and used | | |
| 9.6 | **Machine** PGN 239 (sections, U-turn, hydraulic lift, tram, geoStop) | | |
| 9.7 | **Machine settings** PGN 238 and **pins** PGN 236 | | |
| 9.8 | **IMU** PGN 211: roll and heading; the dual-heading warning rules | | |
| 9.9 | **GPS**: PANDA, PAOGI and plain NMEA give the same fix quality, age and heading as AgOpenGPS | | |
| 9.10 | **GPS out** PGN 100 to a seed monitor or rate controller | | |
| 9.11 | **Rate controller** loopback PGNs | | |
| 9.12 | A setting changed in AgOpenWeb reaches the module and survives the module's restart | | |
| 9.13 | Pulling the module's cable: AgOpenWeb shows it lost; plugging back in recovers | | |

**Signed off:** _ (name, date)
