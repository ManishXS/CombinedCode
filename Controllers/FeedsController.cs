using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using BackEnd.Entities;
using BackEnd.Models;
using BackEnd.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using StackExchange.Redis;

namespace BackEnd.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FeedsController : ControllerBase
    {
        private readonly CosmosDbContext _dbContext;
        private readonly BlobServiceClient _blobServiceClient;
        private readonly string _feedContainer = "media";


        private static readonly string _cdnBaseUrl = "https://tenxcdn-dtg6a0dtb9aqg3bb.z02.azurefd.net/media/";
        private readonly IConnectionMultiplexer _redis;
        private readonly ILogger<FeedsController> _logger;

        //public FeedsController(CosmosDbContext dbContext, BlobServiceClient blobServiceClient, IConnectionMultiplexer redis)
        //{
        //    _dbContext = dbContext;
        //    _blobServiceClient = blobServiceClient;
        //    _redis = redis;
        //}

        public FeedsController(CosmosDbContext dbContext, BlobServiceClient blobServiceClient, IConnectionMultiplexer redis, ILogger<FeedsController> logger)
        {
            _dbContext = dbContext;
            _blobServiceClient = blobServiceClient;
            _redis = redis;
            _logger = logger; // Initialize logger
        }
        [HttpPost("uploadFeed")]
        public async Task<IActionResult> UploadFeed([FromForm] IFormFile file, [FromForm] string uploadId)
        {
            try
            {
                _logger.LogInformation("Starting upload process for uploadId: {UploadId}", uploadId);

                // Validate Input
                if (file == null || file.Length == 0)
                {
                    _logger.LogError("No file provided or file is empty for uploadId: {UploadId}", uploadId);
                    return BadRequest("No file provided or file is empty.");
                }

                if (string.IsNullOrEmpty(uploadId))
                {
                    _logger.LogError("UploadId is missing.");
                    return BadRequest("UploadId is required.");
                }

                _logger.LogInformation("Received file: {FileName}, Size: {FileSize}, UploadId: {UploadId}", file.FileName, file.Length, uploadId);

                // Redis Setup
                var db = _redis.GetDatabase();
                _logger.LogInformation("Connecting to Redis...");
                var pingResult = await db.PingAsync();
                _logger.LogInformation("Redis connection successful. Ping: {PingResult}", pingResult);

                // Track Current Chunk
                var currentChunkValue = await db.StringGetAsync($"{uploadId}:currentChunk");
                var currentChunk = int.Parse(currentChunkValue.IsNullOrEmpty ? "0" : currentChunkValue.ToString());
                _logger.LogInformation("Current chunk index for uploadId {UploadId}: {CurrentChunk}", uploadId, currentChunk);

                // Azure Blob Storage
                _logger.LogInformation("Connecting to Azure Blob Storage...");
                var containerClient = _blobServiceClient.GetBlobContainerClient(_feedContainer);
                var blobClient = containerClient.GetBlobClient(uploadId);

                if (!await containerClient.ExistsAsync())
                {
                    _logger.LogError("Blob container '{Container}' does not exist.", _feedContainer);
                    return StatusCode(500, "Blob container does not exist.");
                }
                _logger.LogInformation("Blob container '{Container}' is available.", _feedContainer);

                // Upload Chunk
                _logger.LogInformation("Uploading chunk for uploadId {UploadId}, ChunkIndex: {ChunkIndex}", uploadId, currentChunk);
                using (var stream = file.OpenReadStream())
                {
                    await blobClient.UploadAsync(stream, overwrite: true);
                }
                _logger.LogInformation("Chunk upload successful for uploadId: {UploadId}, ChunkIndex: {ChunkIndex}", uploadId, currentChunk);

                // Increment Current Chunk
                currentChunk++;
                await db.StringSetAsync($"{uploadId}:currentChunk", currentChunk);
                _logger.LogInformation("Updated current chunk index in Redis for uploadId: {UploadId}. CurrentChunk: {CurrentChunk}", uploadId, currentChunk);

                // Handle Final Chunk
                var totalChunksValue = await db.StringGetAsync($"{uploadId}:totalChunks");
                var totalChunks = int.Parse(totalChunksValue.IsNullOrEmpty ? "0" : totalChunksValue.ToString());
                _logger.LogInformation("Total chunks for uploadId {UploadId}: {TotalChunks}", uploadId, totalChunks);

                if (currentChunk == totalChunks)
                {
                    _logger.LogInformation("All chunks received for uploadId: {UploadId}. Finalizing upload.", uploadId);
                    await db.KeyDeleteAsync($"{uploadId}:currentChunk");
                    await db.KeyDeleteAsync($"{uploadId}:totalChunks");
                    return Ok("File upload completed.");
                }

                return Ok($"Chunk {currentChunk} uploaded.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during file upload for uploadId: {UploadId}", uploadId);
                return StatusCode(500, $"Internal Server Error: {ex.Message}");
            }
        }


        [HttpGet("getUserFeeds")]
        public async Task<IActionResult> GetUserFeeds(string? userId = null, int pageNumber = 1, int pageSize = 10)
        {
            try
            {
                var m = new BlogHomePageViewModel();
                var numberOfPosts = 100;
                var userPosts = new List<UserPost>();

                var queryString = $"SELECT TOP {numberOfPosts} * FROM f WHERE f.type='post' ORDER BY f.dateCreated DESC";
                var query = _dbContext.FeedsContainer.GetItemQueryIterator<UserPost>(new QueryDefinition(queryString));
                while (query.HasMoreResults)
                {
                    var response = await query.ReadNextAsync();
                    var ru = response.RequestCharge;
                    userPosts.AddRange(response.ToList());
                }

                //if there are no posts in the feedcontainer, go to the posts container.
                // There may be one that has not propagated to the feed container yet by the azure function (or the azure function is not running).
                if (!userPosts.Any())
                {
                    var queryFromPostsContainter = _dbContext.PostsContainer.GetItemQueryIterator<UserPost>(new QueryDefinition(queryString));
                    while (queryFromPostsContainter.HasMoreResults)
                    {
                        var response = await queryFromPostsContainter.ReadNextAsync();
                        var ru = response.RequestCharge;
                        userPosts.AddRange(response.ToList());
                    }
                }

                m.BlogPostsMostRecent = userPosts;

                return Ok(m);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error retrieving feeds: {ex.Message}");
            }
        }

        [HttpGet("getChats")]
        public async Task<IActionResult> getChats(string userId)
        {
            try
            {
                var userChats = new List<Chats>();
                var queryString = $"SELECT * FROM f WHERE CONTAINS(f.chatId, 'userId')";
                queryString = queryString.Replace("userId", userId);
                var query = _dbContext.ChatsContainer.GetItemQueryIterator<Chats>(new QueryDefinition(queryString));
                while (query.HasMoreResults)
                {
                    var response = await query.ReadNextAsync();
                    userChats.AddRange(response.ToList());
                }

                List<ChatList> chatList = new List<ChatList>();
                foreach (var item in userChats)
                {
                    string toUserId = item.chatId;
                    toUserId = toUserId.Replace(userId, "");
                    toUserId = toUserId.Replace("|", "");

                    IQueryable<BlogUser> queryUsers = _dbContext.UsersContainer.GetItemLinqQueryable<BlogUser>();

                    if (!string.IsNullOrEmpty(toUserId))
                    {
                        queryUsers = queryUsers.Where(x => x.UserId == toUserId);
                    }

                    var resultUser = queryUsers.Select(item => new
                    {
                        item.Id,
                        item.UserId,
                        item.Username,
                        item.ProfilePicUrl
                    }).FirstOrDefault();

                    if (resultUser != null)
                    {
                        ChatList chatList1 = new ChatList();
                        chatList1.toUserName = resultUser.Username;
                        chatList1.toUserId = resultUser.UserId;
                        chatList1.toUserProfilePic = resultUser.ProfilePicUrl;

                        chatList1.chatWindow = new List<ChatWindow>();

                        List<ChatWindow> chatWindows = new List<ChatWindow>();
                        foreach (var chatMessage in item.chatMessage.Reverse())
                        {
                            ChatWindow chatWindow = new ChatWindow();
                            chatWindow.message = chatMessage.message;
                            if (chatMessage.fromuserId == userId)
                            {
                                chatWindow.type = "reply";
                            }
                            else
                            {
                                chatWindow.type = "sender";
                            }
                            chatWindows.Add(chatWindow);
                        }

                        chatList1.chatWindow.AddRange(chatWindows);

                        chatList.Add(chatList1);
                    }
                }

                return Ok(chatList);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error retrieving feeds: {ex.Message}");
            }
        }
    }
}
