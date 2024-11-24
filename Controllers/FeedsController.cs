using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using BackEnd.Entities;
using BackEnd.Models;
using BackEnd.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using System.Text;

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

        public FeedsController(CosmosDbContext dbContext, BlobServiceClient blobServiceClient)
        {
            _dbContext = dbContext;
            _blobServiceClient = blobServiceClient;
        }

        [HttpPost("uploadFeed")]
        public async Task<IActionResult> UploadFeed([FromForm] FeedUploadModel model, [FromQuery] bool isChunked = false, [FromForm] string? uploadSessionId = null, [FromForm] string? blockId = null)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                if (model.File.Length > 524288000) // 500 MB limit
                    return BadRequest("File size exceeds the maximum allowed size of 0.5 GB.");

                var containerClient = _blobServiceClient.GetBlobContainerClient(_feedContainer);
                string fileName = model.FileName;
                string uploadSession = uploadSessionId ?? Guid.NewGuid().ToString();
                var blobClient = containerClient.GetBlockBlobClient($"{uploadSession}_{fileName}");

                if (isChunked)
                {
                    if (blockId == null)
                        return BadRequest("Block ID is required for chunked uploads.");

                    await using var chunkStream = model.File.OpenReadStream();
                    await blobClient.StageBlockAsync(blockId, chunkStream);
                    return Ok(new { Message = "Chunk uploaded successfully." });
                }
                else
                {
                    var blockList = await blobClient.GetBlockListAsync(BlockListTypes.All);
                    var blockIds = blockList.Value.UncommittedBlocks.Select(b => b.Name).ToList();

                    if (blockIds.Count == 0)
                        return BadRequest("No uncommitted blocks found to finalize the upload.");

                    await blobClient.CommitBlockListAsync(blockIds);

                    var blobUrl = $"{_cdnBaseUrl}{blobClient.Name}";

                    var userPost = new UserPost
                    {
                        PostId = Guid.NewGuid().ToString(),
                        Title = model.ProfilePic,
                        Content = blobUrl,
                        Caption = model.Caption,
                        AuthorId = model.UserId,
                        AuthorUsername = model.UserName,
                        DateCreated = DateTime.UtcNow,
                    };

                    await _dbContext.PostsContainer.UpsertItemAsync(userPost, new PartitionKey(userPost.PostId));

                    return Ok(new { Message = "Feed uploaded successfully.", FeedId = userPost.PostId });
                }
            }
            catch (CosmosException ex)
            {
                return StatusCode(500, $"Cosmos DB error: {ex.Message}");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Unexpected error: {ex.Message}");
            }
        }


        [HttpGet("streamVideo")]
        public async Task<IActionResult> StreamVideo(string fileName)
        {
            try
            {
                var containerClient = _blobServiceClient.GetBlobContainerClient(_feedContainer);
                var blobClient = containerClient.GetBlobClient(fileName);

                if (!await blobClient.ExistsAsync())
                    return NotFound("Video not found.");

                var properties = await blobClient.GetPropertiesAsync();
                var stream = await blobClient.OpenReadAsync();

                Response.Headers.Add("Accept-Ranges", "bytes");

                return File(stream, properties.Value.ContentType, enableRangeProcessing: true);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error streaming video: {ex.Message}");
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
