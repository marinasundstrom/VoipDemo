using System.Net.Http.Headers;
using System.Text;
using VoiceApp.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient<AsteriskClient>((sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();

    client.BaseAddress = new Uri(
        config["Asterisk:BaseUrl"]!);

    var credentials = Convert.ToBase64String(
        Encoding.UTF8.GetBytes(
            $"{config["Asterisk:Username"]}:{config["Asterisk:Password"]}"));

    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Basic", credentials);
});

builder.Services.AddHostedService<AsteriskEventService>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var password = builder.Configuration["Asterisk:Password"];

Console.WriteLine(
    $"ARI password configured: {!string.IsNullOrEmpty(password)}");

Console.WriteLine(
    $"ARI password length: {password?.Length ?? 0}");

app.Run();