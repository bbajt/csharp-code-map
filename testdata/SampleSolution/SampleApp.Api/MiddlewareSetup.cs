using Microsoft.AspNetCore.Builder;

namespace SampleApp.Api;

public static class MiddlewareSetup
{
    public static void Configure(WebApplication app)
    {
        // pos:1 — global error handler (must be first)
        app.UseExceptionHandler("/error");

        // pos:2
        app.UseHttpsRedirection();

        // pos:3 — serve static files before auth checks
        app.UseStaticFiles();

        // pos:4
        app.UseAuthentication();

        // pos:5
        app.UseAuthorization();

        // terminal — route to controllers
        app.MapControllers();
    }
}
