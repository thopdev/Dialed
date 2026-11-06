using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PasskeySpike;
using PasskeySpike.Storage;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("cosmos")
    ?? throw new InvalidOperationException("ConnectionStrings:cosmos is not set.");
var cosmosClient = IdentityCosmos.CreateClient(connectionString);
builder.Services.AddSingleton(cosmosClient);
builder.Services.AddSingleton<IdentityCosmos>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.Configure<PasskeyOptions>(builder.Configuration.GetSection("Passkeys"));
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.AddSingleton<OriginPolicy>();
builder.Services.AddSingleton<TokenIssuer>();
builder.Services.AddScoped<PasskeyCeremonies>();

// In Azure the key ring must be shared and persisted (Blob Storage + Key Vault), or ceremony state
// minted by one replica cannot be opened by another.
builder.Services.AddDataProtection();

builder.Services
    .AddIdentityCore<SpikeUser>(options => options.User.RequireUniqueEmail = true)
    .AddUserStore<CosmosUserStore>();

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = TokenIssuer.SigningKey(jwt),
            NameClaimType = JwtRegisteredClaimNames.Name,
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddCors();
builder.Services.AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>()
    .Configure<OriginPolicy>((cors, origins) => cors.AddDefaultPolicy(policy => policy
        .SetIsOriginAllowed(origins.IsAllowed)
        .AllowAnyHeader()
        .AllowAnyMethod()));

var app = builder.Build();

await IdentityCosmos.EnsureContainerAsync(cosmosClient);

app.UseDefaultFiles();
app.UseStaticFiles(); // wwwroot/index.html: manual test page for Windows Hello / iPhone via the dev tunnel.
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

var auth = app.MapGroup("/auth");
auth.MapPost("/signup/options", (HttpContext http, SignUpOptionsRequest request, PasskeyCeremonies ceremonies) => ceremonies.SignUpOptionsAsync(http, request));
auth.MapPost("/signup", (HttpContext http, CeremonyCompleteRequest request, PasskeyCeremonies ceremonies) => ceremonies.SignUpAsync(http, request));
auth.MapPost("/signin/options", (HttpContext http, PasskeyCeremonies ceremonies) => ceremonies.SignInOptionsAsync(http));
auth.MapPost("/signin", (HttpContext http, CeremonyCompleteRequest request, PasskeyCeremonies ceremonies) => ceremonies.SignInAsync(http, request));

app.MapGet("/me", async (HttpContext http, UserManager<SpikeUser> users) =>
{
    var user = await users.FindByIdAsync(http.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? "");
    return user is null
        ? Results.NotFound()
        : Results.Ok(new { user.Id, user.Email, user.DisplayName, Passkeys = user.Passkeys.Select(p => new { p.Name, p.CreatedAt, p.LastUsedAt, p.SignCount, p.IsBackedUp }) });
}).RequireAuthorization();

app.Run();

public partial class Program;
