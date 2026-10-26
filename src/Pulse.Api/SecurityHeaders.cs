namespace Pulse.Api;

internal static class SecurityHeaders
{
    /// <summary>Headers that make browsers stricter about what the API's responses may do.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cache-Control"] = "no-store";
            await next(context);
        });
}
