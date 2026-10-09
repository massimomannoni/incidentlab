using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;

namespace IncidentLab.Gateway;

public static class GatewayApplication
{
    const string Scheme = "shared-login";
    public static WebApplication Build(string[] args, GatewayConfig? config = null, Action<WebApplicationBuilder>? configure = null)
    {
        config ??= GatewayConfig.FromEnvironment(); config.Validate();
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(o => { o.Limits.MaxRequestBodySize = 16_384; o.AddServerHeader = false; });
        builder.Logging.ClearProviders(); builder.Logging.AddJsonConsole();
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<SessionTickets>();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.Configure<FormOptions>(o => { o.ValueCountLimit = 8; o.ValueLengthLimit = 1024; o.KeyLengthLimit = 100; o.MultipartBodyLengthLimit = 8192; });
        builder.Services.AddAntiforgery(o => { o.Cookie.Name = "__Host-incidentlab-csrf"; o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict; o.HeaderName = "X-IncidentLab-CSRF"; });
        builder.Services.AddAuthentication(Scheme).AddCookie(Scheme, o =>
        {
            o.Cookie.Name = "__Host-incidentlab-session"; o.Cookie.HttpOnly = true; o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict; o.Cookie.Path = "/";
            o.ExpireTimeSpan = GatewayConfig.SessionDuration; o.SlidingExpiration = false; o.LoginPath = "/login";
            o.Events.OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/auth"))
                {
                    context.Response.StatusCode = 401; context.Response.Headers["X-IncidentLab-Login"] = "required";
                    return context.Response.WriteAsJsonAsync(new { detail = "Sign in again; sessions last one hour." });
                }
                context.Response.Redirect("/login"); return Task.CompletedTask;
            };
        });
        builder.Services.AddOptions<CookieAuthenticationOptions>(Scheme).Configure<SessionTickets, TimeProvider>((o, tickets, clock) =>
        { o.SessionStore = tickets; o.TimeProvider = clock; });
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            // One bounded partition for the whole public gateway: rotating IPs, cookies
            // or forwarded headers cannot multiply this origin-work allowance.
            // This limits application work, NOT already incurred CloudFront/WAF charges.
            o.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter("public", key =>
                    new FixedWindowRateLimiterOptions { PermitLimit = GatewayConfig.RequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })),
                PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetConcurrencyLimiter("public", key =>
                    new ConcurrencyLimiterOptions { PermitLimit = GatewayConfig.ConcurrentRequests, QueueLimit = 0 })));
            o.OnRejected = (context, _) => { context.HttpContext.Response.Headers.RetryAfter = "60"; return ValueTask.CompletedTask; };
            o.AddFixedWindowLimiter("login", limit => { limit.PermitLimit = 10; limit.Window = TimeSpan.FromMinutes(1); limit.QueueLimit = 0; });
        });
        builder.Services.AddHttpClient("monitor", c => c.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false });
        configure?.Invoke(builder);
        var app = builder.Build();
        // These checks happen before authentication and before trusting any proxy metadata.
        // CloudFront overrides the origin secret. WAF rejects forged CAPTCHA proof headers.
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.CacheControl = "no-store, private";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["X-Frame-Options"] = "DENY";
            // HTML form POSTs get Origin: null under no-referrer, breaking our exact-origin check.
            // Keep same-origin form provenance without sending referrers to external sites.
            ctx.Response.Headers["Referrer-Policy"] = "same-origin";
            ctx.Response.Headers["Strict-Transport-Security"] = "max-age=31536000";
            ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; font-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            if (!GatewayConfig.Equal(ctx.Request.Headers["X-IncidentLab-Origin"].ToString(), config.OriginKey) ||
                !string.Equals(ctx.Request.Host.Value, config.PublicHost, StringComparison.OrdinalIgnoreCase))
            { ctx.Response.StatusCode = 403; return; }
            // The distribution accepts HTTPS only; do not trust client-supplied X-Forwarded-*.
            ctx.Request.Scheme = "https";
            try
            {
                if (ctx.Request.ContentLength > 16_384) { ctx.Response.StatusCode = 413; return; }
                if (ctx.Request.Method is not ("GET" or "HEAD"))
                {
                    if (ctx.Request.Headers.Origin.ToString() != "https://" + config.PublicHost) { ctx.Response.StatusCode = 403; return; }
                }
                await next(ctx);
            }
            catch (AntiforgeryValidationException) { ctx.Response.StatusCode = 403; }
            catch (BadHttpRequestException ex) { ctx.Response.StatusCode = ex.StatusCode; }
            catch (InvalidDataException) { ctx.Response.StatusCode = 400; }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
            catch (Exception ex)
            {
                app.Logger.LogError("Gateway request failed: {ErrorType}", ex.GetType().Name);
                if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsJsonAsync(new { detail = "Public access temporarily unavailable" }); }
                else ctx.Abort();
            }
        });
        // Bound anonymous work before cookie validation or antiforgery body parsing.
        app.UseRateLimiter();
        // Antiforgery validates against the authenticated identity, not the anonymous proxy request.
        app.UseAuthentication();
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Method is not ("GET" or "HEAD"))
                await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx);
            await next(ctx);
        });
        app.MapGet("/login", (HttpContext ctx, IAntiforgery csrf) =>
        {
            if (ctx.Request.Headers["x-amzn-waf-captcha-verified"] != "true") return Results.StatusCode(403);
            return Results.Content(LoginPage(csrf.GetAndStoreTokens(ctx).RequestToken!, ctx.Request.Query.ContainsKey("failed"), config.Access), "text/html");
        });
        app.MapPost("/login", async (HttpContext ctx, TimeProvider clock) =>
        {
            if (ctx.Request.Headers["x-amzn-waf-captcha-verified"] != "true" || !ctx.Request.HasFormContentType) return Results.StatusCode(403);
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var username = form["username"].ToString(); var password = form["password"].ToString();
            var access = username.Length <= 64 && password.Length is >= 12 and <= 256 ? config.Authenticate(username, password) : null;
            if (access is null)
            {
                app.Logger.LogWarning("Shared login rejected");
                return Results.Redirect("/login?failed=1");
            }
            await ctx.SignOutAsync(Scheme);
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, access)], Scheme));
            await ctx.SignInAsync(Scheme, principal, new AuthenticationProperties { IsPersistent = true, AllowRefresh = false,
                IssuedUtc = clock.GetUtcNow(), ExpiresUtc = clock.GetUtcNow() + GatewayConfig.SessionDuration });
            app.Logger.LogInformation("Public session started; access {Access}; duration 3600 seconds", access);
            return Results.Redirect("/");
        }).RequireRateLimiting("login");
        app.Map("/{**path}", async (HttpContext ctx, IAntiforgery csrf, IHttpClientFactory clients) =>
        {
            if (ctx.User.Identity?.IsAuthenticated != true) { await ctx.ChallengeAsync(Scheme); return; }
            var access = ctx.User.FindFirstValue(ClaimTypes.Role) ?? "";
            if (ctx.Request.Path == "/auth/session" && ctx.Request.Method == "GET")
            {
                var auth = await ctx.AuthenticateAsync(Scheme);
                await ctx.Response.WriteAsJsonAsync(new { access, expires_at = auth.Properties!.ExpiresUtc,
                    csrf_token = csrf.GetAndStoreTokens(ctx).RequestToken }); return;
            }
            if (ctx.Request.Path == "/auth/logout" && ctx.Request.Method == "POST")
            { await ctx.SignOutAsync(Scheme); ctx.Response.Redirect("/login"); return; }
            if (!Allowed(ctx.Request.Path.Value ?? "", ctx.Request.Method, access))
            { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { detail = "This operation is not available through shared public access." }); return; }
            var target = new Uri(config.MonitorUrl.TrimEnd('/') + ctx.Request.Path + ctx.Request.QueryString);
            using var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);
            request.Headers.TryAddWithoutValidation("Accept", ctx.Request.Headers.Accept.ToString());
            // Narrow server-to-server credential, never supplied by or returned to the browser.
            // It exempts only simulation admission, not AI quotas, budgets or settings.
            if (access == "master" && config.MasterAdmissionKey.Length > 0 &&
                (ctx.Request.Method == "POST" && ctx.Request.Path == "/api/runs" || ctx.Request.Method == "GET" && ctx.Request.Path == "/api/limits"))
                request.Headers.Add("X-IncidentLab-Master-Admission", config.MasterAdmissionKey);
            if (ctx.Request.Method is not ("GET" or "HEAD"))
            {
                // Explicitly bound chunked bodies too, even on non-Kestrel test hosts.
                using var body = new MemoryStream(); var buffer = new byte[4096]; int read;
                while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
                { if (body.Length + read > 16_384) { ctx.Response.StatusCode = 413; return; } await body.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted); }
                request.Content = new ByteArrayContent(body.ToArray());
                request.Content.Headers.TryAddWithoutValidation("Content-Type", ctx.Request.ContentType);
            }
            // Do not forward browser cookies, authorization, lab keys, Host, Origin or proxy headers.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await clients.CreateClient("monitor").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is >= 300 and < 400) { ctx.Response.StatusCode = 502; return; }
            if (response.Content.Headers.ContentLength > GatewayConfig.MaximumResponseBytes) { ctx.Response.StatusCode = 502; return; }
            // Bound both known-length and chunked responses BEFORE writing to the browser.
            using var result = new MemoryStream(); var chunk = new byte[8192]; int count;
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            while ((count = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (result.Length + count > GatewayConfig.MaximumResponseBytes) { ctx.Response.StatusCode = 502; return; }
                await result.WriteAsync(chunk.AsMemory(0, count), timeout.Token);
            }
            ctx.Response.StatusCode = (int)response.StatusCode;
            ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            result.Position = 0; await result.CopyToAsync(ctx.Response.Body, timeout.Token);
        });
        return app;
    }

    public static bool Allowed(string path, string method, string access)
    {
        if (access is not ("viewer" or "master")) return false;
        if (method is "GET" or "HEAD")
            return path == "/" || Regex.IsMatch(path, @"^/assets/[A-Za-z0-9_.-]+\.(js|css|svg|png|woff2)$") ||
                new[] { "/api/health", "/api/overview", "/api/events", "/api/rules", "/api/scenarios", "/api/contracts", "/api/runs", "/api/limits", "/api/notices", "/api/settings/ai", "/api/agents", "/api/ai/catalog", "/api/ai/usage" }.Contains(path) ||
                Regex.IsMatch(path, @"^/api/incidents/[a-zA-Z0-9_-]+$");
        // Only the separate personal account can operate. Settings, ingestion and
        // worker routes stay private. The Monitor retains AI and load limits for all callers.
        return access == "master" && method == "POST" && (path is "/api/runs" or "/api/runs/stop" ||
            Regex.IsMatch(path, @"^/api/incidents/[a-zA-Z0-9_-]+/(ack|analyze)$"));
    }

    static string LoginPage(string token, bool failed, string access) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Sign in · IncidentLab</title>
        <style> *{box-sizing:border-box}body{margin:0;min-height:100vh;display:grid;place-items:center;font:15px system-ui,sans-serif;color:#223c47;background:radial-gradient(ellipse at top left,#d8eee8,transparent 60%),#f3f6f7}.card{background:#fff;border:1px solid #e0e9e8;border-radius:24px;padding:40px;max-width:440px;width:calc(100% - 32px);box-shadow:0 18px 70px #385d5810}.mark{background:#e5f3ed;color:#24745b;display:inline-block;border-radius:12px;padding:12px;font:600 15px monospace}h1{font-size:28px;letter-spacing:-1px;margin:22px 0 10px}p{color:#60757e;line-height:1.6}.meta{font:12px monospace;color:#407b67}label{display:block;font-weight:600;margin:20px 0 8px}input{font:inherit;border:1px solid #ccdada;border-radius:9px;padding:12px;width:100%}input:focus{outline:2px solid #75b8a2;outline-offset:2px}button{margin:24px 0 10px;width:100%;padding:13px;border:0;border-radius:9px;background:#22634e;color:white;font:600 15px system-ui;cursor:pointer}.error{color:#a62e36;background:#fff0ef;padding:10px;border-radius:8px}.fine{font-size:12px;margin-bottom:0}</style></head><body><main class="card">
        <div class="mark">[ iL ]</div><h1>Welcome to IncidentLab.</h1><p>A working laboratory for incident detection.<br>Sign in with your laboratory credentials.</p><div class="meta">HTTPS · CAPTCHA verified · 60-minute session</div>
        {{(failed ? "<p class=\"error\" role=\"alert\">Invalid credentials. Please try again.</p>" : "")}}
        <form method="post" action="/login"><input type="hidden" name="__RequestVerificationToken" value="{{HtmlEncoder.Default.Encode(token)}}"><label for="username">Username</label><input id="username" name="username" autocomplete="username" maxlength="64" required><label for="password">Password</label><input id="password" name="password" type="password" autocomplete="current-password" minlength="12" maxlength="256" required><button type="submit">Open the laboratory →</button></form>
        <p class="fine">Shared visitors have read-only access. Personal operators can run bounded tests; AI settings stay private. Sessions expire one hour after sign-in, even while you are active. Use synthetic data only.</p></main></body></html>
        """;
}
