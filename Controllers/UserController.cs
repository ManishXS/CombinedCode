using BlogWebApp.ViewModels;
using Microsoft.AspNetCore.Mvc;
using BackEnd.Entities;
using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;

namespace BackEnd.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class UserController : ControllerBase
    {
        private readonly CosmosDbContext _dbContext;
        private readonly BlobServiceClient _blobServiceClient;
        private readonly string _feedContainer = "profilepic";

        // New CDN Base URL
        private static readonly string _cdnBaseUrl = "https://tenxcdn-dtg6a0dtb9aqg3bb.z02.azurefd.net/profilepic/";

        public UserController(CosmosDbContext dbContext, BlobServiceClient blobServiceClient)
        {
            _dbContext = dbContext;
            _blobServiceClient = blobServiceClient;
        }

        [Route("user")]
        [HttpPost]
        public async Task<IActionResult> UserProfile(string newUsername)
        {
            var oldUsername = User.Identity.Name;

            if (newUsername != oldUsername)
            {
                var queryDefinition = new QueryDefinition("SELECT * FROM u WHERE u.type = 'user' AND u.username = @username")
                    .WithParameter("@username", oldUsername);

                var query = _dbContext.UsersContainer.GetItemQueryIterator<BlogUser>(queryDefinition);

                List<BlogUser> results = new List<BlogUser>();
                while (query.HasMoreResults)
                {
                    var response = await query.ReadNextAsync();
                    results.AddRange(response.ToList());
                }

                if (results.Count > 1)
                {
                    throw new Exception($"More than one user found for username '{newUsername}'");
                }

                var u = results.SingleOrDefault();

                // Set the new username on the user object
                u.Username = newUsername;

                // First try to create the username in the partition with partitionKey "unique_username" to confirm the username does not exist already
                var uniqueUsername = new UniqueUsername { Username = u.Username };
                await _dbContext.UsersContainer.CreateItemAsync<UniqueUsername>(uniqueUsername, new PartitionKey(uniqueUsername.UserId));

                u.Action = "Update";

                // Update the user's username
                await _dbContext.UsersContainer.ReplaceItemAsync<BlogUser>(u, u.UserId, new PartitionKey(u.UserId));

                // Delete the old "unique_username" for the username that just changed
                var queryDefinition1 = new QueryDefinition("SELECT * FROM u WHERE u.userId = 'unique_username' AND u.type = 'unique_username' AND u.username = @username")
                    .WithParameter("@username", oldUsername);
                var query1 = _dbContext.UsersContainer.GetItemQueryIterator<BlogUniqueUsername>(queryDefinition1);
                while (query1.HasMoreResults)
                {
                    var response = await query1.ReadNextAsync();

                    var oldUniqueUsernames = response.ToList();

                    foreach (var oldUniqueUsername in oldUniqueUsernames)
                    {
                        // Last delete the old unique username entry
                        await _dbContext.UsersContainer.DeleteItemAsync<BlogUser>(oldUniqueUsername.Id, new PartitionKey("unique_username"));
                    }
                }
            }

            var m = new UserProfileViewModel
            {
                OldUsername = newUsername,
                NewUsername = newUsername
            };

            return Ok(m);
        }

        [Route("user/{userId}/posts")]
        [HttpGet]
        public async Task<IActionResult> UserPosts(string userId)
        {
            var blogPosts = new List<UserPost>();
            var queryString = $"SELECT * FROM p WHERE p.type='post' AND p.userId = @UserId ORDER BY p.dateCreated DESC";
            var queryDef = new QueryDefinition(queryString);
            queryDef.WithParameter("@UserId", userId);
            var query = _dbContext.UsersContainer.GetItemQueryIterator<UserPost>(queryDef);

            while (query.HasMoreResults)
            {
                var response = await query.ReadNextAsync();
                var ru = response.RequestCharge;
                blogPosts.AddRange(response.ToList());
            }


            foreach (var post in blogPosts)
            {
                if (!string.IsNullOrEmpty(post.Content)) // Assuming Content holds media URL
                {
                    post.Content = post.Content.Replace("https://storagetenx.blob.core.windows.net/profilepic/", _cdnBaseUrl);
                }
            }

            var username = "";

            var firstPost = blogPosts.FirstOrDefault();
            if (firstPost != null)
            {
                username = firstPost.AuthorUsername;
            }

            var m = new UserPostsViewModel
            {
                Username = username,
                Posts = blogPosts
            };
            return Ok(m);
        }
    }
}
