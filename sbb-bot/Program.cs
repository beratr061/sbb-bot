using SbbBot;
using SbbBot.Helpers;
using SbbBot.Services;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

// Configuration
builder.Services.Configure<BotConfig>(builder.Configuration);

// Serilog Configuration
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Logging.AddSerilog();

// Services & Helpers
builder.Services.AddHttpClient();
builder.Services.AddSingleton<Telegram.Bot.ITelegramBotClient>(sp => {
    var config = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BotConfig>>().Value;
    return new Telegram.Bot.TelegramBotClient(config.Telegram.Token);
});
builder.Services.AddSingleton<TelegramHelper>();

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

try
{
    var host = builder.Build();
    
    // Ensure helper static constructor runs or check data dir here
    // StorageHelper static constructor handles it, but good to be explicit if logic was complex.

    Log.Information("Starting SBB Bot Background Services...");
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
