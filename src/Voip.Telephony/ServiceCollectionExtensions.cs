using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace Voip.Telephony;

public static class ServiceCollectionExtensions
{
    // Each host supplies its own ICallFlow and Asterisk:Application name.
    // Business policy and speech-provider selection remain in the host app.
    public static IServiceCollection AddAsteriskTelephony<TFlow>(this IServiceCollection services)
        where TFlow : class, ICallFlow
    {
        services.AddHttpClient<AsteriskClient>((provider, client) =>
        {
            var config = provider.GetRequiredService<IConfiguration>();
            client.BaseAddress = new Uri(config["Asterisk:BaseUrl"]
                ?? throw new InvalidOperationException("Missing Asterisk:BaseUrl."));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config["Asterisk:Username"]}:{config["Asterisk:Password"]}")));
        });
        services.AddSingleton<ICallFlow, TFlow>();
        services.AddHostedService<AsteriskEventService>();
        return services;
    }
}
