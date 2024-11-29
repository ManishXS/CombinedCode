using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using StackExchange.Redis;
using System.Text;
using BackEnd.Entities;
using BackEnd.Models;
using BackEnd.ViewModels;

namespace BackEnd.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FeedsController : ControllerBase
    {
        private readonly CosmosDbContext _dbContext;
        private readonly BlobServiceClient _blobServiceClient;
        private readonly string _feedContainer = "media";
        private readonly string _cdnBaseUrl = "https://tenxcdn-dtg6a0dtb9aqg3bb.z02.azurefd.net/media/";
        private readonly IDatabase _redis;
        private readonly ILogger<FeedsController> _logger;

        public FeedsController(CosmosDbContext dbContext, BlobServiceClient blobServiceClient, IConnectionMultiplexer redis, ILogger<FeedsController> logger)
        {
            _dbContext = dbContext;
            _blobServiceClient = blobServiceClient;
            _redis = redis.GetDatabase(); // Corrected Redis initialization
            _logger = logger;
        }

        [HttpPost("uploadFeed")]
        public async Task<IActionResult> UploadFeed([FromForm] FeedUploadModel model)
        {
            try
            {
                // Validate Input
                if (model.File == null || string.IsNullOrEmpty(model.UploadId) || model.ChunkIndex < 0 || model.TotalChunks <= 0 || string.IsNullOrEmpty(model.FileName))
                {
                    return BadRequest("Missing or invalid required fields.");
                }

                if (model.File.Length == 0)
                {
                    return BadRequest("Chunk is empty.");
                }

                // Generate a unique blob name
                var blobName = $"{ShortGuidGenerator.Generate()}_{model.FileName}";
                var uploadKey = $"{model.UploadId}:chunks";

                // Get Blob container and client
                var containerClient = _blobServiceClient.GetBlobContainerClient(_feedContainer);
                if (!await containerClient.ExistsAsync())
                {
                    return NotFound("Blob container does not exist.");
                }

                var blobClient = containerClient.GetBlockBlobClient(blobName);

                // Stage the current chunk
                var blockId = Convert.ToBase64String(Encoding.UTF8.GetBytes(model.ChunkIndex.ToString("D6")));
                using (var stream = model.File.OpenReadStream())
                {
                    await blobClient.StageBlockAsync(blockId, stream);
                    _logger.LogInformation("Staged block: {BlockId}", blockId);
                }

                // Add the chunk to Redis
                await _redis.SetAddAsync(uploadKey, blockId);
                _logger.LogInformation("Added chunk {ChunkIndex} to Redis for UploadId {UploadId}", model.ChunkIndex, model.UploadId);

                // Finalize the blob if all chunks are uploaded
                if (model.ChunkIndex == model.TotalChunks - 1)
                {
                    _logger.LogInformation("Finalizing blob upload for UploadId {UploadId}", model.UploadId);

                    // Retrieve all uploaded chunks from Redis
                    var redisChunks = await _redis.SetMembersAsync(uploadKey);
                    var blockIds = redisChunks.Select(x => x.ToString()).OrderBy(id => id).ToList();

                    // Validate that all chunks are uploaded
                    if (blockIds.Count != model.TotalChunks)
                    {
                        return BadRequest($"Not all chunks have been uploaded. Expected {model.TotalChunks}, got {blockIds.Count}.");
                    }

                    // Commit the block list
                    await blobClient.CommitBlockListAsync(blockIds);
                    _logger.LogInformation("Finalized blob {BlobName} with {TotalChunks} chunks", blobName, model.TotalChunks);

                    // Save metadata to Cosmos DB
                    var userPost = new UserPost
                    {
                        PostId = Guid.NewGuid().ToString(),
                        Title = model.FileName,
                        Content = $"{_cdnBaseUrl}/{blobName}",
                        Caption = model.Caption,
                        AuthorId = model.UserId,
                        AuthorUsername = model.UserName,
                        DateCreated = DateTime.UtcNow
                    };

                    await _dbContext.PostsContainer.UpsertItemAsync(userPost, new PartitionKey(userPost.PostId));
                    _logger.LogInformation("UserPost created for BlobName {BlobName}, PostId {PostId}", blobName, userPost.PostId);

                    // Clean up Redis keys
                    await _redis.KeyDeleteAsync(uploadKey);

                    return Ok(new
                    {
                        Message = "Upload completed successfully.",
                        BlobUrl = userPost.Content,
                        PostId = userPost.PostId
                    });
                }

                // Intermediate response for chunk upload
                return Ok(new { Message = "Chunk uploaded successfully.", ChunkIndex = model.ChunkIndex });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading chunk for UploadId {UploadId}, ChunkIndex {ChunkIndex}", model.UploadId, model.ChunkIndex);
                return StatusCode(500, "Internal server error.");
            }
        }

        [HttpPost("uploadChunk")]
        public async Task<IActionResult> UploadChunk([FromForm] ChunkUploadModel model)
        {
            try
            {
                if (model.File == null || string.IsNullOrEmpty(model.UploadId) || model.ChunkIndex < 0 || model.TotalChunks <= 0)
                {
                    return BadRequest("Invalid input data.");
                }

                var uploadKey = $"{model.UploadId}:chunks";
                var containerClient = _blobServiceClient.GetBlobContainerClient(_feedContainer);
                var blobName = $"{model.UploadId}_{model.FileName}";
                var blobClient = containerClient.GetBlockBlobClient(blobName);

                using (var stream = model.File.OpenReadStream())
                {
                    var blockId = Convert.ToBase64String(Encoding.UTF8.GetBytes(model.ChunkIndex.ToString("D6")));
                    await blobClient.StageBlockAsync(blockId, stream);
                    await _redis.SetAddAsync(uploadKey, blockId);
                }

                if (model.IsLastChunk)
                {
                    var redisChunks = await _redis.SetMembersAsync(uploadKey);
                    var blockIds = redisChunks.Select(x => x.ToString()).OrderBy(id => id).ToList();

                    if (blockIds.Count != model.TotalChunks)
                    {
                        return BadRequest("Not all chunks uploaded.");
                    }

                    await blobClient.CommitBlockListAsync(blockIds);
                    await _redis.KeyDeleteAsync(uploadKey);

                    var userPost = new UserPost
                    {
                        PostId = Guid.NewGuid().ToString(),
                        Title = model.FileName,
                        Content = $"{_cdnBaseUrl}/{blobName}",
                        Caption = model.Caption,
                        AuthorId = model.UserId,
                        AuthorUsername = model.UserName,
                        DateCreated = DateTime.UtcNow
                    };

                    await _dbContext.PostsContainer.UpsertItemAsync(userPost, new PartitionKey(userPost.PostId));
                    return Ok(new { Message = "Upload completed successfully.", BlobUrl = userPost.Content });
                }

                return Ok(new { Message = "Chunk uploaded successfully." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading chunk for UploadId {UploadId}", model.UploadId);
                return StatusCode(500, "Internal server error.");
            }
        }

        [HttpGet("getUserFeeds")]
        public async Task<IActionResult> GetUserFeeds(string? userId = null, int pageNumber = 1, int pageSize = 10)
        {
            try
            {
                var userPosts = new List<UserPost>();
                var queryString = $"SELECT * FROM f WHERE f.type='post' ORDER BY f.dateCreated DESC OFFSET {(pageNumber - 1) * pageSize} LIMIT {pageSize}";
                var query = _dbContext.FeedsContainer.GetItemQueryIterator<UserPost>(new QueryDefinition(queryString));

                while (query.HasMoreResults)
                {
                    var response = await query.ReadNextAsync();
                    userPosts.AddRange(response.ToList());
                }

                return Ok(userPosts);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving user feeds.");
                return StatusCode(500, "Error retrieving feeds.");
            }
        }

       
    }
}
