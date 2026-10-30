# VoipDemo

A Docker-based VoIP proof of concept using **Asterisk**, **ASP.NET Core**, and a SIP softphone such as **Linphone**.

The application receives calls through Asterisk's REST Interface (ARI). When a caller dials extension **500**, Asterisk hands control to the .NET application, which answers the call and requests playback of a custom greeting.

## Architecture

```text
Linphone (SIP extension 1001)
         | SIP / RTP
         v
Asterisk (Docker)
         | ARI REST + event WebSocket
         v
VoiceApp (ASP.NET Core)
         | answer / play commands
         v
Asterisk -> RTP audio -> Linphone
```

Asterisk handles SIP signaling and RTP media. VoiceApp handles call events and call-control commands. **The current implementation plays a prerecorded sound; it does not yet stream audio through .NET or use AI.**

## Project layout

```text
VoipDemo/
├── compose.yaml
├── .env                  # local secret; do not commit
├── asterisk/
│   ├── Dockerfile
│   ├── ari.conf
│   ├── extensions.conf
│   ├── http.conf
│   ├── pjsip.conf
│   ├── rtp.conf
│   └── sounds/
│       └── greeting.wav  # or a compatible greeting.slin
└── src/
    └── VoiceApp/
        ├── Dockerfile
        ├── Program.cs
        └── Services/
            ├── AsteriskClient.cs
            └── AsteriskEventService.cs
```

## Prerequisites

- Docker Desktop with Docker Compose
- A SIP softphone (tested with Linphone on macOS)
- .NET SDK if developing VoiceApp outside Docker
- FFmpeg if converting or generating custom audio files

## Configuration

### Environment variables

Create `.env` alongside `compose.yaml`:

```dotenv
ARI_PASSWORD=choose-a-strong-local-secret
```

The `voiceapp` service reads the value through Docker Compose:

```yaml
environment:
  Asterisk__BaseUrl: http://asterisk:8088/ari/
  Asterisk__Username: voiceapp
  Asterisk__Password: ${ARI_PASSWORD}
```

ASP.NET Core maps `Asterisk__Password` to `Asterisk:Password`. You do not need to store the password in `appsettings.json`.

**Important:** The ARI password in `asterisk/ari.conf` must match `ARI_PASSWORD`. Compose does not substitute variables inside `ari.conf` automatically.

### Asterisk ARI

`asterisk/ari.conf`:

```ini
[general]
enabled=yes
pretty=yes

[voiceapp]
type=user
read_only=no
password_format=plain
password=choose-a-strong-local-secret
```

The `voiceapp` ARI application name is also used by the dialplan and WebSocket subscription. The ARI account name and application name happen to match here, but they are separate concepts.

### SIP endpoint and dialplan

The local test SIP account is extension **1001**. Its credentials are configured in `asterisk/pjsip.conf`, separately from the ARI credentials. Use a strong SIP password if the service is accessible beyond a trusted development network.

The dialplan routes extension **500** to the ARI application:

```ini
[internal]
exten => 500,1,Stasis(voiceapp)
 same => n,Hangup()
```

VoiceApp answers the call through ARI. Avoid answering the call twice in both the dialplan and application.

### Mount configuration and sounds

For development, mount Asterisk configuration directly rather than baking it into the image:

```yaml
services:
  asterisk:
    build: ./asterisk
    ports:
      - "5060:5060/udp"
      - "10000-10099:10000-10099/udp"
      - "127.0.0.1:8088:8088"
    volumes:
      - ./asterisk/pjsip.conf:/etc/asterisk/pjsip.conf:ro
      - ./asterisk/extensions.conf:/etc/asterisk/extensions.conf:ro
      - ./asterisk/http.conf:/etc/asterisk/http.conf:ro
      - ./asterisk/ari.conf:/etc/asterisk/ari.conf:ro
      - ./asterisk/rtp.conf:/etc/asterisk/rtp.conf:ro
      - ./asterisk/sounds:/var/lib/asterisk/sounds/custom:ro
    networks:
      - voip
```

The `voiceapp` service should also join the `voip` network. Inside that network it reaches Asterisk at `http://asterisk:8088/ari/`. Binding port 8088 to `127.0.0.1` restricts host access to local development.

Mounted file changes are visible immediately inside the container, but Asterisk may need a module reload or restart to apply configuration changes. Compose volume-map changes require container recreation.

## Start the services

From the directory containing `compose.yaml`:

```bash
docker compose up -d --build
```

Check status and logs:

```bash
docker compose ps
docker compose logs -f voiceapp
docker compose logs -f asterisk
```

VoiceApp should report `Connected to Asterisk ARI` once its event WebSocket subscription is established. `depends_on` controls startup order, not readiness; the event listener reconnects if Asterisk is still starting.

## Verify ARI

Test the ARI REST API from the Mac:

```bash
curl -i -u voiceapp http://localhost:8088/ari/asterisk/info
```

Enter the password from `.env` when prompted. Expect **HTTP 200**. HTTP 401 means Asterisk rejected the credentials.

Inspect the active configuration and registered ARI application:

```bash
docker compose exec asterisk asterisk -rx "ari show users"
docker compose exec asterisk asterisk -rx "ari show apps"
docker compose exec asterisk cat /etc/asterisk/ari.conf
```

The event listener subscribes to `/ari/events?app=voiceapp` using ARI authentication. Do not log the full WebSocket URL if it includes `api_key=username:password`.

## Configure Linphone

Create a manual SIP account using the credentials from `pjsip.conf`:

| Setting | Local test value |
| --- | --- |
| Username | `1001` |
| Authentication ID | `1001` |
| Password | SIP password from `pjsip.conf` |
| Domain / SIP server | Mac's reachable LAN IP address |
| Transport | UDP |

**Authentication ID matters:** registration initially failed until it was explicitly set to `1001`.

Verify registration:

```bash
docker compose exec asterisk asterisk -rx "pjsip show endpoints"
docker compose exec asterisk asterisk -rx "pjsip show contacts"
```

The endpoint can exist while being `Unavailable` if Linphone has not registered.

## Audio greeting

Place a compatible audio file in `asterisk/sounds/`, mounted to `/var/lib/asterisk/sounds/custom/` in the container.

For example, convert a recording to mono, 8 kHz, signed 16-bit PCM WAV:

```bash
ffmpeg -i input.wav -ar 8000 -ac 1 -c:a pcm_s16le asterisk/sounds/greeting.wav
```

Or generate raw 8 kHz signed linear audio with the `.slin` extension:

```bash
ffmpeg -i input.wav -f s16le -ar 8000 -ac 1 asterisk/sounds/greeting.slin
```

Do not rename a WAV file to `.slin` without converting its contents.

Verify that Asterisk sees the sound:

```bash
docker compose exec asterisk asterisk -rx "core show sounds"
```

The .NET call handler requests playback by **sound identifier**, without a file extension:

```csharp
await asterisk.PlayAsync(channelId, "custom/greeting", cancellationToken);
```

A successful ARI playback request means Asterisk accepted the command; it does not prove the caller heard audio. Observe playback events and RTP diagnostics when troubleshooting.

## Make a test call

1. Confirm VoiceApp is connected to ARI and Linphone is registered.
2. Dial **500** in Linphone.
3. Asterisk enters `Stasis(voiceapp)`.
4. VoiceApp receives `StasisStart`, answers, and requests `custom/greeting` playback.
5. Asterisk streams the greeting to Linphone over RTP.

To inspect Asterisk during a call:

```bash
docker compose exec asterisk asterisk -rvvv
```

Then enable:

```text
core set verbose 5
pjsip set logger on
rtp set debug on
```

Turn off verbose RTP logging afterward with `rtp set debug off`.

## macOS / Docker Desktop RTP networking

A key issue during testing was that Asterisk played `custom/greeting.slin` but sent RTP to **127.0.0.1**. Inside a container, that loopback address refers to the container itself, not Linphone on the Mac.

The working setup used the Mac's reachable LAN address for SIP/media instead of loopback. In this development session, the Mac's address was **192.168.1.24**; replace it with the current address on your network.

For a Docker Desktop setup, the Asterisk PJSIP transport may need settings similar to:

```ini
[transport-udp]
type=transport
protocol=udp
bind=0.0.0.0:5060
local_net=172.22.0.0/16
external_signaling_address=192.168.1.24
external_media_address=192.168.1.24
```

**Do not copy these IP values blindly.** Verify your current Mac IP and Docker subnet:

```bash
ipconfig getifaddr en0
docker network inspect voipdemo_voip --format '{{range .IPAM.Config}}{{.Subnet}}{{end}}'
```

Also ensure Linphone advertises a media address reachable by Asterisk; disable STUN/ICE temporarily when diagnosing a local setup. Asterisk's external address settings affect what it advertises, not necessarily the destination it learns from the client's SDP or RTP packets.

After changing transport configuration, restart Asterisk and inspect the negotiated SDP (`c=IN IP4`, `m=audio`) and RTP debug output.

## Common problems

| Symptom | Check |
| --- | --- |
| `no configuration file provided` | Run Compose from the directory containing `compose.yaml`, or use `-f` |
| ARI WebSocket returns HTTP 401 | Match `.env` and the **active** `/etc/asterisk/ari.conf`; verify REST with `curl` first |
| Changes to `ari.conf` seem ignored | Confirm the mounted file inside the container; reload ARI or restart Asterisk |
| SIP endpoint is `Unavailable` | Verify Linphone registration, SIP password, and Authentication ID |
| `Unable to open hello-world` | Change the .NET playback identifier to `custom/greeting` and rebuild VoiceApp |
| Playback starts but no sound | Inspect RTP destination and SDP; avoid container-local `127.0.0.1` |
| `204 No Content` from playback API | Request accepted, but playback/audio delivery still needs verification |

## Development workflow

- After changing **C# code**, rebuild and recreate VoiceApp:

  ```bash
  docker compose up -d --build --force-recreate voiceapp
  ```

- After changing **mounted Asterisk configuration**, reload the appropriate module or restart Asterisk:

  ```bash
  docker compose restart asterisk
  ```

- After changing **Compose mounts**, recreate the affected container:

  ```bash
  docker compose up -d --force-recreate asterisk
  ```

- After changing **mounted audio files**, no image rebuild is necessary.

## Next steps: dynamic voice and AI

The current proof of concept demonstrates SIP registration, ARI call control, custom audio playback, and working RTP routing.

Possible next milestones:

1. Track `PlaybackStarted`, `PlaybackFinished`, and call termination events in a per-call session.
2. Generate speech dynamically through a text-to-speech service.
3. Introduce Asterisk External Media to stream audio to and from a .NET audio processor.
4. Add speech recognition and conversational AI, or a realtime speech-to-speech model.
5. Add interruption handling, cancellation, audio format negotiation, and production-grade authentication/TLS.

This is a **local development proof of concept**, not a hardened public SIP/telephony deployment. Do not expose ARI or unauthenticated SIP endpoints publicly.
