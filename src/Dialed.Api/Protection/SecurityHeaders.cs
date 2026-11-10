namespace Dialed.Api.Protection;

public static class SecurityHeaders
{
    /// <summary>
    /// Headers every API answer carries: no content sniffing, no referrer, HTTPS only (HSTS) and, for everything that is about one person, no caching.
    /// The API only returns JSON, so it also forbids being framed or running anything.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            var options = context.RequestServices.GetRequiredService<ProtectionOptions>();
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
                if (options.Hsts)
                {
                    headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
                }

                var path = context.Request.Path;
                // Photo files from the local stand-in for Blob Storage set their own (private) caching.
                var photoFile = path.StartsWithSegments(Photos.LocalSignedPhotoUrls.PathPrefix) && headers.CacheControl.Count > 0;
                if (!(path == "/" || path.StartsWithSegments("/health") || path.StartsWithSegments("/alive") || photoFile))
                {
                    headers["Cache-Control"] = "no-store";
                }

                return Task.CompletedTask;
            });
            return next();
        });
}
