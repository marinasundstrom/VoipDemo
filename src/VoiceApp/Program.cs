using Voip.Telephony;
using VoiceApp.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAsteriskTelephony<DemoCallFlow>();
builder.Services.AddSingleton<ISpeechSynthesizer, MacSpeechSynthesizer>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
