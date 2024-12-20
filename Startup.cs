using Azure.Storage.Blobs;
using BackEnd.Entities;
using Microsoft.Azure.Cosmos;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using tusdotnet;
using tusdotnet.Models;
using tusdotnet.Models.Configuration;
using tusdotnet.Stores;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace BackEnd
{
    public class Startup
    {
        private readonly IConfiguration _configuration;

        public Startup(IConfiguration configuration)
        {
            _configuration = configuration;
            Console.WriteLine("Startup constructor called.");
        }

        public void ConfigureServices(IServiceCollection services)
        {
            try
            {
                Console.WriteLine("Starting ConfigureServices...");

                var appConfigConnectionString = "Endpoint=https://azurermtenx.azconfig.io;" +
                                                "Id=8FPB;" +
                                                "Secret=3NCoPOSo0Y1ykrX6ih9ObYVbY2ZA6RLqaXyMyBI04eB5k4wkhpA5JQQJ99AKACGhslBY0DYHAAACAZAC1woJ";

                var updatedConfiguration = new ConfigurationBuilder()
                    .AddConfiguration(_configuration)
                    .AddAzureAppConfiguration(options =>
                    {
                        options.Connect(appConfigConnectionString);
                    })
                    .Build();

                var cosmosDbConnectionString = updatedConfiguration["CosmosDbConnectionString"];
                var blobConnectionString = updatedConfiguration["BlobConnectionString"];
                var apiKey = updatedConfiguration["ApiKey"];

                if (string.IsNullOrEmpty(cosmosDbConnectionString) ||
                    string.IsNullOrEmpty(blobConnectionString) ||
                    string.IsNullOrEmpty(apiKey))
                {
                    Console.WriteLine("Error: Missing CosmosDbConnectionString or BlobConnectionString or ApiKey.");
                    throw new Exception("Required configuration is missing.");
                }

                CosmosClientOptions clientOptions = new CosmosClientOptions
                {
                    ConnectionMode = ConnectionMode.Direct,
                    MaxRequestsPerTcpConnection = 10,
                    MaxTcpConnectionsPerEndpoint = 10
                };
                CosmosClient cosmosClient = new CosmosClient(cosmosDbConnectionString, clientOptions);
                services.AddSingleton(cosmosClient);
                services.AddScoped<CosmosDbContext>();

                services.AddSingleton(new BlobServiceClient(blobConnectionString));
                services.AddSingleton<IConfiguration>(updatedConfiguration);

                services.Configure<FormOptions>(options =>
                {
                    options.MultipartBodyLengthLimit = 500 * 1024 * 1024;
                });

                services.Configure<IISServerOptions>(options =>
                {
                    options.MaxRequestBodySize = 500 * 1024 * 1024;
                });

                services.Configure<KestrelServerOptions>(options =>
                {
                    options.Limits.MaxRequestBodySize = 500 * 1024 * 1024;
                    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(10);
                    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
                });

                services.AddCors(options =>
                {
                    options.AddPolicy("AllowSpecificOrigin", builder =>
                    {
                        builder.AllowAnyOrigin()
                               .AllowAnyHeader()
                               .AllowAnyMethod();
                    });
                });

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

                if (env.IsDevelopment())
                {
                    app.UseDeveloperExceptionPage();
                    app.UseSwagger();
                    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "My API V1"));
                    logger.LogInformation("Development environment detected - Swagger UI enabled.");
                }

                app.UseHttpsRedirection();
                app.UseRouting();
                app.UseCors("AllowSpecificOrigin");

                app.UseTus(httpContext => new DefaultTusConfiguration
                {
                    Store = new TusDiskStore(Path.Combine(env.ContentRootPath, "uploads")),
                    UrlPath = "/files",
                    MaxAllowedUploadSizeInBytes = 500 * 1024 * 1024,
                    Events = new Events
                    {
                        OnFileCompleteAsync = async ctx =>
                        {
                            var fileId = ctx.FileId;
                            var filePath = Path.Combine(env.ContentRootPath, "uploads", fileId);
                            logger.LogInformation($"File {fileId} has been fully uploaded. Path: {filePath}");
                        }
                    }
                });

                app.UseMiddleware<SkipAuthorizationMiddleware>();

                app.Use(async (context, next) =>
                {
                    logger.LogInformation("Incoming Request: {Method} {Path}", context.Request.Method, context.Request.Path);
                    await next.Invoke();
                    logger.LogInformation("Response Status: {StatusCode}", context.Response.StatusCode);
                });

                app.UseAuthorization();

                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapControllers();
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
