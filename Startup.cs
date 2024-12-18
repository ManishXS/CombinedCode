using Azure.Identity;
using Azure.Storage.Blobs;
using BackEnd.Entities;
using Microsoft.Azure.Cosmos;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace BackEnd
{
    public class Startup
    {
        private readonly IConfiguration _configuration;

        public Startup(IConfiguration configuration)
        {
            // Store the passed configuration for later use
            _configuration = configuration;
            Console.WriteLine("Startup constructor called.");
        }

        public void ConfigureServices(IServiceCollection services)
        {
            try
            {
                Console.WriteLine("Starting ConfigureServices...");

                // Connect to Azure App Configuration
                // NOTE: Replace the secret with your actual configuration or ensure this is managed via Key Vault
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

                // Retrieve required configuration settings
                var cosmosDbConnectionString = updatedConfiguration["CosmosDbConnectionString"];
                var blobConnectionString = updatedConfiguration["BlobConnectionString"];
                var apiKey = updatedConfiguration["ApiKey"];

                if (string.IsNullOrEmpty(cosmosDbConnectionString) ||
                    string.IsNullOrEmpty(blobConnectionString) ||
                    string.IsNullOrEmpty(apiKey))
                {
                    Console.WriteLine("Error: Missing CosmosDbConnectionString or BlobConnectionString or ApiKey.");
                    throw new Exception("Required configuration is missing. Check CosmosDbConnectionString, BlobConnectionString, and ApiKey.");
                }

                // Configure Cosmos DB client
                CosmosClientOptions clientOptions = new CosmosClientOptions
                {
                    ConnectionMode = ConnectionMode.Direct,
                    MaxRequestsPerTcpConnection = 10,
                    MaxTcpConnectionsPerEndpoint = 10
                };
                CosmosClient cosmosClient = new CosmosClient(cosmosDbConnectionString, clientOptions);
                services.AddSingleton(cosmosClient);
                services.AddScoped<CosmosDbContext>();

                // Configure Blob Service client
                services.AddSingleton(x => new BlobServiceClient(blobConnectionString));

                // Make the updated configuration available for DI
                services.AddSingleton<IConfiguration>(updatedConfiguration);

                // Increase multipart request size limit if needed
                services.Configure<FormOptions>(options =>
                {
                    // Set a large body length limit (200 MB)
                    options.MultipartBodyLengthLimit = 209715200;
                });

                // CORS configuration
                // We need to ensure that the Access-Control-Allow-Origin header matches the requesting origin.
                // Since we want to allow all origins for development and we do not need credentials, we can use AllowAnyOrigin.
                //
                // If you need credentials, remember you cannot use AllowAnyOrigin; you must specify exact origins.
                //
                // Below is a configuration that allows all origins, headers, and methods without credentials:
                //
                //services.AddCors(options =>
                //{
                //    options.AddPolicy("AllowSpecificOrigin", builder =>
                //    {
                //        builder.WithOrigins("https://tenxso.com")  
                //               .AllowAnyHeader()                    
                //               .AllowAnyMethod()                   
                //               .AllowCredentials();                
                //    });
                //});

                services.AddCors(options =>
                {
                    options.AddPolicy("AllowSpecificOrigin", builder =>
                    {
                        // Allow all origins, headers, and methods
                        // No credentials allowed with '*'
                        builder.AllowAnyOrigin()
                               .AllowAnyHeader()
                               .AllowAnyMethod();
                    });
                });

                // Add SignalR
                services.AddSignalR();

                // Add Controllers and Swagger
                services.AddControllers();
                services.AddSwaggerGen();

                Console.WriteLine("ConfigureServices completed successfully.");
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

                // If in Development, enable Swagger UI for easier testing
                if (env.IsDevelopment())
                {
                    app.UseDeveloperExceptionPage();
                    app.UseSwagger();
                    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "My API V1"));
                    logger.LogInformation("Development environment detected - Swagger UI enabled.");
                }

                // Enforce HTTPS redirection
                app.UseHttpsRedirection();

                // Add routing
                app.UseRouting();

                // Apply CORS policy before other middlewares that handle requests
                logger.LogInformation("Applying CORS policy...");
                app.UseCors("AllowSpecificOrigin");

                // Use custom middleware (if you have a middleware to skip authorization)
                logger.LogInformation("Applying custom middleware: SkipAuthorizationMiddleware");
                app.UseMiddleware<SkipAuthorizationMiddleware>();

                // Log incoming requests and outgoing responses
                app.Use(async (context, next) =>
                {
                    logger.LogInformation("Incoming Request: {Method} {Path}", context.Request.Method, context.Request.Path);
                    await next.Invoke();
                    logger.LogInformation("Response Status: {StatusCode}", context.Response.StatusCode);
                });

                // Use Authorization after CORS
                app.UseAuthorization();

                // Map controllers and hubs
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapControllers();

                    // Include the chat hub URL here
                    // Clients will connect to: https://<your-app-url>/chatHub
                    logger.LogInformation("Mapping SignalR Hub at /chatHub");
                    endpoints.MapHub<ChatHub>("/chatHub");
                });

                logger.LogInformation("Application configured successfully.");
                Console.WriteLine("Configure method completed successfully.");
            }
            catch (Exception ex)
            {
                logger.LogError($"Error in Configure: {ex.Message}");
                Console.WriteLine($"Error in Configure: {ex.Message}");
                throw;
            }
        }
    }
}
