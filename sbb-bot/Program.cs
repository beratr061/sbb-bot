using SbbBot;
using SbbBot.Helpers;
using SbbBot.Services;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

// Configuration
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
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
builder.Services.AddSingleton<TelegramHelper>();

// Worker Services
builder.Services.AddHostedService<NewsWatcherService>();
builder.Services.AddHostedService<DocumentWatcherService>();
builder.Services.AddHostedService<BusLineWatcherService>();
builder.Services.AddHostedService<MeetingWatcherService>();
builder.Services.AddHostedService<UkomeWatcherService>();

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
