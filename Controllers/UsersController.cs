using Microsoft.AspNetCore.Mvc;
using BackEnd.Entities;
using Microsoft.Azure.Cosmos;
using Azure.Storage.Blobs;
using BackEnd.Models;
using System.Collections;
using System.Net;
using BackEnd.Shared;

namespace BackEnd.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class UsersController : ControllerBase
    {
        private readonly CosmosDbContext _dbContext;
        private readonly BlobServiceClient _blobServiceClient;
        private static readonly Random _random = new Random();
        private readonly string _profileContainer = "profilepic";  // Blob container for storing profile pictures

        public UsersController(CosmosDbContext dbContext, BlobServiceClient blobServiceClient)
        {
            _dbContext = dbContext;
            _blobServiceClient = blobServiceClient;
        }

        [HttpPost("updateUser")]
        public async Task<IActionResult> UpdateUser([FromForm] FeedUploadModel model)
        {
            try
            {
                string updatedPicURL = string.Empty;

                if (model.File != null && !string.IsNullOrEmpty(model.UserId) && !string.IsNullOrEmpty(model.FileName) && !string.IsNullOrEmpty(model.ProfilePic))
                {
                    var containerClient = _blobServiceClient.GetBlobContainerClient(_profileContainer);
                    var blobName = Utility.GetFileNameFromUrl(model.ProfilePic);
                    var blobClient = containerClient.GetBlobClient(blobName);

                    using (var stream = model.File.OpenReadStream())
                    {
                        await blobClient.UploadAsync(stream, overwrite: true);
                    }

                    updatedPicURL = blobClient.Uri.ToString(); // Direct Blob Storage URL
                }

                var itemToUpdate = new BlogUser
                {
                    UserId = model.UserId,
                    Username = model.UserName,
                    ProfilePicUrl = updatedPicURL
                };

                var updatedItem = await _dbContext.UsersContainer.UpsertItemAsync(itemToUpdate);
                Console.WriteLine("Item updated successfully: " + updatedItem);

                return Ok(new { Message = "Profile updated successfully.", UserId = model.UserId });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error updating profile picture: {ex.Message}");
            }
        }

        [HttpGet("getUser")]
        public async Task<IActionResult> GetUser(string userId)
        {
            try
            {
                IQueryable<BlogUser> query = _dbContext.UsersContainer.GetItemLinqQueryable<BlogUser>();

                if (!string.IsNullOrEmpty(userId))
                {
                    query = query.Where(x => x.UserId == userId);
                }

                var result = query.Select(item => new
                {
                    item.Id,
                    item.Username,
                    item.ProfilePicUrl
                }).FirstOrDefault();

                if (result != null)
                {
                    return Ok(new
                    {
                        userId = result.Id,
                        username = result.Username,
                        profilePic = result.ProfilePicUrl
                    });
                }

                return Ok(new { userId = string.Empty, username = string.Empty, profilePic = string.Empty });
            }
            catch (CosmosException ex)
            {
                return StatusCode(500, $"Cosmos DB Error: {ex.Message}");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"An error occurred: {ex.Message}");
            }
        }

        [HttpGet("create")]
        public async Task<IActionResult> CreateUser()
        {
            try
            {
                var user = new BlogUser
                {
                    UserId = ShortGuidGenerator.Generate(),
                    Username = GenerateRandomName(),
                    ProfilePicUrl = GetRandomProfilePic() // Updated for Blob Storage URLs
                };

                try
                {
                    user.Action = "Create";
                    await _dbContext.UsersContainer.CreateItemAsync(user, new PartitionKey(user.UserId));

                    return Ok(new
                    {
                        userId = user.UserId,
                        username = user.Username,
                        profilePic = user.ProfilePicUrl
                    });
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
                {
                    throw ex;
                }
            }
            catch (CosmosException ex)
            {
                return StatusCode(500, $"Cosmos DB Error: {ex.Message}");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"An error occurred: {ex.Message}");
            }
        }

        private string GenerateRandomName()
        {
            return $"User_{_random.Next(1000, 9999)}";
        }

        private string GetRandomProfilePic()
        {
            int randomNumber = _random.Next(1, 26); // Generate a random number between 1 and 25
            var containerClient = _blobServiceClient.GetBlobContainerClient(_profileContainer);
            return $"{containerClient.Uri}/pp{randomNumber}.jpg"; // Blob Storage URL
        }
    }
}
