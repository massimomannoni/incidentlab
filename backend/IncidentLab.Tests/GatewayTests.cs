using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IncidentLab.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IncidentLab.Tests;

public sealed class GatewayTests
{
    const string Password = "unit-test-password-not-a-real-secret";
    const string MasterPassword = "different-unit-test-master-password";
    static readonly string MasterHash = Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(MasterPassword, Convert.FromHexString(new string('c', 32)), GatewayConfig.PasswordIterations, HashAlgorithmName.SHA256, 32)).ToLowerInvariant();
    static readonly GatewayConfig Configuration = new() { PublicHost = "demo.example.test", Username = "visitor", OriginKey = new('a', 64),
        PasswordSalt = new('b', 32), PasswordHash = Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(Password, Convert.FromHexString(new string('b', 32)), GatewayConfig.PasswordIterations, HashAlgorithmName.SHA256, 32)).ToLowerInvariant() };
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    sealed class Monitor : HttpMessageHandler
    {
        public int Requests;
        public string? Admission;
        public TaskCompletionSource? Release;
        public readonly TaskCompletionSource AllEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ResponseBytes;
        public bool UnknownLength;
        sealed class NonSeekable(byte[] bytes) : MemoryStream(bytes)
        { public override bool CanSeek => false; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref Requests) == GatewayConfig.ConcurrentRequests) AllEntered.TrySetResult();
            Assert.False(request.Headers.Contains("Cookie")); Assert.False(request.Headers.Contains("X-Lab-Worker-Key"));
            Assert.False(request.Headers.Contains("Authorization")); Assert.Equal("monitor", request.RequestUri!.Host);
            Admission = request.Headers.TryGetValues(SimulationQuota.MasterHeader,out var values) ? values.Single() : null;
            if (Release != null) await Release.Task.WaitAsync(ct);
            HttpContent content = ResponseBytes > 0
                ? UnknownLength ? new StreamContent(new NonSeekable(new byte[ResponseBytes])) : new ByteArrayContent(new byte[ResponseBytes])
                : JsonContent.Create(new { live = true });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
    sealed class Fixture : IAsyncDisposable
    {
        public WebApplication App; public HttpClient Client; public CookieContainer Cookies = new(); public Clock Clock = new(); public Monitor Monitor = new();
        public Fixture(string access = "viewer", bool master = false)
        {
            App = GatewayApplication.Build([], Configuration with { Access = access, MasterUsername = master ? "master" : "",
                MasterPasswordSalt = master ? new('c', 32) : "", MasterPasswordHash = master ? MasterHash : "",
                MasterAdmissionKey = master ? new('d',64) : "" }, b =>
            {
                b.WebHost.UseTestServer(); b.Logging.ClearProviders(); b.Services.AddSingleton<TimeProvider>(Clock);
                b.Services.AddHttpClient("monitor").ConfigurePrimaryHttpMessageHandler(() => Monitor);
            });
            App.StartAsync().GetAwaiter().GetResult(); Client = App.GetTestClient(); Client.BaseAddress = new Uri("https://" + Configuration.PublicHost);
        }
        public async Task<HttpResponseMessage> Request(string path, string method = "GET", HttpContent? body = null, string? token = null, bool trusted = true, bool captcha = true, bool origin = true, bool cookie = true, string? originValue = null)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = body };
            if (trusted) request.Headers.Add("X-IncidentLab-Origin", Configuration.OriginKey);
            if (captcha) request.Headers.Add("x-amzn-waf-captcha-verified", "true");
            if (origin && method != "GET") request.Headers.Add("Origin", originValue ?? Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            if (token != null) request.Headers.Add("X-IncidentLab-CSRF", token);
            var cookieHeader = Cookies.GetCookieHeader(Client.BaseAddress!);
            if (cookie && cookieHeader.Length > 0) request.Headers.Add("Cookie", cookieHeader);
            request.Headers.Add("X-Lab-Worker-Key", "forged-browser-header");
            request.Headers.Add(SimulationQuota.MasterHeader, "forged-browser-admission");
            var response = await Client.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies)) foreach (var value in cookies) Cookies.SetCookies(Client.BaseAddress!, value);
            return response;
        }
        public async Task<string> LoginToken()
        {
            using var page = await Request("/login"); Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            return WebUtility.HtmlDecode(Regex.Match(await page.Content.ReadAsStringAsync(), "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
        }
        public async Task<HttpResponseMessage> Login(string password = Password, bool captcha = true, string username = "visitor")
        {
            var token = await LoginToken();
            return await Request("/login", "POST", new FormUrlEncodedContent(new Dictionary<string, string> {
                ["username"] = username, ["password"] = password, ["__RequestVerificationToken"] = token }), captcha: captcha);
        }
        public async Task<JsonObject> Session()
        {
            using var response = await Request("/auth/session"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.StopAsync(); await App.DisposeAsync(); }
    }

    [Fact] public async Task OriginAndCaptchaCannotBeBypassed()
    {
        await using var f = new Fixture();
        using var direct = await f.Request("/login", trusted: false); Assert.Equal(HttpStatusCode.Forbidden, direct.StatusCode);
        using var noCaptcha = await f.Request("/login", captcha: false); Assert.Equal(HttpStatusCode.Forbidden, noCaptcha.StatusCode);
        using var post = await f.Login(captcha: false); Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        using var api = await f.Request("/api/overview"); Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Equal(0, f.Monitor.Requests);
    }
    [Fact] public async Task SuccessfulSessionIsSecureAndExpiresAtExactlyOneHour()
    {
        await using var f = new Fixture(); using var login = await f.Login(); Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var header = login.Headers.GetValues("Set-Cookie").Last(x => x.StartsWith("__Host-incidentlab-session="));
        Assert.Contains("secure", header); Assert.Contains("httponly", header); Assert.Contains("samesite=strict", header); Assert.DoesNotContain("domain=", header);
        var session = await f.Session(); Assert.Equal("viewer", session["access"]!.GetValue<string>());
        Assert.InRange((session["expires_at"]!.GetValue<DateTimeOffset>() - f.Clock.Now).TotalSeconds, 3599, 3601);
        f.Clock.Now += TimeSpan.FromMinutes(59);
        using var before = await f.Request("/api/overview"); Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.False(before.Headers.Contains("Set-Cookie"));
        f.Clock.Now += TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1);
        using var expired = await f.Request("/api/overview"); Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }
    [Fact] public async Task HtmlFormsPreserveSameOriginWithoutLeakingExternalReferrers()
    {
        await using var f = new Fixture();
        using var page = await f.Request("/login");
        Assert.Equal("same-origin", Assert.Single(page.Headers.GetValues("Referrer-Policy")));
        Assert.Contains("<form method=\"post\" action=\"/login\">", await page.Content.ReadAsStringAsync());
        using var login = await f.Login(); Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        // The dashboard also submits logout using an HTML form, not fetch().
        using var dashboard = await f.Request("/");
        Assert.Equal("same-origin", Assert.Single(dashboard.Headers.GetValues("Referrer-Policy")));
    }
    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("https://untrusted.example")]
    public async Task LoginStillRejectsMissingOpaqueAndForeignOrigins(string? origin)
    {
        await using var f = new Fixture(); var token = await f.LoginToken();
        using var response = await f.Request("/login", "POST", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["username"] = Configuration.Username, ["password"] = Password, ["__RequestVerificationToken"] = token }),
            origin: origin is not null, originValue: origin);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        using var api = await f.Request("/api/overview"); Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
    }
    [Fact] public async Task InvalidPasswordAndMissingCsrfNeverIssueSession()
    {
        await using var f = new Fixture(); using var failed = await f.Login("wrong-test-password");
        Assert.Equal("/login?failed=1", failed.Headers.Location!.OriginalString);
        using var unprotected = await f.Request("/login", "POST", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "visitor", ["password"] = Password }));
        Assert.Equal(HttpStatusCode.Forbidden, unprotected.StatusCode);
        using var api = await f.Request("/api/overview"); Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
    }
    [Fact] public async Task ViewerCannotMutateAndWorkerOrIngestNeverBecomePublic()
    {
        await using var f = new Fixture(); using var login = await f.Login(); var token = (await f.Session())["csrf_token"]!.GetValue<string>();
        foreach (var path in new[] { "/api/runs", "/api/runs/stop", "/api/ingest", "/api/worker/claim", "/api/incidents/example/analyze", "/api/incidents/example/ack" })
        { using var response = await f.Request(path, "POST", JsonContent.Create(new { }), token); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); }
        using var settings = await f.Request("/api/settings/ai", "PUT", JsonContent.Create(new { enabled = true, daily_budget_usd = 100 }), token);
        Assert.Equal(HttpStatusCode.Forbidden, settings.StatusCode);
        Assert.Equal(0, f.Monitor.Requests);
    }
    [Fact] public void PublicAdministrationCannotBeEnabledByConfiguration()
    {
        foreach (var access in new[] { "admin", "tester", "owner", "", "VIEWER" })
        {
            Assert.Throws<ArgumentException>(() => (Configuration with { Access = access }).Validate());
            Assert.False(GatewayApplication.Allowed("/api/overview", "GET", access));
            Assert.False(GatewayApplication.Allowed("/api/settings/ai", "PUT", access));
            Assert.False(GatewayApplication.Allowed("/api/incidents/example/analyze", "POST", access));
        }
    }
    [Fact] public void MasterConfigurationMustBeCompleteAndSeparate()
    {
        Assert.Throws<ArgumentException>(() => (Configuration with { MasterUsername = "master" }).Validate());
        Assert.Throws<ArgumentException>(() => (Configuration with { MasterPasswordHash = MasterHash }).Validate());
        Assert.Throws<ArgumentException>(() => (Configuration with { MasterUsername = "VISITOR", MasterPasswordSalt = new('c', 32), MasterPasswordHash = MasterHash }).Validate());
        Assert.Throws<ArgumentException>(() => (Configuration with { Access = "master" }).Validate());
    }
    [Theory]
    [InlineData("master", Password)]
    [InlineData("visitor", MasterPassword)]
    public async Task CredentialsCannotCrossRoles(string username, string password)
    {
        await using var f = new Fixture(master: true);
        using var login = await f.Login(password, username: username);
        Assert.Equal("/login?failed=1", login.Headers.Location!.OriginalString);
        using var session = await f.Request("/auth/session"); Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
    }
    [Fact] public async Task MasterCanOperateButNotChangeBudgetsOrReachWorkerRoutes()
    {
        await using var f = new Fixture(master: true); using var login = await f.Login(MasterPassword, username: "master");
        var session = await f.Session(); Assert.Equal("master", session.S("access")); var token = session.S("csrf_token");
        foreach (var path in new[] { "/api/runs", "/api/runs/stop", "/api/incidents/example/ack", "/api/incidents/example/analyze" })
        { using var response = await f.Request(path, "POST", JsonContent.Create(new { }), token); Assert.Equal(HttpStatusCode.OK, response.StatusCode); }
        foreach (var path in new[] { "/api/ingest", "/api/worker/claim", "/api/runs/arbitrary", "/api/settings/ai" })
        { using var response = await f.Request(path, "POST", JsonContent.Create(new { }), token); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); }
        using var settings = await f.Request("/api/settings/ai", "PUT", JsonContent.Create(new { enabled = true }), token);
        Assert.Equal(HttpStatusCode.Forbidden, settings.StatusCode); Assert.Equal(4, f.Monitor.Requests);
        f.Clock.Now += TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1);
        using var expired = await f.Request("/api/limits"); Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }
    [Fact] public async Task MasterAdmissionIsServerIssuedAndNeverForwardedForOtherRoutesOrVisitors()
    {
        await using var f=new Fixture(master:true); using var login=await f.Login(MasterPassword,username:"master");
        var session=await f.Session(); Assert.DoesNotContain(new string('d',64),session.ToJsonString());
        var token=session.S("csrf_token");
        using(var limits=await f.Request("/api/limits")) Assert.Equal(new string('d',64),f.Monitor.Admission);
        using(var run=await f.Request("/api/runs","POST",JsonContent.Create(new{}),token)) Assert.Equal(new string('d',64),f.Monitor.Admission);
        using(var ai=await f.Request("/api/incidents/example/analyze","POST",JsonContent.Create(new{}),token)) Assert.Null(f.Monitor.Admission);
        using(var history=await f.Request("/api/runs")) Assert.Null(f.Monitor.Admission);
        using var visitor=await f.Login();
        using(var limits=await f.Request("/api/limits")) Assert.Null(f.Monitor.Admission);
        using(var forbidden=await f.Request("/api/runs","POST",JsonContent.Create(new{}),(await f.Session()).S("csrf_token"))) Assert.Equal(HttpStatusCode.Forbidden,forbidden.StatusCode);
    }
    [Fact] public async Task MasterStillNeedsCaptchaCsrfAndExactOrigin()
    {
        await using var f = new Fixture(master: true);
        using var rejected = await f.Login(MasterPassword, captcha: false, username: "master"); Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        using var login = await f.Login(MasterPassword, username: "master"); var token = (await f.Session()).S("csrf_token");
        using var noCsrf = await f.Request("/api/runs", "POST", JsonContent.Create(new { })); Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
        using var wrongOrigin = await f.Request("/api/runs", "POST", JsonContent.Create(new { }), token, originValue: "https://elsewhere.example");
        Assert.Equal(HttpStatusCode.Forbidden, wrongOrigin.StatusCode); Assert.Equal(0, f.Monitor.Requests);
    }
    [Fact] public async Task AddingMasterNeverElevatesVisitor()
    {
        await using var f = new Fixture(master: true); using var login = await f.Login(); var session = await f.Session();
        Assert.Equal("viewer", session.S("access"));
        using var response = await f.Request("/api/runs", "POST", JsonContent.Create(new { access = "master" }), session.S("csrf_token"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.Equal(0, f.Monitor.Requests);
    }
    [Fact] public async Task GlobalRequestAllowanceAlsoBoundsAnonymousRequests()
    {
        await using var f = new Fixture();
        for (var i = 0; i < GatewayConfig.RequestsPerMinute; i++)
        { using var response = await f.Request("/api/overview", cookie: false); Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); }
        using var blocked = await f.Request("/api/overview", cookie: false);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal("60", Assert.Single(blocked.Headers.GetValues("Retry-After")));
        Assert.Equal(0, f.Monitor.Requests);
    }
    [Fact] public async Task GlobalConcurrencyDoesNotQueueMoreMonitorWork()
    {
        await using var f = new Fixture(); using var login = await f.Login();
        f.Monitor.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Enumerable.Range(0, GatewayConfig.ConcurrentRequests).Select(_ => f.Request("/api/overview")).ToArray();
        try
        {
            await f.Monitor.AllEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var blocked = await f.Request("/api/overview"); Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
            Assert.Equal(GatewayConfig.ConcurrentRequests, f.Monitor.Requests);
        }
        finally { f.Monitor.Release.TrySetResult(); foreach (var response in await Task.WhenAll(pending)) response.Dispose(); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedMonitorResponsesAreRejectedBeforeSendingData(bool unknownLength)
    {
        await using var f = new Fixture(); using var login = await f.Login();
        f.Monitor.ResponseBytes = GatewayConfig.MaximumResponseBytes + 1; f.Monitor.UnknownLength = unknownLength;
        using var response = await f.Request("/api/overview");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }
    [Fact] public async Task LogoutRevokesEvenCopiedSessionCookie()
    {
        await using var f = new Fixture(); using var login = await f.Login(); var token = (await f.Session())["csrf_token"]!.GetValue<string>();
        var copy = f.Cookies.GetCookieHeader(f.Client.BaseAddress!);
        using var logout = await f.Request("/auth/logout", "POST", token: token); Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        f.Cookies = new CookieContainer(); f.Cookies.SetCookies(f.Client.BaseAddress!, copy.Replace("; ", ", "));
        using var replay = await f.Request("/api/overview"); Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }
    [Fact] public async Task LoginAttemptsAreBounded()
    {
        await using var f = new Fixture();
        for (var i = 0; i < 10; i++) { using var failed = await f.Login("wrong-test-password"); Assert.Equal(HttpStatusCode.Redirect, failed.StatusCode); }
        using var limited = await f.Login(); Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }
    [Theory]
    [InlineData("/api/worker/claim", "POST")]
    [InlineData("/api/ingest", "POST")]
    [InlineData("//other.example/", "GET")]
    [InlineData("/assets/../../api/worker", "GET")]
    [InlineData("/api/settings/ai", "DELETE")]
    public void NoOpenProxyOrImplicitPrivilegedRoutes(string path, string method) => Assert.False(GatewayApplication.Allowed(path, method, "viewer"));
}
