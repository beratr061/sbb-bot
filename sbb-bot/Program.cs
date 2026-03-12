using SbbBot;
using SbbBot.Helpers;
using SbbBot.Models;
using SbbBot.Services;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

// Configuration
builder.Services.Configure<BotConfig>(builder.Configuration);
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("Database"));

// Serilog Configuration
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Logging.AddSerilog();

// Services & Helpers
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();
builder.Services.AddTransient<DatabaseInitializer>();
builder.Services.AddSingleton<Telegram.Bot.ITelegramBotClient>(sp => {
    var config = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BotConfig>>().Value;
    return new Telegram.Bot.TelegramBotClient(config.Telegram.Token);
});
builder.Services.AddSingleton<TelegramHelper>();
builder.Services.Configure<SbbBot.Models.DiscordOptions>(builder.Configuration.GetSection("Discord"));
builder.Services.AddSingleton<IDiscordHelper, DiscordHelper>();

// Repositories
builder.Services.AddSingleton<SbbBot.Repositories.BusLineRepository>();
builder.Services.AddSingleton<SbbBot.Repositories.RouteRepository>();
builder.Services.AddSingleton<SbbBot.Repositories.FareRepository>();
builder.Services.AddSingleton<SbbBot.Repositories.AnnouncementRepository>();
builder.Services.AddSingleton<SbbBot.Repositories.HashRepository>();

// Worker Services
builder.Services.AddSingleton<InteractionManager>();
builder.Services.AddSingleton<VehicleService>();
builder.Services.AddHostedService<TelegramListenerService>();
builder.Services.AddHostedService<NewsWatcherService>();
builder.Services.AddHostedService<DocumentWatcherService>();
builder.Services.AddHostedService<BusLineWatcherService>();
builder.Services.AddHostedService<MeetingWatcherService>();
builder.Services.AddHostedService<UkomeWatcherService>();
builder.Services.AddHostedService<AnnouncementWatcherService>();
builder.Services.AddHostedService<FareWatcherService>();
builder.Services.AddHostedService<RouteWatcherService>();
builder.Services.AddHostedService<OpenDataWatcherService>();
builder.Services.AddHostedService<NeonPingService>();

try
{
    var discordOptions = builder.Configuration.GetSection("Discord").Get<DiscordOptions>();
    foreach (var bot in discordOptions?.Bots ?? [])
    {
        if (string.IsNullOrWhiteSpace(bot.Token))
            Log.Warning("[Discord][{Name}] Token eksik, bu bot başlatılmayacak", bot.Name);

        foreach (var ch in bot.Channels ?? [])
            if (ch.ChannelId == 0)
                Log.Warning("[Discord][{Bot}][{Channel}] ChannelId eksik", bot.Name, ch.Name);
    }
    
    var host = builder.Build();
    
    // Initialize Database Connections
    StorageHelper.Initialize(host.Services.GetRequiredService<IDbConnectionFactory>());
    
    // Initialize Database Tables and run Migrations
    var dbInitializer = host.Services.GetRequiredService<DatabaseInitializer>();
    await dbInitializer.InitializeAsync();

    // Ensure helper static constructor runs or check data dir here
    // StorageHelper static constructor handles it, but good to be explicit if logic was complex.

    // Discord Lifecycle Hooks
    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
    var discordHelper = host.Services.GetRequiredService<IDiscordHelper>();

    lifetime.ApplicationStarted.Register(() =>
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await discordHelper.StartAsync();
                Log.Information("Discord bots started successfully.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to start Discord bots.");
            }
        });

        // Fire-and-forget health check after 30 seconds
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            try
            {
                await discordHelper.CheckHealthAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Discord health check failed.");
            }
        });
    });

    lifetime.ApplicationStopping.Register(() =>
    {
        try
        {
            discordHelper.StopAsync().GetAwaiter().GetResult();
            Log.Information("Discord bots stopped.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error stopping Discord bots.");
        }
    });

    Log.Information("Starting SBB Bot Background Services...");
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
