using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Dapper;
using SbbBot.Helpers;

namespace SbbBot.Services;

public class NeonPingService : BackgroundService
{
    private readonly IDbConnectionFactory _dbFactory;
    private readonly IntervalsConfig _intervals;

    public NeonPingService(IDbConnectionFactory dbFactory, IOptions<BotConfig> options)
    {
        _dbFactory = dbFactory;
        _intervals = options.Value.Intervals;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int intervalMinutes = _intervals.NeonPingMinutes > 0 ? _intervals.NeonPingMinutes : 4;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connection = _dbFactory.CreateConnection();
                await connection.ExecuteScalarAsync<int>("SELECT 1");
                
                Log.Debug("[NeonPing] Bağlantı aktif.");
            }
            catch (Exception ex)
            {
                Log.Warning("[NeonPing] Ping başarısız: {Message}", ex.Message);
            }

            // Bekle ve tekrarla
            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
        }
    }
}
