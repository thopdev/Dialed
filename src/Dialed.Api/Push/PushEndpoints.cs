using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Dialed.Api.Push;

public sealed record PushKeyDto(string PublicKey);

public sealed record PushSubscribeRequest(string Endpoint, string P256dh, string Auth, string? Language);

public sealed record PushUnsubscribeRequest(string Endpoint);

public sealed record PushSettingsDto(bool Follows, bool Household, int Devices, bool LowStock = false, bool Peak = false, bool Service = false);

public sealed record PushSettingsRequest(bool Follows, bool Household, bool LowStock = false, bool Peak = false, Dialed.Domain.Inventory.InventorySettings? Inventory = null, string? TimeZone = null, bool Service = false);

public static class PushEndpoints
{
    private const int MaxEndpoint = 2048;
    private const int MaxKey = 256;
    private const int MaxDevices = 10;

    public static IServiceCollection AddDialedPush(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<PushOptions>(configuration.GetSection(PushOptions.Section));
        services.PostConfigure<PushOptions>(options =>
        {
            // Development works out of the box: a key pair is made when the app starts (subscriptions from an earlier run stop working).
            if (!options.IsConfigured && environment.IsDevelopment())
            {
                var keys = WebPush.VapidHelper.GenerateVapidKeys();
                options.PublicKey = keys.PublicKey;
                options.PrivateKey = keys.PrivateKey;
            }
        });
        // With Push:StorePath the devices, switches and stock alert memory survive a restart (development and one instance); without it they are in memory.
        services.AddSingleton<IPushStore>(sp => configuration["Push:StorePath"] is { Length: > 0 } path
            ? new FilePushStore(path, sp.GetRequiredService<ILogger<FilePushStore>>())
            : new InMemoryPushStore());
        services.AddSingleton<IPushSender>(sp => sp.GetRequiredService<IOptions<PushOptions>>().Value.IsConfigured
            ? ActivatorUtilities.CreateInstance<WebPushSender>(sp)
            : new NoPushSender());
        services.AddSingleton<StockAlertService>();
        services.AddHostedService<StockAlertWorker>();
        services.AddSingleton<PushNotifier>();
        services.AddSingleton<INotifier>(sp => sp.GetRequiredService<PushNotifier>());
        return services;
    }

    public static IEndpointRouteBuilder MapDialedPush(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/push").RequireAuthorization();

        // The public VAPID key the browser needs to subscribe. 503 when the server has no keys: the app then says push is not available.
        group.MapGet("/key", (IOptions<PushOptions> options) =>
            options.Value.IsConfigured ? Results.Ok(new PushKeyDto(options.Value.PublicKey!)) : Results.Problem("Push notifications are not set up on this server.", statusCode: StatusCodes.Status503ServiceUnavailable));

        group.MapPut("/subscription", async (PushSubscribeRequest request, ClaimsPrincipal user, IPushStore store, TimeProvider time, CancellationToken ct) =>
        {
            if (!IsValid(request))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["subscription"] = ["That is not a valid push subscription."] });
            }

            var userId = UserId(user);
            var mine = await store.ListAsync(userId, ct);
            if (mine.Count >= MaxDevices && mine.All(s => s.Endpoint != request.Endpoint))
            {
                return Results.Conflict(new ProblemDetails { Title = "Too many devices", Detail = "Turn notifications off on a device you no longer use first." });
            }

            await store.SaveAsync(new PushSubscription(userId, request.Endpoint, request.P256dh, request.Auth, Language(request.Language), time.GetUtcNow()), ct);
            return Results.NoContent();
        });

        // Only the person's own device can be removed: someone else's endpoint answers the same as an unknown one.
        group.MapPost("/unsubscribe", async (PushUnsubscribeRequest request, ClaimsPrincipal user, IPushStore store, CancellationToken ct) =>
        {
            await store.RemoveAsync(UserId(user), request.Endpoint ?? "", ct);
            return Results.NoContent();
        });

        group.MapGet("/settings", async (ClaimsPrincipal user, IPushStore store, CancellationToken ct) =>
        {
            var userId = UserId(user);
            var settings = await store.GetSettingsAsync(userId, ct);
            return Results.Ok(new PushSettingsDto(settings.Follows, settings.Household, (await store.ListAsync(userId, ct)).Count, settings.LowStock, settings.Peak, settings.Service));
        });

        group.MapPut("/settings", async (PushSettingsRequest request, ClaimsPrincipal user, IPushStore store, CancellationToken ct) =>
        {
            var userId = UserId(user);
            if (!ValidInventory(request.Inventory))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["inventory"] = ["Those inventory settings are not valid."] });
            }

            // Only a time zone this server knows is kept; the stock alerts fall back to UTC without one.
            var zone = request.TimeZone is { Length: > 0 and <= 64 } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out _) ? id : null;
            await store.SaveSettingsAsync(userId, new PushSettings(request.Follows, request.Household, request.LowStock, request.Peak, request.Inventory, zone, request.Service), ct);
            return Results.Ok(new PushSettingsDto(request.Follows, request.Household, (await store.ListAsync(userId, ct)).Count, request.LowStock, request.Peak, request.Service));
        });

        return endpoints;
    }

    private static bool ValidInventory(Dialed.Domain.Inventory.InventorySettings? inventory) =>
        inventory is null
        || (inventory.LowStockG is >= 0m and <= 100_000m
            && inventory.RestWindows is { } windows
            && new[] { windows.Light, windows.Medium, windows.Dark }.All(w => w is { RestDays: >= 0 and <= 365, PeakEndDays: >= 0 and <= 1000 }));

    private static bool IsValid(PushSubscribeRequest request) =>
        request is not null
        && request.Endpoint is { Length: > 0 and <= MaxEndpoint } endpoint
        && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && IsKey(request.P256dh) && IsKey(request.Auth);

    private static bool IsKey(string? value) =>
        value is { Length: > 0 and <= MaxKey } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '=');

    private static string Language(string? language) => string.Equals(language, "nl", StringComparison.OrdinalIgnoreCase) ? "nl" : "en";

    private static string UserId(ClaimsPrincipal user) => user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "";
}
