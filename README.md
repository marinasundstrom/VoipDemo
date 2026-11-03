# VoipDemo

A VoIP proof of concept using Asterisk, ASP.NET Core, and a SIP softphone such as Linphone. Dial **500** to explore recorded audio, live text-to-speech, keypad input, and recording/playback through an Asterisk media WebSocket.

VoiceApp can run **locally on macOS** while Asterisk runs in Docker, or both can run in Docker. The local development profile runs an interactive menu with speech synthesized during the call using macOS `say` and FFmpeg. Docker defaults to the original Echo scenario using a prepared PCM announcement (or a test tone).

## How it works

```text
Linphone (extension 1001) <-- SIP / RTP --> Asterisk
                                             |
                       mixing bridge: caller + WebSocket media channel
                                             |
                                  media WebSocket (slin16)
                                             |
                                          VoiceApp
                                             ^
                                  raw PCM file from macOS say

VoiceApp also connects to Asterisk's separate ARI REST API and event WebSocket.
```

For each incoming call, VoiceApp answers, creates a mixing bridge, adds the caller, creates an incoming WebSocket external-media channel, and connects to `/media/{connectionId}`. After `MEDIA_START` and the media channel’s `StasisStart`, it adds that channel to the bridge and sends audio as binary frames. Connecting first matters: Asterisk can reject bridge insertion with HTTP 422 before the media channel enters Stasis. Both plain-text and JSON media control events are supported.

Call tasks run independently of the ARI event receive loop. Explicit media channel IDs and the `WebSocket/` name filter prevent recursive call setup. Caller hangup, setup failure, application shutdown, or ARI disconnection triggers cleanup of the media socket, external channel, bridge, and caller channel. A media channel leaving Stasis alone does not end the caller session. A lost ARI connection ends active calls because their state can no longer be tracked reliably.

In the Echo scenario, or menu option 5, VoiceApp plays a 200 ms beep, captures five seconds of incoming PCM in memory, and streams that recording back once. Audio received before the recording window is discarded; the call stays open after playback. Nothing is saved to disk. There is no speech recognition or AI integration yet.

## Prerequisites

- Docker Desktop with Docker Compose for Asterisk.
- A SIP softphone (tested with Linphone on macOS).
- .NET **11 SDK** for running VoiceApp locally; the project currently uses a prerelease .NET 11 OpenAPI package.
- macOS `say` and FFmpeg (`brew install ffmpeg`) for speech synthesis.
- Asterisk with `chan_websocket`. The locally checked image was certified-22.8-cert5 and included the module. The `22-cert` image tag can change, so verify the installed module below.

See the [Asterisk WebSocket channel documentation](https://docs.asterisk.org/Configuration/Channel-Drivers/WebSocket/) for protocol and version requirements.

## Configure credentials

Create `.env` in the repository root:

```dotenv
ARI_PASSWORD=choose-a-local-secret
```

Set the same password in `asterisk/ari.conf`, under `[voiceapp]`. Compose reads `.env` for the application environment; it does **not** substitute values inside `ari.conf`. Do not commit real credentials.

The SIP credentials for extension **1001** are separate and live in `asterisk/pjsip.conf`.

The dialplan in `asterisk/extensions.conf` routes extension 500 to `Stasis(voiceapp)`. VoiceApp answers through ARI; the dialplan does not answer first.

## Interactive scenarios (local macOS mode)

Development settings select `Demo:Scenario=Menu`. Use Linphone's keypad during a call:

| Key | Scenario |
| --- | --- |
| `1` | Play an 8-second recorded music sample (or your configured PCM file) |
| `2` | Synthesize new text during the call, including the current server time |
| `3` | Choose `1` sales, `2` support, or `3` opening hours; hear your choice confirmed |
| `4` | Enter exactly 10 digits followed by `#`; hear them read back individually |
| `5` | Hear instructions and a beep, record for five seconds, then hear your recording |
| `0` | Say goodbye and end the call |

Each scenario returns to the menu. Keys pressed during a prompt are queued; they do not interrupt playback. Choice prompts allow 15 seconds for input after playback, with up to three attempts. Number entry allows 30 seconds and three attempts; fewer/more than ten digits, nonnumeric input, or a missing `#` are rejected. Leading zeroes are preserved. Entered digits are not logged.

Generate the original demo melody once:

```bash
python3 scripts/create-demo-music.py
```

To substitute your own recording, convert it to raw PCM and set its absolute path:

```bash
ffmpeg -i your-music.wav -ar 16000 -ac 1 -c:a pcm_s16le -f s16le media/music.pcm
export Demo__RecordingFile="$PWD/media/music.pcm"
```

Live speech is behind `ISpeechSynthesizer`. `MacSpeechSynthesizer` invokes `say` and FFmpeg for each utterance using temporary files and cancellation-aware child processes; it sends the resulting PCM over the existing media socket. This is synthesis at call time, not streaming partial synthesis chunks. Replace that implementation to connect another TTS or AI provider later.

Optional settings:

```bash
export Speech__Voice=Samantha
export Speech__FfmpegPath=/opt/homebrew/bin/ffmpeg
export Demo__LiveText='This text can later come from an AI service.'
```

The current synthesizer requires macOS. Docker runs `Demo:Scenario=Echo` by default; do not enable Menu in Linux until a compatible speech provider is implemented. To run the original Echo flow locally, set `Demo__Scenario=Echo` before starting VoiceApp. Remove that override to return to Menu.

## Generate an Echo announcement on macOS

Run from the repository root:

```bash
./scripts/synthesize-speech.sh "Hello! After the beep, say something. I will record you for five seconds, then play it back."
```

The default output is `media/speech.pcm`, which is ignored by Git. Optionally specify an output path and an installed voice:

```bash
say -v '?'
./scripts/synthesize-speech.sh "Hello from your Mac." media/speech.pcm Samantha
```

The helper writes speech to a temporary AIFF file using `say`, then converts it with FFmpeg to **16 kHz, mono, signed 16-bit little-endian PCM**. It replaces the output only after conversion succeeds. Do not pass WAV or AIFF files directly to the media sender: their headers would be interpreted as audio.

Preview the generated raw file locally if desired:

```bash
ffplay -f s16le -ar 16000 -ac 1 media/speech.pcm
```

VoiceApp opens the configured file for each call, so regenerate it to change the next Echo call's greeting. This file-based helper is separate from the menu's live synthesis. Prepared files also work when VoiceApp runs in Linux Docker, where `say` is unavailable.

## Option A: run VoiceApp locally

Run these commands from the repository root. If a Docker VoiceApp is already running, stop it so only one app controls `voiceapp` calls:

```bash
docker compose stop voiceapp
docker compose up -d asterisk
python3 scripts/create-demo-music.py

export Asterisk__Password='the-password-from-ari.conf'
export Asterisk__AudioFile="$PWD/media/speech.pcm"
dotnet run --project src/VoiceApp --launch-profile http
```

The `http` launch profile selects Development settings and listens at `http://localhost:5144`. `appsettings.Development.json` points ARI at `http://localhost:8088/ari/`, exposed by Asterisk's Docker port mapping. A local `dotnet run` does **not** automatically read Compose's `.env` file.

`Asterisk__AudioFile` applies to the standalone Echo flow; omit/unset it to use the test tone there. The Menu flow generates its own prompts live. The audio path above is absolute because `dotnet run` uses the application project as its working directory.

Check the local app in a second terminal:

```bash
curl http://localhost:5144/health
```

`/health` reports that the HTTP app is running; it does not guarantee Asterisk connectivity. Wait for `Connected to Asterisk ARI` in the application logs. Stop the local app with Ctrl+C before switching to Docker mode. For automatic rebuilds, use `dotnet watch --project src/VoiceApp run --launch-profile http` with the same environment variables.

## Option B: run both services in Docker

Generate speech on the Mac first, then run:

```bash
VOICE_AUDIO_FILE=/app/media/speech.pcm docker compose up -d --build
```

Compose mounts `./media` read-only at `/app/media`. Add `VOICE_AUDIO_FILE=/app/media/speech.pcm` to `.env` if you want subsequent Compose commands to retain the speech setting. Leave it empty/unset to use the test tone:

```bash
docker compose up -d --build
docker compose logs -f voiceapp
```

Inside Docker, VoiceApp reaches ARI at `http://asterisk:8088/ari/`. The media WebSocket uses the same host, port, and TLS scheme with a `/media/` path. For another Asterisk host, override `Asterisk__BaseUrl`.

`depends_on` controls startup order, not readiness. VoiceApp retries the ARI event connection every five seconds. The existing VoiceApp Dockerfile requires the .NET 11 SDK and ASP.NET runtime images to be available; local mode uses your installed SDK instead.

## Verify Asterisk

```bash
docker compose ps
docker compose exec asterisk asterisk -rx "core show version"
docker compose exec asterisk asterisk -rx "module show like websocket"
docker compose exec asterisk asterisk -rx "http show status"
docker compose exec asterisk asterisk -rx "ari show apps"
curl -i -u voiceapp http://localhost:8088/ari/asterisk/info
```

Enter the ARI password when curl prompts. Expect HTTP 200 and a loaded `chan_websocket.so`. The incoming media connection uses Asterisk's HTTP server and requires no outbound `websocket_client.conf` entry. ARI and media use separate WebSocket connections.

## Configure Linphone and test a call

Create a manual SIP account:

| Setting | Value |
| --- | --- |
| Username | `1001` |
| Authentication ID | `1001` |
| Password | SIP password from `asterisk/pjsip.conf` |
| Domain / SIP server | Mac's reachable LAN IP address (currently `192.168.1.24`) |
| Registrar URI | `sip:192.168.1.24;transport=udp` (replace IP with your Mac's address) |
| Outbound SIP Proxy URI | Same LAN URI, especially if the existing account identity still uses `127.0.0.1` |
| Transport | UDP |

Explicitly set Authentication ID; leaving it empty caused registration failures during initial testing.

```bash
docker compose exec asterisk asterisk -rx "pjsip show endpoints"
docker compose exec asterisk asterisk -rx "pjsip show contacts"
```

1. Confirm VoiceApp logs `Connected to Asterisk ARI` and Linphone is registered.
2. Dial **500**.
3. Look for `Media ready` and `Sending PCM file` (or `Sending 440 Hz test tone`).
4. In local Menu mode, select an option with the keypad. For the echo test, press **5**, listen for the announcement and beep, then speak for five seconds and hear your recording. Docker/standalone Echo mode starts that sequence immediately. Look for `Recording five seconds`, `Playing back 160000 recorded PCM bytes`, and `Echo sequence complete` in the app logs. Menu mode returns to the main menu; standalone Echo mode keeps the call open afterward.
5. Hang up. Check that the call's channels and bridge disappear:

```bash
docker compose exec asterisk asterisk -rx "core show channels"
docker compose exec asterisk asterisk -rx "bridge show all"
```

An accepted API request or logged send does not prove sound reached the phone; listening to a SIP call is the final audio-delivery check.

Verified on 2026-11-03 with local VoiceApp and Docker Asterisk: calling `sip:500@192.168.1.24` in Linphone played the announcement and beep, recorded five seconds, and played the caller audio back. RTP flowed in both directions after replacing the loopback SIP destination.

## macOS / Docker Desktop RTP networking

Use the Mac's reachable LAN address in Linphone. **Do not use `localhost` or `127.0.0.1` as the SIP server, registrar, or outbound proxy when Asterisk runs in Docker—even when VoiceApp runs locally.** Docker forwards SIP signaling, so registration and answering can succeed while media is completely silent.

This failure recurred during the WebSocket speech test: VoiceApp logged `Media ready` and began sending a valid, non-silent PCM file, but Linphone's SDP contained `c=IN IP4 127.0.0.1`. Asterisk then logged `Sent RTP packet to 127.0.0.1:63286`. That loopback address belongs to the Asterisk container, not the Mac. Successful ARI/WebSocket setup does not rule out this problem.

In Linphone, open **Account list → Account settings → Account settings** and set **Registrar URI** and **Outbound SIP Proxy URI** to `sip:192.168.1.24;transport=udp`, using your current Mac LAN address, then save. Setting the outbound proxy also routes calls correctly when an older account identity still contains `1001@127.0.0.1`. For a new account, use the LAN address as its domain from the start. For the verification call, type **`sip:500@192.168.1.24`** explicitly instead of selecting an old call-history entry, which may retain `500@127.0.0.1`. Redial after making the change; an existing call keeps its negotiated addresses.

Keep VoiceApp's local ARI URL as `http://localhost:8088/ari/`: that is a separate HTTP connection, and localhost is correct there.

Check your addresses:

```bash
ipconfig getifaddr en0
docker network inspect voipdemo_voip --format '{{range .IPAM.Config}}{{.Subnet}}{{end}}'
```

The `voip` network is pinned to **`172.19.0.0/16`** using Compose IPAM, and Asterisk's `local_net` matches it. Docker will preserve this subnet when recreating the stack instead of choosing another one. If it overlaps a VPN or another network on your machine, choose an unused subnet and update both `compose.yaml` and `asterisk/pjsip.conf` together.

The Mac's LAN address is independent of Docker IPAM. **Reserve `192.168.1.24` for this Mac in your router's DHCP settings** so Linphone's registrar/proxy and Asterisk's advertised addresses remain valid. A DHCP reservation is not created by this repository. If a reservation is unavailable, you must update those settings whenever the Mac's LAN IP changes. Avoid assigning a manual static IP without coordinating it with the router's DHCP range.

Adjust `local_net`, `external_signaling_address`, and `external_media_address` in the PJSIP transport to match your Docker subnet and Mac LAN address. The checked-in addresses are development values, not portable defaults. Linphone must also advertise a media address reachable from Asterisk; temporarily disabling STUN/ICE can help diagnose a local setup.

Use `docker compose exec asterisk asterisk -rvvv`, then `pjsip set logger on` and `rtp set debug on` to inspect SDP and RTP destinations. On the next call, verify that the SDP `c=IN IP4` addresses and `Sent RTP packet to` destination are reachable from the other side, rather than loopback or an unreachable container address.

Disable those logs afterward with `pjsip set logger off` and `rtp set debug off`. Restart Asterisk after changing transport settings.

## Development and checks

```bash
dotnet build src/VoiceApp/VoiceApp.csproj
dotnet run --project tests/VoiceApp.IntegrationTests
# Also test the real macOS say/FFmpeg pipeline:
dotnet run --project tests/VoiceApp.IntegrationTests -- --speech
```

The dependency-free integration executable starts a simulated ARI/media server and checks tone delivery, PCM speech bytes and final-frame padding, fragmented plain-text/JSON `MEDIA_START`, XOFF/XON flow control, duplicate/media event filtering, hangup during setup, setup failure, host shutdown, media-leg departure, and beep/record/playback with byte-for-byte caller audio verification. Additional checks cover all menu scenarios, ARI DTMF routing, invalid-input retries, ten-digit validation, leading zeroes, missing terminators, timeouts, and cancellation. It does not replace a real SIP audio test.

After C# changes in Docker mode, run `docker compose up -d --build voiceapp` (retain `VOICE_AUDIO_FILE` if using speech). Local mode only needs an app restart or `dotnet watch`. After changing mounted Asterisk configuration, reload the affected module or run `docker compose restart asterisk`. Volume mapping changes require container recreation.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| ARI HTTP 401 | Match the application password and active `ari.conf`; local mode needs `Asterisk__Password` |
| Local app cannot resolve `asterisk` | Use the `http` launch profile or set `Asterisk__BaseUrl=http://localhost:8088/ari/` |
| External-media creation fails | Verify Asterisk version and loaded `chan_websocket.so` |
| No `MEDIA_START` | Inspect `/media/` reachability and Asterisk logs; connection setup has a timeout |
| Audio file not found | Use an absolute host path locally; `/app/media/speech.pcm` inside Docker |
| Distorted speech | Use the helper's raw PCM output, not WAV/AIFF or 8 kHz audio |
| Media ready but silence on phone | Inspect RTP destination, SDP, and Linphone registration |
| Duplicate call handling | Ensure only one VoiceApp instance is running |

This is a local development proof of concept. ARI is bound to host loopback in Compose; do not expose this test configuration publicly. Future work includes speech recognition/AI, silence-based recording termination, and interruption handling.
