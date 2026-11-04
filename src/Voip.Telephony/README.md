# Shared Asterisk foundation

`Voip.Telephony` contains transport and call-lifecycle code shared by two deliberately small apps:

| Host | Use case | ARI app / extension |
| --- | --- | --- |
| VoiceApp | Media experiments: music, TTS, DTMF, record/playback | `voiceapp` / 500 |
| CustomerServiceApp | Opening hours, help topics, customer queue, agent acceptance | `customerservice` / 600 |

The library owns the ARI event connection, answering, bridge/external-media setup, media readiness, DTMF delivery, cancellation and resource cleanup. A per-call `ICallFlow` runs outside the ARI receive loop. Returning from the flow ends the call; the cancellation token signals hangup or host shutdown. Flow implementations are registered as singletons, so keep per-call state in local variables and make shared services thread-safe.

The foundation does not know about menus, opening hours, agents, or queues. `ICallMedia` sends raw 16 kHz mono s16le audio and can record incoming PCM. `CallInput` provides DTMF input. `ISpeechSynthesizer` abstracts text-to-PCM; the included macOS provider is optional and chosen by each host.

## Add another exploration app

Create an ASP.NET Core host that references this project and implement `ICallFlow`:

```csharp
using Voip.Telephony;

public sealed class GreetingFlow(ISpeechSynthesizer speech) : ICallFlow
{
    public async Task RunAsync(ICallMedia media, CallInput input, CancellationToken token)
    {
        var pcm = await speech.SynthesizeAsync("Hello from another sample.", token);
        await media.SendAudioAsync(pcm, token);
        await Task.Delay(150, token); // Allow the final paced frame to drain before hangup.
    }
}
```

Register the foundation and the desired speech provider in `Program.cs`:

```csharp
builder.Services.AddAsteriskTelephony<GreetingFlow>();
builder.Services.AddSingleton<ISpeechSynthesizer, MacSpeechSynthesizer>();
```

Configure `Asterisk:BaseUrl`, `Username`, `Password`, and a **unique** `Application` name. Add a dialplan extension with `Stasis(your-application)` followed by `Hangup()`, reload the dialplan, and give the host its own HTTP port. Credentials identify the ARI user; the application name identifies the event subscription. Reuse credentials if appropriate, but do not run two independent hosts for the same ARI application.

Use fake `ICallMedia`/`ISpeechSynthesizer` implementations for fast business-flow tests. The existing integration executable provides a simulated HTTP/WebSocket Asterisk server for transport/lifecycle tests. Always finish with a real SIP call: successful ARI operations alone cannot validate RTP routing to a phone.

This is a sample foundation, not a production telephony framework. Incoming media uses a bounded buffer, outgoing audio is paced, and shared call cleanup is bounded and best-effort. Real agent origination, durable queues, recognition, and richer streaming/call-transfer policies remain explicit extensions.
