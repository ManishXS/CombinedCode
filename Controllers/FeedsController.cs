using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using StackExchange.Redis;
using BackEnd.Entities;
using BackEnd.Models;
using System.Security.Cryptography;
using tusdotnet.Models.Configuration;
using tusdotnet.Models;
using tusdotnet.Stores;
using tusdotnet.Helpers;

namespace BackEnd.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FeedsController : ControllerBase
    {
        private readonly CosmosDbContext _dbContext;
        private readonly BlobServiceClient _blobServiceClient;
        private readonly string _feedContainer = "media";
        private readonly IDatabase _redis;
        private readonly ILogger<FeedsController> _logger;

        public FeedsController(CosmosDbContext dbContext, BlobServiceClient blobServiceClient, ILogger<FeedsController> logger)
        {
            _dbContext = dbContext;
            _blobServiceClient = blobServiceClient;
            _logger = logger;
        }

        [HttpPost("uploadFeed")]
        public async Task<IActionResult> UploadFeed([FromForm] FeedUploadModel model, CancellationToken cancellationToken)
        {
            Console.WriteLine("Starting UploadFeed API...");
            try
            {
                if (model.File == null || string.IsNullOrEmpty(model.UserId) || string.IsNullOrEmpty(model.FileName))
                {
                    Console.WriteLine("Validation failed: Missing required fields.");
                    return BadRequest("Missing required fields.");
                }
                Console.WriteLine($"Received file: {model.File.FileName}, Size: {model.File.Length} bytes");
                Console.WriteLine($"User ID: {model.UserId}, User Name: {model.UserName}");
                var containerClient = _blobServiceClient.GetBlobContainerClient(_feedContainer);
                Console.WriteLine($"Connecting to Blob Container: {_feedContainer}");
                var blobName = $"{ShortGuidGenerator.Generate()}_{Path.GetFileName(model.File.FileName)}";
                var blobClient = containerClient.GetBlobClient(blobName);
                Console.WriteLine($"Generated Blob Name: {blobName}");
                const int BufferSize = 4 * 1024 * 1024;
                using var crc32 = new Crc32();
                await using var fileStream = model.File.OpenReadStream();
                await using var blobStream = await blobClient.OpenWriteAsync(overwrite: true, cancellationToken: cancellationToken);
                byte[] buffer = new byte[BufferSize];
                int bytesRead;
                long totalBytesUploaded = 0;
                Console.WriteLine("Starting file upload...");
                while ((bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await blobStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    crc32.TransformBlock(buffer, 0, bytesRead, buffer, 0);
                    totalBytesUploaded += bytesRead;
                    Console.WriteLine($"Uploaded {totalBytesUploaded} bytes so far...");
                }
                crc32.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var checksum = BitConverter.ToString(crc32.Hash).Replace("-", "").ToLowerInvariant();
                Console.WriteLine($"File upload completed. CRC32 Checksum: {checksum}");
                var blobUrl = blobClient.Uri.ToString();
                Console.WriteLine($"Blob URL: {blobUrl}");
                var userPost = new UserPost
                {
                    PostId = ShortGuidGenerator.Generate(),
                    Title = model.ProfilePic,
                    Content = blobUrl,
                    Caption = string.IsNullOrEmpty(model.Caption) || model.Caption == "undefined" ? string.Empty : model.Caption,
                    AuthorId = model.UserId,
                    AuthorUsername = model.UserName,
                    DateCreated = DateTime.UtcNow,
                    Checksum = checksum
                };
                await _dbContext.PostsContainer.UpsertItemAsync(userPost, new PartitionKey(userPost.PostId));
                Console.WriteLine("User post successfully saved to Cosmos DB.");
                return Ok(new
                {
                    Message = "Feed uploaded successfully.",
                    FeedId = userPost.PostId,
                    Checksum = checksum
                });
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Upload was canceled by the client or due to timeout.");
                return StatusCode(499, "Upload was canceled due to timeout or client cancellation.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during upload: {ex.Message}");
                return StatusCode(500, $"Error uploading feed: {ex.Message}");
            }
        }

        [HttpGet("getUserFeeds")]
        public async Task<IActionResult> GetUserFeeds(string? userId = null, int pageNumber = 1, int pageSize = 2)
        {
            try
            {
                var userPosts = new List<UserPost>();
                var queryString = $"SELECT * FROM f WHERE f.type='post' ORDER BY f.dateCreated DESC OFFSET {(pageNumber - 1) * pageSize} LIMIT {pageSize}";
                Console.WriteLine($"Executing query: {queryString}");
                var queryFromPostsContainer = _dbContext.PostsContainer.GetItemQueryIterator<UserPost>(new QueryDefinition(queryString));
                while (queryFromPostsContainer.HasMoreResults)
                {
                    var response = await queryFromPostsContainer.ReadNextAsync();
                    Console.WriteLine($"Fetched {response.Count} posts from Cosmos DB.");
                    userPosts.AddRange(response.ToList());
                }
                if (!string.IsNullOrEmpty(userId))
                {
                    Console.WriteLine($"UserId provided: {userId}. Checking user likes...");
                    var userLikes = new List<UserPost>();
                    foreach (var item in userPosts)
                    {
                        userLikes = new List<UserPost>();
                        var queryString_likes = $"SELECT * FROM f WHERE f.type='like' and f.postId='pid' and f.userId='uid'";
                        queryString_likes = queryString_likes.Replace("uid", userId);
                        queryString_likes = queryString_likes.Replace("pid", item.PostId);
                        Console.WriteLine($"Executing like check query: {queryString_likes}");
                        var queryFromPostsContainter_likes = _dbContext.PostsContainer.GetItemQueryIterator<UserPost>(new QueryDefinition(queryString_likes));
                        while (queryFromPostsContainter_likes.HasMoreResults)
                        {
                            var response = await queryFromPostsContainter_likes.ReadNextAsync();
                            Console.WriteLine($"Fetched {response.Count} like records for postId {item.PostId} and userId {userId}.");
                            userLikes.AddRange(response.ToList());
                        }
                        if (userLikes.Count > 0)
                        {
                            item.LikeFlag = 1;
                            Console.WriteLine($"User {userId} has liked post {item.PostId}.");
                        }
                    }
                }
                Console.WriteLine("Reordering posts by LikeCount, CommentCount, and DateCreated...");
                userPosts = userPosts
                    .OrderByDescending(x => x.LikeCount)
                    .ThenByDescending(x => x.CommentCount)
                    .ThenByDescending(x => x.DateCreated)
                    .ToList();
                Console.WriteLine("Reordering complete.");
                Console.WriteLine("Returning final ordered list of posts.");
                return Ok(new { BlogPostsMostRecent = userPosts });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error retrieving feeds: {ex.Message}");
                return StatusCode(500, $"Error retrieving feeds: {ex.Message}");
            }
        }

        [HttpGet("download")]
        public async Task<IActionResult> DownloadFile([FromQuery] string fileName)
        {
            try
            {
                if (string.IsNullOrEmpty(fileName))
                {
                    return BadRequest("File name is required.");
                }
                var containerClient = _blobServiceClient.GetBlobContainerClient(_feedContainer);
                var blobClient = containerClient.GetBlobClient(fileName);
                if (!await blobClient.ExistsAsync())
                {
                    return NotFound("File not found.");
                }
                var downloadInfo = await blobClient.DownloadAsync();
                return File(downloadInfo.Value.Content, downloadInfo.Value.ContentType, fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error downloading file: {ex.Message}");
                return StatusCode(500, "Error downloading file.");
            }
        }

        public class Crc32 : HashAlgorithm
        {
            private const uint Polynomial = 0xedb88320;
            private readonly uint[] table = new uint[256];
            private uint crc = 0xffffffff;
            public Crc32()
            {
                InitializeTable();
                HashSizeValue = 32;
            }
            private void InitializeTable()
            {
                for (uint i = 0; i < 256; i++)
                {
                    uint entry = i;
                    for (int j = 0; j < 8; j++)
                    {
                        if ((entry & 1) == 1)
                            entry = (entry >> 1) ^ Polynomial;
                        else
                            entry >>= 1;
                    }
                    table[i] = entry;
                }
            }
            public override void Initialize()
            {
                crc = 0xffffffff;
            }
            protected override void HashCore(byte[] array, int ibStart, int cbSize)
            {
                for (int i = ibStart; i < ibStart + cbSize; i++)
                {
                    byte index = (byte)(crc ^ array[i]);
                    crc = (crc >> 8) ^ table[index];
                }
            }
            protected override byte[] HashFinal()
            {
                crc ^= 0xffffffff;
                return BitConverter.GetBytes(crc);
            }
        }
    }
}
