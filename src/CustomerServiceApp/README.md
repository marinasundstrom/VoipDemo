# Customer-service template

A separate runnable app demonstrating a small customer-service workflow on the shared [Voip.Telephony](../Voip.Telephony/README.md) foundation. VoiceApp remains the media playground on extension **500**; this app uses **600** and the independent ARI application `customerservice`. Both apps can run against the same Asterisk instance.

## Caller journey

1. Check opening hours. If closed, announce the hours and hang up.
2. Greet the caller and ask what they need: **1** orders, **2** billing, **3** technical support, **0** finish.
3. Offer **1** to speak to customer service or **0** to finish.
4. Enter the queue and play looping waiting music.
5. Reserve a destination automatically, or wait for an agent to explicitly accept the call.
6. Announce the selected demo extension, play a placeholder adviser response, and hang up.

This does **not** dial the destination numbers yet. `2001` and `2002` are simulated agents. A reservation lasts until the placeholder finishes, the call fails, or the caller hangs up. The shared ARI host closes the channel when the flow returns.

## Run locally

Requirements: .NET 11, macOS `say`, FFmpeg, and Docker Asterisk. From the repository root:

```bash
python3 scripts/create-demo-music.py
docker compose up -d asterisk
docker compose exec asterisk asterisk -rx 'dialplan reload'
export Asterisk__Password='the-password-from-asterisk/ari.conf'
dotnet run --project src/CustomerServiceApp --launch-profile http
```

Dial **`sip:600@192.168.1.24`** in Linphone (use your Mac's actual LAN IP). Do not use a loopback SIP destination: see the root README's audio-networking notes. The HTTP app listens on `http://localhost:5146`. VoiceApp can run concurrently on port 5144.

The shared library is provider-neutral, but this app currently registers `MacSpeechSynthesizer`, so run it locally on macOS. Use an absolute `CustomerService__MusicFile` path if launching outside the usual project working directory. The default is `../../media/music.pcm`, raw 16 kHz mono s16le PCM.

## Automatic and manual acceptance

`CustomerService:DispatchMode` defaults to `Automatic`: FIFO callers reserve the first idle destination in configured order. A five-second minimum music interval makes the waiting stage audible even when someone is immediately available. Additional callers wait until a destination is released.

For a future agent UI, select manual acceptance at startup:

```bash
export CustomerService__DispatchMode=Manual
dotnet run --project src/CustomerServiceApp --launch-profile http
```

In manual mode, callers remain queued even when an agent is idle. Inspect the queue and choose a call to accept:

```bash
curl http://localhost:5146/queue
curl http://localhost:5146/agents
curl -X POST http://localhost:5146/agents/agent-1/accept/CALL_ID
```

Replace `CALL_ID` with a waiting call's `id`. Queue entries include topic and enqueue time; agent entries include their current call ID. Acceptance is atomic: a busy/unknown agent, a closed service, or a call already claimed/abandoned returns **409**. The endpoint also exists in automatic mode, although idle destinations will normally already have been reserved. Manual selection can accept a specific caller; automatic dispatch is FIFO.

Hangup immediately removes the ticket or releases its agent. After 120 seconds without assignment (configurable), callers hear a busy message and disconnect. Queue membership and agent reservations are in memory, process-local, and disappear on restart. These localhost operator endpoints are demo APIs, not an authenticated production agent console.

## Opening hours and closing the service

Default schedule: **Monday–Friday, 09:00–17:00 Europe/Stockholm**. Timezone conversion follows daylight saving; opening is inclusive and closing is exclusive. Configure `CustomerService:Hours` in `appsettings.json`. The initial schedule is a single daytime interval on selected weekdays; holidays and overnight shifts are future work.

```bash
curl http://localhost:5146/service
curl -X PUT http://localhost:5146/service/Closed
curl -X PUT http://localhost:5146/service/Open
curl -X PUT http://localhost:5146/service/Schedule
```

`Closed` and `Open` override the clock immediately; `Schedule` restores scheduled hours. Runtime overrides last until restart. To set a startup override, use `CustomerService__Override=Open` or `Closed`.

Closing the service rejects new queue entries and informs waiting callers that the service is closed, states the configured opening hours, and hangs up. Already-accepted calls finish normally. A background check applies scheduled changes within about one second; incoming calls and acceptance requests also check current availability. The hours spoken in the closed message come from the same configuration used for scheduling.

## Where to extend

- **CustomerServiceFlow:** greeting, offer of customer service, waiting, placeholder response.
- **IHelpTopicSelector:** keypad classification today; speech recognition/AI intent selection later.
- **CallQueueManager:** waiting tickets and atomic destination reservations; no SIP or speech code.
- **ServiceAvailability:** schedule and manual service overrides; no call-control code.
- **ICallFlow / ICallMedia / ISpeechSynthesizer:** shared extension points; no queue policy in the foundation.

For real agents, replace the placeholder response with agent origination/bridging and release the reservation on agent hangup or failed routing. Do not treat configured numbers as real presence signals: this demo's availability is its own reservation state. An agent-facing UI can use the existing queue/agent/accept endpoints without changing how callers are queued.

## Verify

```bash
dotnet run --project tests/CustomerServiceApp.Tests
dotnet run --project tests/VoiceApp.IntegrationTests -- --speech
```

Tests cover FIFO/capacity, concurrent acceptance, cancelled callers, idempotent releases, both dispatch modes, music failure, queue timeout, opening-hour boundaries/timezone changes, closure of waiting callers, and reopening. The original media/DTMF suite also runs with a separate ARI app name to verify application isolation.

For a phone test: force Open, call 600, choose a topic and customer service, then accept the call in manual mode. Confirm music stops, the placeholder plays, and the call ends. Queue another call and force Closed to check the waiting-caller message, then call once more to check the closed-at-entry message. Restore Schedule afterward if desired.
