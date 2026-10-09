using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orapmshms.Services;

namespace Orapmshms.Services.LegacyApi;

/// <summary>
/// Compatibility bridge for imported Web API helpers.
/// Resolves dependencies from the active ASP.NET Core request (when present),
/// so no application startup / Program.cs changes are required.
/// </summary>
internal static class LegacyApiRuntime
{
    // Keep existing initialization behavior for deployments that already call it.
    private static IServiceProvider? _services;
    public static void Initialize(IServiceProvider services) =>
        _services = services ?? throw new ArgumentNullException(nameof(services));

    // IHttpContextAccessor uses a process-wide AsyncLocal context. The original
    // MVC application already registers AddHttpContextAccessor() in Program.cs.
    // This accessor can therefore read the current request without modifying startup.
    private static readonly IHttpContextAccessor RequestContextAccessor = new HttpContextAccessor();

    public static HttpContext? Current => RequestContextAccessor.HttpContext;

    // Prefer request-scoped services; otherwise use the optional app root initialized
    // by existing deployments. This avoids resolving scoped services from the root.
    public static IServiceProvider Services =>
        Current?.RequestServices
        ?? _services
        ?? throw new InvalidOperationException(
            "Legacy API services are unavailable outside an HTTP request. " +
            "Initialize the runtime explicitly for non-HTTP/background code.");

    // Configuration is also needed in controller field initializers and certain
    // background helpers, before a controller action can run.
    public static IConfiguration Configuration =>
        Current?.RequestServices.GetService<IConfiguration>()
        ?? _services?.GetService<IConfiguration>()
        ?? StandaloneConfiguration.Value;

    private static readonly Lazy<IConfiguration> StandaloneConfiguration =
        new Lazy<IConfiguration>(LoadConfiguration);

    private static IConfiguration LoadConfiguration()
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Production";

        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
    }

    // Share the existing MVC SQL Server database configuration across all
    // imported API controllers and services. Do not create extra DB configs.
    private const string MvcConnectionName = "con";

    public static string ConnectionString =>
        Configuration.GetConnectionString(MvcConnectionName) is string value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException(
                "ConnectionStrings:con is required. Configure the existing MVC database connection string.");

    // Preserve the public compatibility aliases used throughout the migrated API.
    // All of them read the same existing MVC ConnectionStrings:con value.
    public static string BookingConnectionString => ConnectionString;
    public static string StripePayConnectionString => ConnectionString;
    public static string NoShowConnectionString => ConnectionString;

    public static DateTime HotelNow(string? hotelId) =>
        Services.GetRequiredService<IHotelClock>().GetHotelNow(hotelId);

    public static DateTime HotelToday(string? hotelId) =>
        Services.GetRequiredService<IHotelClock>().GetHotelToday(hotelId);
}
