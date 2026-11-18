using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using ReportsApi.ClaimsTransformations;
using ReportsApi.Models;
using ReportsApi.Repositories;
using ReportsApi.Utils;
using System.Security.Claims;
using Minio;
using ReportsApi.Options;

// keycloak
var keycloakUrl = EnvironmentUtils.GetEnvironmentVariableOrThrow("KEYCLOAK_URL");
var keycloakInternalUrl = EnvironmentUtils.GetEnvironmentVariableOrThrow("KEYCLOAK_INTERNAL_URL");
var keycloakRealm = EnvironmentUtils.GetEnvironmentVariableOrThrow("KEYCLOAK_REALM");
// clickhouse
var clHost = EnvironmentUtils.GetEnvironmentVariableOrThrow("CLICKHOUSE_HOST");
var clPort = EnvironmentUtils.GetEnvironmentVariableOrThrow("CLICKHOUSE_PORT");
var clUser = EnvironmentUtils.GetEnvironmentVariableOrThrow("CLICKHOUSE_USER");
var clPassword = EnvironmentUtils.GetEnvironmentVariableOrThrow("CLICKHOUSE_PASSWORD");
var clDb = EnvironmentUtils.GetEnvironmentVariableOrThrow("CLICKHOUSE_DB");
// cdn
var cdnUrl = EnvironmentUtils.GetEnvironmentVariableOrThrow("CDN_URL");
// s3
var minioUrl = EnvironmentUtils.GetEnvironmentVariableOrThrow("MINIO_URL");
var minioAccessKey = EnvironmentUtils.GetEnvironmentVariableOrThrow("MINIO_ACCESS_KEY");
var minioSecretKey = EnvironmentUtils.GetEnvironmentVariableOrThrow("MINIO_SECRET_KEY");
var minioBucket = EnvironmentUtils.GetEnvironmentVariableOrThrow("MINIO_BUCKET");
var minioRegion = EnvironmentUtils.GetEnvironmentVariableOrThrow("MINIO_REGION");
var objectkeySecretKey = EnvironmentUtils.GetEnvironmentVariableOrThrow("OBJECTKEY_SECRET_KEY");

var cdnUri = new Uri(cdnUrl);
var minioUri = new Uri(minioUrl);

var builder = WebApplication.CreateBuilder(args);

builder.Services                                                  //
       .AddAuthentication(JwtBearerDefaults.AuthenticationScheme) //
       .AddJwtBearer(options => {
           options.MetadataAddress = UriUtils.CombineUriPath(keycloakInternalUrl, $"realms/{keycloakRealm}/.well-known/openid-configuration");
           options.RequireHttpsMetadata = false;

           options.MapInboundClaims = false;

           options.TokenValidationParameters = new TokenValidationParameters {
               ValidateAudience = true,
               ValidAudience = "reports-api",

               ValidateIssuer = true,
               ValidIssuer = UriUtils.CombineUriPath(keycloakUrl, $"realms/{keycloakRealm}")
           };

#if DEBUG_AUTH
           options.IncludeErrorDetails = true;

           options.Events = new JwtBearerEvents {
               OnAuthenticationFailed = context => {
                   var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                   logger.LogError(context.Exception, $"OnAuthenticationFailed: JWT auth error: {context.Exception.Message}");
                   return Task.CompletedTask;
               },
               OnChallenge = context => {
                   var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                   logger.LogWarning($"OnChallenge: Error={context.Error}, Description={context.ErrorDescription}");
                   return Task.CompletedTask;
               }
           };
#endif
       });

builder.Services.AddAuthorization();

builder.Services.AddTransient<IClaimsTransformation, KeycloakClaimsTransformation>();
builder.Services.AddSingleton(new ClickhouseConStringBuilder(clHost, clPort, clUser, clPassword, clDb));
builder.Services.AddTransient<ClickhouseReportStore, ClickhouseReportStore>();
builder.Services.AddTransient<S3ReportStore, S3ReportStore>();
builder.Services.AddTransient<ReportRepository, ReportRepository>();

builder.Services.Configure<S3Options>(options => {
    options.MinioUrl = minioUrl;
    options.AccessKey = minioAccessKey;
    options.SecretKey = minioSecretKey;
    options.Bucket = minioBucket;
    options.Region = minioRegion;
    options.ObjectKeySecretKey = Convert.FromHexString(objectkeySecretKey);
});
builder.Services.AddMinio(client => {
    client.WithEndpoint(GetHostAndPort(minioUri))          //
          .WithCredentials(minioAccessKey, minioSecretKey) //
          .WithRegion(minioRegion)                         //
          .WithSSL(minioUri.Scheme == Uri.UriSchemeHttps)  //
          .Build();
});
builder.Services.AddKeyedSingleton<IMinioClient>("cdn-presigner", (_, _) => {
    return new MinioClient()                                //
           .WithEndpoint(GetHostAndPort(cdnUri))            //
           .WithCredentials(minioAccessKey, minioSecretKey) //
           .WithRegion(minioRegion)                         //
           .WithSSL(cdnUri.Scheme == Uri.UriSchemeHttps)    //
           .Build();
});

#if DEBUG_AUTH
Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = true;
#endif

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/reports", async ([FromQuery] string? date, ClaimsPrincipal user, ReportRepository repo) => {
    var ownerId = user.FindFirst(v => v.Type == "sub");
    if (ownerId == null || string.IsNullOrWhiteSpace(ownerId.Value)) {
        return Results.Forbid();
    }
    if (!DateTimeOffset.TryParse(date, out var reportDate)) {
        return Results.Problem("Дата отчёта не указана", statusCode: StatusCodes.Status400BadRequest);
    }
    var reportUri = await repo.GetReportUriAsync(ownerId.Value, reportDate.UtcDateTime.Date);
    if (reportUri == null) {
        return Results.NotFound();
    }
    return Results.Ok(new ReportUriDto { Uri = reportUri });
}).RequireAuthorization(v => v.RequireRole("prothetic_user"));

app.Run();

static string GetHostAndPort(Uri uri) {
    var defaultPort = uri.Scheme == Uri.UriSchemeHttps ? 443 : 80;
    if (uri.Port == defaultPort) {
        return uri.Host;
    } else {
        return $"{uri.Host}:{uri.Port}";
    }
}