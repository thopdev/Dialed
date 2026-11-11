using Dialed.Api.Account;
using Dialed.Api.Auth;
using Dialed.Api.Configuration;
using Dialed.Api.Ai;
using Dialed.Api.Friends;
using Dialed.Api.Households;
using Dialed.Api.Passkeys;
using Dialed.Api.Photos;
using Dialed.Api.Protection;
using Dialed.Api.PublicProfiles;
using Dialed.Api.Seeding;
using Dialed.Api.Sync;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// The PWA is served from another origin (localhost port, dev tunnel, Static Web Apps).
var allowedOrigins = AllowedOrigins.From(builder.Configuration);
var originPatterns = AllowedOrigins.Patterns(builder.Configuration);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .SetIsOriginAllowed(origin => AllowedOrigins.IsAllowed(origin, allowedOrigins, originPatterns))
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddDialedProtection(builder.Configuration);
builder.Services.AddDialedAuth(builder.Configuration, builder.Environment);
builder.Services.AddDialedPasskeys(builder.Configuration);
builder.Services.AddDialedSync();
builder.Services.AddDialedPhotoProcessing();
builder.Services.AddDialedHouseholds(builder.Environment);
builder.Services.AddDialedPublicProfiles(builder.Configuration);
builder.Services.AddDialedFriends();
builder.Services.AddDialedAi(builder.Configuration);
builder.Services.AddDialedBetaInvites(builder.Configuration);
builder.Services.AddDialedAccount();
builder.Services.AddDialedPhotos(builder.Configuration, builder.Environment);
builder.Services.AddDevelopmentSeeding(builder.Configuration);

var app = builder.Build();

app.UseCorrelationId();
app.UseExceptionHandler(); // unexpected errors become a ProblemDetails with the correlation id, never a stack trace
app.UseSecurityHeaders();
app.UseCors();
app.UseRequestSizeLimit();
app.UseAuthentication();
app.UseRateLimiter(); // after authentication: limits are per signed-in user
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapGet("/", () => "Dialed API");
app.MapDialedAuth(app.Environment);
app.MapDialedPasskeys();
app.MapDialedSync();
app.MapDialedHouseholds();
app.MapDialedFriends();
app.MapDialedPublicProfiles();
app.MapDialedAi();
app.MapDialedBetaInvites();
app.MapDialedAccount();
app.MapDialedPhotos();

app.Run();
