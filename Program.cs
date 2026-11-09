using MobySync.Extensions;
using MobySync.Filters;
using MobySync.Helpers;
using MobySync.Interfaces;
using MobySync.Models;
using MobySync.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings-example.json", optional: true, reloadOnChange: false);
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();

// Register Services
builder.Services.AddSingleton<DockerHelper>();
builder.Services.AddSingleton<UpdateCoordinator>();
builder.Services.AddSingleton<CredentialsHelper>();
builder.Services.AddSingleton<RegistryHelper>();
builder.Services.AddSingleton<INotificationHelper, DiscordNotificationHelper>();
builder.Services.AddSingleton<INotificationHelper, GenericWebhookNotificationHelper>();

var selfUpdateTargetId = Environment.GetEnvironmentVariable("MOBYSYNC_SELF_UPDATE_TARGET");

if (string.IsNullOrWhiteSpace(selfUpdateTargetId))
{
    builder.Services.AddHostedService<UpdateService>();
}

builder.Services.AddHttpClient();

builder.Logging.ClearProviders();
builder.Logging.AddSystemdConsole(options =>
{
    options.IncludeScopes = false;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss";
});

var app = builder.Build();

if (!string.IsNullOrWhiteSpace(selfUpdateTargetId))
{
    var targetImage = Environment.GetEnvironmentVariable("MOBYSYNC_SELF_TARGET_IMAGE");
    var coordinator = app.Services.GetRequiredService<UpdateCoordinator>();
    await coordinator.ExecuteSelfUpdate(selfUpdateTargetId, targetImage);
    return;
}

if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("API_KEY")))
{
    app.Logger.LogCritical("API_KEY environment variable is not set, exiting...");
    
    Environment.Exit(1);
}

var apiSection = app.Configuration.GetSection(nameof(ApiSection)).Get<ApiSection>();

if (apiSection is null || string.IsNullOrEmpty(apiSection.Ip) || string.IsNullOrEmpty(apiSection.Port) || string.IsNullOrEmpty(apiSection.Scheme))
{
    app.Logger.LogCritical("ApiSection is missing or corrupted in appsettings.json, exiting...");
    
    Environment.Exit(1);
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapGroup("/api")
   .AddEndpointFilter<ApiKeyFilter>()
   .MapUpdaterEndpoints();

app.Run($"{apiSection.Scheme}://{apiSection.Ip}:{apiSection.Port}");
