using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using StackExchange.Redis;
using BackEnd.Entities;
using BackEnd.Models;
using System.Security.Cryptography;

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

        public FeedsController(CosmosDbContext dbContext, BlobServiceClient blobServiceClient,  ILogger<FeedsController> logger)
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
                // Validate required fields
                if (model.File == null || string.IsNullOrEmpty(model.UserId) || string.IsNullOrEmpty(model.FileName))
                {
                    Console.WriteLine("Validation failed: Missing required fields.");
                    return BadRequest("Missing required fields.");
                }

                Console.WriteLine($"Received file: {model.File.FileName}, Size: {model.File.Length} bytes");
                Console.WriteLine($"User ID: {model.UserId}, User Name: {model.UserName}");

                // Get Blob container reference
                var containerClient = _blobServiceClient.GetBlobContainerClient(_feedContainer);
                Console.WriteLine($"Connecting to Blob Container: {_feedContainer}");

                // Generate a unique Blob name
                var blobName = $"{Guid.NewGuid()}_{Path.GetFileName(model.File.FileName)}";
                var blobClient = containerClient.GetBlobClient(blobName);
                Console.WriteLine($"Generated Blob Name: {blobName}");

                // Upload the file to Azure Blob Storage with buffering and CRC32 checksum verification
                const int BufferSize = 4 * 1024 * 1024; // 4 MB buffer size
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

                // Create the user post record
                var userPost = new UserPost
                {
                    PostId = Guid.NewGuid().ToString(),
                    Title = model.ProfilePic,
                    Content = $"{_cdnBaseUrl}{blobName}",
                    Caption = string.IsNullOrEmpty(model.Caption) ? string.Empty : model.Caption,
                    AuthorId = model.UserId,
                    AuthorUsername = model.UserName,
                    DateCreated = DateTime.UtcNow,
                    Checksum = checksum
                };

                // Insert the user post into the Cosmos DB database
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
        public async Task<IActionResult> GetUserFeeds(string? userId = null, int pageNumber = 1, int pageSize = 2) // Default pageSize set to 2
        {
            try
            {
                var userPosts = new List<UserPost>();
                var queryString = $"SELECT * FROM f WHERE f.type='post' ORDER BY f.dateCreated DESC OFFSET {(pageNumber - 1) * pageSize} LIMIT {pageSize}"; // Pagination logic


                    var queryFromPostsContainer = _dbContext.PostsContainer.GetItemQueryIterator<UserPost>(new QueryDefinition(queryString));
                    while (queryFromPostsContainer.HasMoreResults)
                    {
                        var response = await queryFromPostsContainer.ReadNextAsync();
                        userPosts.AddRange(response.ToList());
                    }

                if (!string.IsNullOrEmpty(userId))
                {
                    var userLikes = new List<UserPost>();
                    foreach (var item in userPosts)
                    {
                        userLikes = new List<UserPost>();

                       var queryString_likes = $"SELECT  * FROM f WHERE f.type='like' and f.postId='pid' and f.userId='uid'";
                      // var queryString_likes = $"SELECT * FROM f WHERE f.type='post' ORDER BY (f.likeCount * 2 + f.commentCount * 1.5) DESC, f.dateCreated DESC OFFSET {(pageNumber - 1) * pageSize} LIMIT {pageSize}";

                        queryString_likes = queryString_likes.Replace("uid", userId);
                        queryString_likes = queryString_likes.Replace("pid", item.PostId);
                        var queryFromPostsContainter_likes = _dbContext.PostsContainer.GetItemQueryIterator<UserPost>(new QueryDefinition(queryString_likes));
                        while (queryFromPostsContainter_likes.HasMoreResults)
                        {
                            var response = await queryFromPostsContainter_likes.ReadNextAsync();
                            var ru = response.RequestCharge;
                            userLikes.AddRange(response.ToList());
                        }
                        if (userLikes.Count > 0)
                        {
                            item.LikeFlag = 1;
                        }
                    }
                 
                }

                return Ok(new { BlogPostsMostRecent = userPosts }); // Returning paginated data
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error retrieving feeds: {ex.Message}");
            }
        }


    }

    // CRC32 checksum implementation
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
