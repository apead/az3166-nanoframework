# AZ3166 MQTT telemetry + OLED dashboard (nanoFramework)

Sends every reading the MXCHIP AZ3166 has to an on-premise MQTT broker and shows a dashboard on the
on-board OLED.

Requires the Wi-Fi firmware **1.0.0.8 or later** (no firmware change needed, plain MQTT on port 1883).
The NuGet packages are included in `packages\` and match that firmware - don't update them.

## Configure

At the top of `Program.cs`:

| Setting | |
|---|---|
| `WifiSsid`, `WifiPassword` | your 2.4 GHz network |
| `MqttBroker`, `MqttPort` | host name or IP of your broker, normally port 1883 |
| `MqttUser`, `MqttPassword` | leave empty for anonymous access |
| `TopicPrefix` | default `az3166` |
| `TelemetryIntervalSeconds` | default 10 s (can be changed at runtime, see commands) |

## Topics

The device id is `az3166-` plus the last 3 bytes of the MAC address (e.g. `az3166-a4c0fe`, shown on the
SYSTEM page and in the debug output).

| Topic | Direction | Content |
|---|---|---|
| `az3166/<id>/status` | device → broker | `online` / `offline` (retained; `offline` is the last will) |
| `az3166/<id>/info` | device → broker | device information JSON (retained) |
| `az3166/<id>/telemetry` | device → broker | all readings as JSON (includes the Wi-Fi diagnostics) |
| `az3166/<id>/wifi` | device → broker | Wi-Fi diagnostics JSON (retained, updated when the connection changes) |
| `az3166/<id>/cmd` | broker → device | `interval=N`, `text=Hello`, `ping` |

Example telemetry:

```json
{"device":"az3166-a4c0fe","uptime":125,"temperature":24.31,"humidity":41.20,
 "pressure":1012.84,"pressureSensorTemperature":25.10,
 "accel":{"x":0.012,"y":-0.020,"z":1.003},"gyro":{"x":0.35,"y":-0.26,"z":0.09},
 "magnetometer":{"x":-120.0,"y":310.5,"z":-402.0},"heading":111.1,
 "buttonA":false,"buttonB":false,
 "wifi":{"ssid":"MyWiFi","ip":"192.168.8.102","rssi":-47},
 "messages":13,"freeMemory":154312}
```

## Watch it / send commands (Mosquitto)

```
mosquitto_sub -h 192.168.1.10 -t "az3166/#" -v
mosquitto_pub -h 192.168.1.10 -t "az3166/az3166-a4c0fe/cmd" -m "text=Hello AZ3166"
mosquitto_pub -h 192.168.1.10 -t "az3166/az3166-a4c0fe/cmd" -m "interval=2"
```

Quick broker for testing (Docker): `docker run -it -p 1883:1883 eclipse-mosquitto mosquitto -c /mosquitto-no-auth.conf`

## Display

While starting, the display shows a boot log (sensor detection, scan, join result and time, DHCP address,
gateway, DNS, MQTT). Then seven pages, rotating every 8 s; **button A** = previous page, **button B** = next page (rotation pauses
for 30 s after a button press).

- **CLIMATE** - temperature (large), humidity gauge, pressure
- **MOTION** - bubble level from the accelerometer, X/Y/Z in g, rotation rate
- **COMPASS** - compass rose with needle, heading and cardinal direction
- **WIFI** - connection state and attempts, RSSI with signal bars, last join result, access point BSSID,
  join and DHCP times, link drops, connection uptime
- **NETWORK** - IP, subnet mask, gateway, DNS, MQTT state, messages sent, MQTT reconnects
- **LOG** - the last events (link lost/up, IP changes, MQTT connects, errors)
- **SYSTEM** - uptime, firmware version, MAC, buttons, last command

The header shows Wi-Fi signal bars and an MQTT box (filled = connected). Messages sent with `text=` pop up
over the current page for 5 s.

If the picture is upside down, set `Rotate180 = true` in `Ssd1306.cs`.

Wi-Fi diagnostics JSON (`wifi` topic and `wifi` object in the telemetry):

```json
{"state":"Connected","ssid":"MyWiFi","bssid":"..","rssi":-47,"bars":4,"networksFound":6,
 "ip":"192.168.8.102","mask":"255.255.255.0","gateway":"192.168.8.1","dns":"192.168.8.1",
 "mac":"02:7C:03:A4:C0:FE","attempts":1,"lastResult":"OK","joinMs":2410,"dhcpMs":1230,
 "linkDrops":0,"addressChanges":1,"connectedFor":352,"mqttReconnects":0}
```

## Start up checks

The Visual Studio output shows each start up step (`Init: OLED`, `Init: HTS221`, ...). The display, sensors
and buttons are opened on a separate thread: if one of them hangs, `HW init stuck: <step>` is reported and the
app carries on with Wi-Fi and MQTT.

## Notes

- The compass heading assumes the board is flat and isn't calibrated (hard-iron offsets of the board will
  skew it).
- Sensors are detected at start up; a missing sensor is left out of the telemetry and its page says so.
- Plain MQTT is unencrypted; use it on a trusted network. TLS (port 8883) needs a firmware build with TLS.
