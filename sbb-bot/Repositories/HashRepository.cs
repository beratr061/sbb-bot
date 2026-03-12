using Dapper;
using Microsoft.Extensions.Logging;
using SbbBot.Helpers;

namespace SbbBot.Repositories;

public class HashRepository
{
    private readonly IDbConnectionFactory _dbFactory;
    private readonly ILogger<HashRepository> _logger;
    private readonly HashSet<string> _allowedTables = new() 
    { 
        "ukome_decisions", 
        "news", 
        "documents", 
        "open_data_sets",
        "meetings"
    };

    public HashRepository(IDbConnectionFactory dbFactory, ILogger<HashRepository> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    private void ValidateTableName(string tableName)
    {
        if (!_allowedTables.Contains(tableName))
        {
            throw new ArgumentException($"Invalid table name: {tableName}");
        }
    }

    public async Task<bool> ExistsAsync(string tableName, string contentHash)
    {
        try
        {
            ValidateTableName(tableName);
            using var conn = _dbFactory.CreateConnection();
            var sql = tableName == "open_data_sets" 
                ? $"SELECT 1 FROM {tableName} WHERE dataset_id = @ContentHash"
                : $"SELECT 1 FROM {tableName} WHERE content_hash = @ContentHash";
            var result = await conn.QuerySingleOrDefaultAsync<int?>(sql, new { ContentHash = contentHash });
            return result.HasValue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExistsAsync error for {TableName} - {ContentHash}", tableName, contentHash);
            throw;
        }
    }

    public async Task InsertAsync(string tableName, string contentHash, string title, string url, DateTime date)
    {
        try
        {
            ValidateTableName(tableName);
            using var conn = _dbFactory.CreateConnection();
            
            // For open_data_sets, dataset_id comes via the contentHash parameter
            if (tableName == "open_data_sets")
            {
                var sqlOpenData = $@"
                    INSERT INTO {tableName} (dataset_id, title, activity_type, timestamp)
                    VALUES (@ContentHash, @Title, 'insert', @Date)
                    ON CONFLICT (dataset_id) DO NOTHING";
                await conn.ExecuteAsync(sqlOpenData, new { ContentHash = contentHash, Title = title, Date = date });
                return;
            }

            // For other tables
            var sql = $@"
                INSERT INTO {tableName} (title, url, content_hash) 
                VALUES (@Title, @Url, @ContentHash) 
                ON CONFLICT (content_hash) DO NOTHING";
                
            await conn.ExecuteAsync(sql, new { Title = title, Url = url, ContentHash = contentHash });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InsertAsync error for {TableName} - {ContentHash}", tableName, contentHash);
            throw;
        }
    }

    public async Task<bool> IsSeededAsync(string tableName)
    {
        try
        {
            ValidateTableName(tableName);
            using var conn = _dbFactory.CreateConnection();
            var sql = "SELECT 1 FROM system_state WHERE key = @Key";
            var result = await conn.QuerySingleOrDefaultAsync<int?>(sql, new { Key = $"{tableName}_seeded" });
            return result.HasValue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IsSeededAsync error for {TableName}", tableName);
            throw;
        }
    }

    public async Task MarkAsSeededAsync(string tableName)
    {
        try
        {
            ValidateTableName(tableName);
            using var conn = _dbFactory.CreateConnection();
            var sql = @"
                INSERT INTO system_state (key, value, updated_at) 
                VALUES (@Key, 'true', NOW())
                ON CONFLICT (key) DO UPDATE SET value = 'true', updated_at = NOW()";
            await conn.ExecuteAsync(sql, new { Key = $"{tableName}_seeded" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MarkAsSeededAsync error for {TableName}", tableName);
            throw;
        }
    }
}
