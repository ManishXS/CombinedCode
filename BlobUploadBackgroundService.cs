using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using StackExchange.Redis;
using System.Text.Json;

namespace BackEnd.Services
{
    public class BlobUploadBackgroundService : BackgroundService
    {
        private readonly ILogger<BlobUploadBackgroundService> _logger;
        private readonly IConnectionMultiplexer _redis;
        private readonly BlobServiceClient _blobServiceClient;
        private readonly string _cdnBaseUrl = "https://tenxcdn-dtg6a0dtb9aqg3bb.z02.azurefd.net/media/";

        public BlobUploadBackgroundService(
            ILogger<BlobUploadBackgroundService> logger,
            IConnectionMultiplexer redis,
            BlobServiceClient blobServiceClient)
        {
            _logger = logger;
            _redis = redis;
            _blobServiceClient = blobServiceClient;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("BlobUploadBackgroundService is starting.");

            var db = _redis.GetDatabase();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Fetch the next item from the queue
                    var serializedTask = await db.ListRightPopAsync("uploadQueue");
                    if (serializedTask.IsNullOrEmpty)
                    {
                        await Task.Delay(1000, stoppingToken); // Wait before checking again
                        continue;
                    }

                    // Deserialize task details
                    var taskData = JsonSerializer.Deserialize<UploadTask>(serializedTask!); // Assuming RedisValue can be safely converted to string
                    if (taskData == null)
                    {
                        _logger.LogWarning("Received null task data. Skipping...");
                        continue;
                    }

                    _logger.LogInformation("Processing upload task: {UploadId}", taskData.UploadId);

                    // Get the block IDs from Redis
                    var redisChunksKey = $"{taskData.UploadId}:chunks";
                    var redisChunks = await db.SetMembersAsync(redisChunksKey);
                    if (redisChunks == null || redisChunks.Length != taskData.TotalChunks)
                    {
                        _logger.LogWarning("Incomplete chunks for UploadId: {UploadId}", taskData.UploadId);
                        continue;
                    }

                    var blockIds = redisChunks.Select(chunk => chunk.ToString())
                                              .OrderBy(id => id)
                                              .ToList();

                    // Get BlobContainerClient and BlobClient
                    var containerClient = _blobServiceClient.GetBlobContainerClient("media");
                    var blobClient = containerClient.GetBlockBlobClient(taskData.BlobName);

                    // Commit blocks
                    await blobClient.CommitBlockListAsync(blockIds);
                    _logger.LogInformation("Successfully finalized blob upload for {BlobName}", taskData.BlobName);

                    // Save metadata to the database (if applicable)
                    // SaveMetadataToDatabase(taskData);

                    // Cleanup Redis keys
                    await db.KeyDeleteAsync(redisChunksKey);

                    _logger.LogInformation("Cleaned up Redis keys for {UploadId}", taskData.UploadId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred while processing upload tasks.");
                }
            }

            _logger.LogInformation("BlobUploadBackgroundService is stopping.");
        }

        private class UploadTask
        {
            public string UploadId { get; set; } = string.Empty;
            public string BlobName { get; set; } = string.Empty;
            public int TotalChunks { get; set; }
        }
    }
}
