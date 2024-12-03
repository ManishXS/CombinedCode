using Azure.Identity;
using Azure.Storage.Blobs;
using BackEnd.Entities;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Azure.Cosmos;
using Microsoft.OpenApi.Models;
using StackExchange.Redis;

namespace BackEnd
{
    public class Startup
    {
        private readonly IConfiguration _configuration;

        public Startup(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            try
            {
                Console.WriteLine("Starting ConfigureServices...");

                // Azure App Configuration
                var appConfigConnectionString = "Endpoint=https://azurermtenx.azconfig.io;" +
                                                "Id=8FPB;" +
                                                "Secret=3NCoPOSo0Y1ykrX6ih9ObYVbY2ZA6RLqaXyMyBI04eB5k4wkhpA5JQQJ99AKACGhslBY0DYHAAACAZAC1woJ";

                var updatedConfiguration = new ConfigurationBuilder()
                    .AddConfiguration(_configuration)
                    .AddAzureAppConfiguration(options =>
                    {
                        options.Connect(appConfigConnectionString)
                               .ConfigureKeyVault(kv => kv.SetCredential(new DefaultAzureCredential()));
                    })
                    .Build();

                services.Configure<FormOptions>(options =>
                {
                    options.MultipartBodyLengthLimit = 512 * 1024 * 1024; // 512 MB
                });



                // Read configuration values
                var cosmosDbConnectionString = updatedConfiguration["CosmosDbConnectionString"];
                var blobConnectionString = updatedConfiguration["BlobConnectionString"];
            

                var apiKey = updatedConfiguration["ApiKey"];

                // Validate required configurations
                if (string.IsNullOrEmpty(cosmosDbConnectionString) ||
                    string.IsNullOrEmpty(blobConnectionString) ||
                    string.IsNullOrEmpty(apiKey))
                {
                    throw new Exception("Required configuration is missing. Check CosmosDbConnectionString, BlobConnectionString, RedisConnectionString, and ApiKey.");
                }

                // Cosmos DB Client
                CosmosClientOptions clientOptions = new CosmosClientOptions
                {
                    ConnectionMode = ConnectionMode.Direct,
                    MaxRequestsPerTcpConnection = 10,
                    MaxTcpConnectionsPerEndpoint = 10
                };
                CosmosClient cosmosClient = new CosmosClient(cosmosDbConnectionString, clientOptions);
                services.AddSingleton(cosmosClient);
                services.AddScoped<CosmosDbContext>();

                // Blob Service Client
                services.AddSingleton(x => new BlobServiceClient(blobConnectionString));



                // Inject updated configuration
                services.AddSingleton<IConfiguration>(updatedConfiguration);

                // CORS Configuration
                services.AddCors(options =>
                {
                    options.AddPolicy("AllowSpecific", builder =>
                    {
                        builder.WithOrigins("https://tenxso.com")
                               .AllowAnyHeader()
                               .AllowAnyMethod()
                               .AllowCredentials();
                    });
                });

                // SignalR for real-time communication
                services.AddSignalR();

                // Controllers and Swagger
                services.AddControllers();
                services.AddSwaggerGen(c =>
                {
                    c.SwaggerDoc("v1", new OpenApiInfo { Title = "My API", Version = "v1" });
                });

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in ConfigureServices: {ex.Message}");
                throw;
            }
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILogger<Startup> logger)
        {
            try
            {
                logger.LogInformation("Starting Configure...");

                if (env.IsDevelopment())
                {
                    app.UseDeveloperExceptionPage();
                    app.UseSwagger();
                    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "My API V1"));
                    logger.LogInformation("Development mode - Swagger UI enabled.");
                }

                app.UseHttpsRedirection();
                app.UseRouting();

                // CORS
                app.UseCors("AllowSpecific");

                // Logging Middleware
                app.Use(async (context, next) =>
                {
                    logger.LogInformation("Incoming Request: {Method} {Path}", context.Request.Method, context.Request.Path);
                    await next.Invoke();
                    logger.LogInformation("Response Status: {StatusCode}", context.Response.StatusCode);
                });

                // Middleware for skipping authorization (only if intentional)
                app.UseMiddleware<SkipAuthorizationMiddleware>();

                // Authorization and Endpoints
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapControllers();
                    endpoints.MapHub<ChatHub>("/chatHub");
                    endpoints.MapFallbackToFile("index.html");

                });

                logger.LogInformation("Application configured successfully.");
            }
            catch (Exception ex)
            {
                logger.LogError($"Error in Configure: {ex.Message}");
                throw;
            }
        }
    }
}
