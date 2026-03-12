using Dapper;
using Microsoft.Extensions.Logging;
using SbbBot.Helpers;
using SbbBot.Models;

namespace SbbBot.Repositories;

public class AnnouncementRepository
{
    private readonly IDbConnectionFactory _dbFactory;
    private readonly ILogger<AnnouncementRepository> _logger;

    public AnnouncementRepository(IDbConnectionFactory dbFactory, ILogger<AnnouncementRepository> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<bool> ExistsAsync(string announcementId)
    {
        try
        {
            using var conn = _dbFactory.CreateConnection();
            var sql = "SELECT 1 FROM announcements WHERE announcement_id = @Id";
            var result = await conn.QuerySingleOrDefaultAsync<int?>(sql, new { Id = announcementId });
            return result.HasValue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExistsAsync error for {AnnouncementId}", announcementId);
            throw;
        }
    }

    public async Task InsertAsync(Announcement announcement)
    {
        try
        {
            using var conn = _dbFactory.CreateConnection();
            var sql = @"
                INSERT INTO announcements (announcement_id, title, content, start_date, end_date, content_hash, raw_json, created_at)
                VALUES (@AnnouncementId, @Title, @Content, @StartDate, @EndDate, @ContentHash, @RawJson, @CreatedAt)
                ON CONFLICT (announcement_id) DO UPDATE SET 
                    title = @Title, 
                    content = @Content, 
                    start_date = @StartDate, 
                    end_date = @EndDate, 
                    content_hash = @ContentHash,
                    raw_json = @RawJson";
            await conn.ExecuteAsync(sql, announcement);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InsertAsync error for {AnnouncementId}", announcement?.AnnouncementId);
            throw;
        }
    }

    public async Task<List<Announcement>> GetAllAsync()
    {
        try
        {
            using var conn = _dbFactory.CreateConnection();
            var sql = @"
                SELECT announcement_id as AnnouncementId, title as Title, content as Content, 
                       start_date as StartDate, end_date as EndDate, content_hash as ContentHash, 
                       raw_json as RawJson, created_at as CreatedAt 
                FROM announcements";
            var result = await conn.QueryAsync<Announcement>(sql);
            return result.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetAllAsync error in AnnouncementRepository");
            throw;
        }
    }

    public async Task<bool> IsSeededAsync()
    {
        try
        {
            using var conn = _dbFactory.CreateConnection();
            var sql = "SELECT 1 FROM system_state WHERE key = 'announcements_seeded'";
            var result = await conn.QuerySingleOrDefaultAsync<int?>(sql);
            return result.HasValue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IsSeededAsync error in AnnouncementRepository");
            throw;
        }
    }

    public async Task MarkAsSeededAsync()
    {
        try
        {
            using var conn = _dbFactory.CreateConnection();
            var sql = @"
                INSERT INTO system_state (key, value, updated_at) 
                VALUES ('announcements_seeded', 'true', NOW())
                ON CONFLICT (key) DO UPDATE SET value = 'true', updated_at = NOW()";
            await conn.ExecuteAsync(sql);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MarkAsSeededAsync error in AnnouncementRepository");
            throw;
        }
    }
}
