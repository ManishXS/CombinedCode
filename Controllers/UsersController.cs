using Microsoft.AspNetCore.Mvc;
using BackEnd.Entities;
using Microsoft.Azure.Cosmos;
using System.Resources;
using User = BackEnd.Entities.User;
using System.Collections;
using System.Reflection;
using Azure.Storage.Blobs;
using BackEnd.Models;
using BackEnd.Shared;
using Microsoft.Azure.Cosmos.Linq;
using System.Drawing.Printing;
using System.Net;

namespace BackEnd.Controllers
{
    [ApiController]
    [Route("api/[controller]")]

    public class UsersController : ControllerBase
    {
        private readonly CosmosDbContext _dbContext;
        private readonly BlobServiceClient _blobServiceClient;
        private static readonly Random _random = new Random();
        private readonly string _profileContainer = "profilepic";  // Blob container for storing profilepic

        // Base URL for the profile pictures served via CDN
        private static readonly string _cdnBaseUrl = "https://tenxcdn.azureedge.net/profilepic/";

        public UsersController(CosmosDbContext dbContext, BlobServiceClient blobServiceClient)
        {
            _dbContext = dbContext;
            _blobServiceClient = blobServiceClient;
        }

        /// <summary>
        /// Upload a new feed with media.
        /// </summary>
        [HttpPost("updateUser")]
        public async Task<IActionResult> updateUser([FromForm] FeedUploadModel model)
        {
            try
            {
                string updatedPicURL = string.Empty;

                // Ensure required fields are present
                if (model.File != null && !string.IsNullOrEmpty(model.UserId) && !string.IsNullOrEmpty(model.FileName) && !string.IsNullOrEmpty(model.ProfilePic))
                {
                    // Get Blob container reference
                    var containerClient = _blobServiceClient.GetBlobContainerClient(_profileContainer);

                    // Set Blob name same as the already set profile pic
                    var blobName = Utility.GetFileNameFromUrl(model.ProfilePic); ;
                    var blobClient = containerClient.GetBlobClient(blobName);

                    // Upload the file to Blob Storage
                    using (var stream = model.File.OpenReadStream())
                    {
                        await blobClient.UploadAsync(stream, overwrite: true);
                    }

                    // Get the Blob URL
                    var blobUrl = blobClient.Uri.ToString();
                    updatedPicURL = blobUrl;
                }


                // Create an instance of the item you want to update

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
                return StatusCode(500, $"Error updating Profilepic: {ex.Message}");
            }
        }

        // Change the route to "create" for clarity
        [HttpGet("getUser")]
        public async Task<IActionResult> GetUser(string userId)
        {
            try
            {
                // Start with the base query
                IQueryable<BlogUser> query = _dbContext.UsersContainer.GetItemLinqQueryable<BlogUser>();

                // Apply the userId filter if provided
                if (!string.IsNullOrEmpty(userId))
                {
                    query = query.Where(x => x.UserId == userId);
                }

                // Apply ordering after filtering
                var result = query.Select(item => new
                {
                    item.Id,
                    item.Username,
                    item.ProfilePicUrl
                }).FirstOrDefault();

                if (result!=null)
                {
                    return Ok(new
                    {
                        userId = result.Id,
                        username = result.Username,
                        profilePic = result.ProfilePicUrl
                    });
                }
                else
                {
                    return Ok(new
                    {
                        userId = string.Empty,
                        username = string.Empty,
                        profilePic = string.Empty
                    });
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


        // Change the route to "create" for clarity
        [HttpGet("create")]
        public async Task<IActionResult> CreateUser()
        {
            try
            {
                var user = new BlogUser
                {
                    UserId = Guid.NewGuid().ToString(),
                    Username = GenerateRandomName(),
                    ProfilePicUrl = GetRandomProfilePic()
                };

                try
                {
                    //var uniqueUsername = new UniqueUsername { Username = user.Username };

                    ////First create a user with a partitionkey as "unique_username" and the new username.  Using the same partitionKey "unique_username" will put all of the username in the same logical partition.
                    ////  Since there is a Unique Key on /username (per logical partition), trying to insert a duplicate username with partition key "unique_username" will cause a Conflict.
                    ////  This question/answer https://stackoverflow.com/a/62438454/21579
                    //await _dbContext.UsersContainer.CreateItemAsync<UniqueUsername>(uniqueUsername, new PartitionKey(uniqueUsername.UserId));
                    user.Action = "Create";
                    //if we get past adding a new username for partition key "unique_username", then go ahead and insert the new user.
                    await _dbContext.UsersContainer.CreateItemAsync<BlogUser>(user, new PartitionKey(user.UserId));

                    return Ok(new
                    {
                        userId = user.UserId,
                        username = user.Username,
                        profilePic = user.ProfilePicUrl
                    });
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
                {
                    //item already existed.  Optimize for the success path.
                    throw ex;// ("", $"User with the username {username} already exists.");
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

        // Generates a random name by combining a random adjective and noun from the resx file
        private string GenerateRandomName()
        {
            string adjective = GetRandomResource("Adj_");
            string noun = GetRandomResource("Noun_");

            // Make sure at least one word is French
            bool isFrenchInAdjective = _random.Next(2) == 0;
            string finalAdjective = isFrenchInAdjective ? GetFrenchPart(adjective) : GetEnglishPart(adjective);
            string finalNoun = isFrenchInAdjective ? GetEnglishPart(noun) : GetFrenchPart(noun);

            return $"{finalAdjective}_{finalNoun}";
        }

        // Returns a random profile picture URL from the range pp1 to pp25
        private string GetRandomProfilePic()
        {
            int randomNumber = _random.Next(1, 26); // Generate a random number between 1 and 25
            return $"{_cdnBaseUrl}pp{randomNumber}.jpg";
        }

        // Get a random resource entry (adjective or noun) from the resx file
        private string GetRandomResource(string resourceType)
        {
            ResourceManager resourceManager = new ResourceManager("BackEnd.Resources.AdjectivesNouns", Assembly.GetExecutingAssembly());
            var resourceSet = resourceManager.GetResourceSet(System.Globalization.CultureInfo.CurrentUICulture, true, true);

            if (resourceSet == null)
            {
                throw new Exception("ResourceSet is null. Resource file might not be found.");
            }

            var matchingEntries = new List<DictionaryEntry>();
            foreach (DictionaryEntry entry in resourceSet)
            {
                if (entry.Key.ToString().StartsWith(resourceType))
                {
                    matchingEntries.Add(entry);
                }
            }

            if (matchingEntries.Count == 0)
            {
                throw new Exception($"No matching {resourceType} resources found.");
            }

            // Select a random entry
            DictionaryEntry selectedEntry = matchingEntries[_random.Next(matchingEntries.Count)];

            // Safeguard against unboxing null values (CS8605 fix)
            if (selectedEntry.Value != null && selectedEntry.Key != null)
            {
                return $"{selectedEntry.Key}-{selectedEntry.Value}";
            }

            throw new Exception("Invalid resource entry detected.");
        }

        // Extract the French part from the name (e.g., "Adj_Aventureux-Adventurous")
        private string GetFrenchPart(string entry)
        {
            var parts = entry?.Split('-');
            return parts?[0].Split('_')[1];
        }

        // Extract the English part from the value (e.g., "Adj_Aventureux-Adventurous")
        private string GetEnglishPart(string entry)
        {
            var parts = entry?.Split('-');
            return parts?[1];
        }
    }
}
