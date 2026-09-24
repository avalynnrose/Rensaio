using Rensaio.Resilience;
using RensaioOAuthProxy.Services;

namespace RensaioOAuthProxy;

public static class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddControllers();

        // Register services
        builder.Services.AddSingleton<TokenStoreService>();
        builder.Services.AddScoped<ProviderApiService>();
        builder.Services.AddHttpClient();

        // Provider token calls are subject to the same rate limits and transient
        // outages as the backend's scrobbler/metadata calls, so they share the
        // identical resilience pipeline (exponential retry + per-host circuit
        // breaker) via the linked shared source.
        builder.Services.AddProviderResilience();
        builder.Services.AddHttpClient(ProviderApiService.HttpClientName).AddProviderResilience();

        var app = builder.Build();

        app.UseHttpsRedirection();
        app.MapControllers();

        app.Run();
    }
}
