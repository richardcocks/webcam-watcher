# webcam-watcher

Turns a light on in Home Assistant while your webcam is in use, on one PC or several.

A small Windows console app watches for apps using the webcam and reports to a Home Assistant
webhook. A Home Assistant package keeps track of every machine that reports and switches a light
(e.g. a key light for video calls) on while any of them has its camera on.

- No agent to install and no admin rights needed: a single exe you build yourself.
- Reacts within milliseconds. It doesn't poll; Windows notifies it when the camera starts or stops.
- One webhook for any number of machines. Each machine identifies itself and can request its own brightness.
- A machine that goes silent (laptop closed mid-call, app killed) expires after 3 minutes.

## How it works

Windows records webcam use per app under

```
HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam
```

(Store apps directly under it, desktop apps under `NonPackaged`). While an app has the camera open,
its `LastUsedTimeStop` value is `0`. The watcher asks Windows to signal changes to that key
(`RegNotifyChangeKeyValue`), re-reads it when signalled, and posts to Home Assistant on every change
and as a heartbeat once a minute:

```json
{"machine": "MY-PC", "active": true, "apps": ["C:\\Program Files\\...\\ms-teams.exe"], "brightness": 70}
```

## Build

Needs the .NET 10 SDK (or change `TargetFramework` in the csproj to an older `-windows` target).

```
dotnet build -c Release
```

To produce a single exe that runs without .NET installed:

```
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

Don't enable trimming or Native AOT; the JSON payload uses reflection-based serialization.

## Run

```
webcam-watcher <webhook-url> [machine-name] [--brightness 1-100]
```

| Setting | Argument | Environment variable | Default |
|---|---|---|---|
| Webhook URL | 1st positional | `HA_WEBHOOK_URL` | required |
| Machine name | 2nd positional | `HA_MACHINE` | computer name |
| Brightness (%) | `--brightness` / `-b` | `HA_BRIGHTNESS` | 70 |

For example:

```
webcam-watcher http://homeassistant.local:8123/api/webhook/YOUR_WEBHOOK_ID work-laptop --brightness 60
```

To start it at login, put a shortcut in `shell:startup`. To hide the console window, set
`<OutputType>WinExe</OutputType>` in the csproj (log output is then discarded).

## Home Assistant setup

[`home-assistant/webcam_key_light.yaml`](home-assistant/webcam_key_light.yaml) is a
[package](https://www.home-assistant.io/docs/configuration/packages/) containing everything needed:

| Entity | Purpose |
|---|---|
| `input_text.webcam_reports` | Machines currently reporting their camera on, as `MACHINE=timestamp;...` |
| `input_number.webcam_key_light_brightness` | Brightness requested by the machine whose camera came on most recently |
| `binary_sensor.webcam_active` | On while any machine has reported its camera on in the last 3 minutes |
| `automation.webcam_report` | Receives the webhook and updates the two helpers |
| `automation.webcam_key_light` | Light on at the requested brightness; off 1s after the last camera stops |

1. Copy the file to `<config>/packages/` and enable packages in `configuration.yaml`:
   ```yaml
   homeassistant:
     packages: !include_dir_named packages
   ```
2. Replace `YOUR_WEBHOOK_ID` with a long random string. Anyone who knows it can post reports.
3. Replace `light.desk_key_light` with your light.
4. Restart Home Assistant.

The webhook is `local_only`, so machines must reach Home Assistant from your local network. If you
need to report from outside it (e.g. through a VPN that doesn't route to your LAN), set
`local_only: false` and use your external URL.

## Notes

- If an app crashes while it has the camera open, Windows can leave `LastUsedTimeStop` at `0`, so
  that machine keeps reporting the camera as on until the app next opens and closes it.
- If two machines have their cameras on with different brightness settings, the one that started
  most recently wins, and the light stays at that level until all cameras are off.
- Only the webcam is watched. Changing `webcam` to `microphone` in the registry path gives the
  same for microphones.

## License

MIT
