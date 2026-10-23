using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClipForge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Tests.Integration;

public class AuthTests : TestBase
{
    public AuthTests(ApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Register_login_me_refresh_logout_flow()
    {
        var client = CreateClient();
        var email = NextEmail("auth");

        var register = await client.PostAsJsonAsync("/api/auth/register", new { email, password = "Password123", displayName = "Ana", acceptedTerms = true }, Json);
        register.StatusCode.ShouldBe(HttpStatusCode.Created, await register.Content.ReadAsStringAsync());
        var cookies = register.Headers.GetValues("Set-Cookie").ToList();
        cookies.ShouldContain(c => c.StartsWith("cf_access=") && c.Contains("httponly", StringComparison.OrdinalIgnoreCase) && c.Contains("path=/", StringComparison.OrdinalIgnoreCase));
        cookies.ShouldContain(c => c.StartsWith("cf_refresh=") && c.Contains("path=/api/auth", StringComparison.OrdinalIgnoreCase));
        var registered = await ReadJsonAsync(register);
        registered.GetProperty("user").GetProperty("email").GetString().ShouldBe(email);
        registered.GetProperty("user").GetProperty("displayName").GetString().ShouldBe("Ana");

        // registration side effects: profile, free subscription, welcome notification, verification e-mail
        var me = await client.GetAsync("/api/auth/me");
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        var meBody = await ReadJsonAsync(me);
        meBody.GetProperty("subscription").GetProperty("planCode").GetString().ShouldBe("free");
        meBody.GetProperty("subscription").GetProperty("status").GetString().ShouldBe("active");
        meBody.GetProperty("limits").GetProperty("monthlyProcessingMinutes").GetInt32().ShouldBe(60);
        meBody.GetProperty("profile").GetProperty("locale").GetString().ShouldBe("pt-BR");
        Factory.Emails.LastTo(email).ShouldNotBeNull().Subject.ShouldContain("confirme seu e-mail");
        var unread = await ReadJsonAsync(await client.GetAsync("/api/notifications/unread-count"));
        unread.GetProperty("count").GetInt32().ShouldBe(1);

        // duplicate e-mail
        var duplicate = await CreateClient().PostAsJsonAsync("/api/auth/register", new { email, password = "Password123", displayName = "Ana", acceptedTerms = true }, Json);
        await ShouldBeProblemAsync(duplicate, HttpStatusCode.Conflict, "email_taken");

        // login with a fresh client
        var client2 = CreateClient();
        var login = await client2.PostAsJsonAsync("/api/auth/login", new { email, password = "Password123" }, Json);
        login.StatusCode.ShouldBe(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        (await client2.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // refresh rotates cookies
        var refresh = await client2.PostAsync("/api/auth/refresh", null);
        refresh.StatusCode.ShouldBe(HttpStatusCode.OK, await refresh.Content.ReadAsStringAsync());
        refresh.Headers.GetValues("Set-Cookie").ShouldContain(c => c.StartsWith("cf_refresh="));
        (await client2.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // logout clears cookies and revokes the refresh token
        var logout = await client2.PostAsync("/api/auth/logout", null);
        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client2.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await ShouldBeProblemAsync(await client2.PostAsync("/api/auth/refresh", null), HttpStatusCode.Unauthorized, "unauthorized");

        // bearer header also works
        var bearerClient = CreateClient();
        var accessToken = cookies.First(c => c.StartsWith("cf_access=")).Split(';')[0]["cf_access=".Length..];
        bearerClient.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);
        (await bearerClient.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Wrong_password_returns_401_and_lockout_after_repeated_failures_returns_423()
    {
        var (_, _, email) = await RegisterAsync();
        var client = CreateClient();

        var wrong = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "WrongPass1" }, Json);
        await ShouldBeProblemAsync(wrong, HttpStatusCode.Unauthorized, "invalid_credentials");

        HttpResponseMessage? last = null;
        for (var i = 0; i < 6; i++)
        {
            last = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "WrongPass1" }, Json);
        }
        await ShouldBeProblemAsync(last!, HttpStatusCode.Locked, "locked_out");

        // even the right password is refused while locked
        var right = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Password123" }, Json);
        await ShouldBeProblemAsync(right, HttpStatusCode.Locked, "locked_out");
    }

    [Fact]
    public async Task Validation_errors_are_problem_details_with_field_errors()
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register", new { email = "not-an-email", password = "short", displayName = "", acceptedTerms = false }, Json);
        await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        var body = await ReadJsonAsync(response);
        var errors = body.GetProperty("errors");
        errors.TryGetProperty("email", out _).ShouldBeTrue();
        errors.TryGetProperty("password", out _).ShouldBeTrue();
        errors.TryGetProperty("displayName", out _).ShouldBeTrue();
        errors.TryGetProperty("acceptedTerms", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Forgot_and_reset_password_using_the_emailed_token()
    {
        var (_, _, email) = await RegisterAsync();
        var client = CreateClient();

        var forgot = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email }, Json);
        forgot.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        // unknown e-mail also answers 202
        (await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "nobody@example.com" }, Json)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var message = Factory.Emails.LastTo(email).ShouldNotBeNull();
        message.Subject.ShouldContain("redefini");
        var match = Regex.Match(message.TextBody, @"/reset-password\?email=([^&\s]+)&token=([^\s]+)");
        match.Success.ShouldBeTrue(message.TextBody);
        var token = Uri.UnescapeDataString(match.Groups[2].Value);

        var badToken = await client.PostAsJsonAsync("/api/auth/reset-password", new { email, token = "garbage", newPassword = "NewPassword456" }, Json);
        await ShouldBeProblemAsync(badToken, HttpStatusCode.BadRequest, "invalid_token");

        var reset = await client.PostAsJsonAsync("/api/auth/reset-password", new { email, token, newPassword = "NewPassword456" }, Json);
        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent, await reset.Content.ReadAsStringAsync());

        await ShouldBeProblemAsync(await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Password123" }, Json), HttpStatusCode.Unauthorized, "invalid_credentials");
        (await client.PostAsJsonAsync("/api/auth/login", new { email, password = "NewPassword456" }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Verify_email_with_the_emailed_token()
    {
        var (client, userId, email) = await RegisterAsync();
        var message = Factory.Emails.LastTo(email).ShouldNotBeNull();
        var match = Regex.Match(message.TextBody, @"/verify-email\?userId=([^&\s]+)&token=([^\s]+)");
        match.Success.ShouldBeTrue(message.TextBody);
        var token = Uri.UnescapeDataString(match.Groups[2].Value);

        var verify = await client.PostAsJsonAsync("/api/auth/verify-email", new { userId, token }, Json);
        verify.StatusCode.ShouldBe(HttpStatusCode.NoContent, await verify.Content.ReadAsStringAsync());
        var me = await ReadJsonAsync(await client.GetAsync("/api/auth/me"));
        me.GetProperty("user").GetProperty("emailConfirmed").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Change_password_revokes_other_sessions_and_keeps_the_current_one()
    {
        var (client, _, email) = await RegisterAsync();
        var other = CreateClient();
        (await other.PostAsJsonAsync("/api/auth/login", new { email, password = "Password123" }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var wrong = await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = "Nope12345", newPassword = "Another123" }, Json);
        await ShouldBeProblemAsync(wrong, HttpStatusCode.Unauthorized, "invalid_credentials");

        var change = await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = "Password123", newPassword = "Another123" }, Json);
        change.StatusCode.ShouldBe(HttpStatusCode.NoContent, await change.Content.ReadAsStringAsync());

        // the other session cannot refresh anymore; the current one can
        await ShouldBeProblemAsync(await other.PostAsync("/api/auth/refresh", null), HttpStatusCode.Unauthorized, "unauthorized");
        (await client.PostAsync("/api/auth/refresh", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = "Another123" }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_tokens_are_stored_hashed_and_rotated()
    {
        var (client, userId, _) = await RegisterAsync();
        (await client.PostAsync("/api/auth/refresh", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var tokens = await WithDbAsync(db => db.RefreshTokens.Where(t => t.UserId == userId).OrderBy(t => t.CreatedAt).ToListAsync());
        tokens.Count.ShouldBe(2);
        tokens[0].RevokedAt.ShouldNotBeNull();
        tokens[0].ReplacedByTokenHash.ShouldBe(tokens[1].TokenHash);
        tokens[1].RevokedAt.ShouldBeNull();
        tokens.ShouldAllBe(t => t.TokenHash.Length == 64 && Regex.IsMatch(t.TokenHash, "^[0-9a-f]+$"));
    }

    [Fact]
    public async Task Account_delete_requires_password_and_revokes_sessions()
    {
        var (client, userId, email) = await RegisterAsync();
        using (var wrong = new HttpRequestMessage(HttpMethod.Delete, "/api/account/") { Content = JsonContent.Create(new { password = "wrong" }, options: Json) })
        {
            await ShouldBeProblemAsync(await client.SendAsync(wrong), HttpStatusCode.Unauthorized, "invalid_credentials");
        }
        using (var request = new HttpRequestMessage(HttpMethod.Delete, "/api/account/") { Content = JsonContent.Create(new { password = "Password123" }, options: Json) })
        {
            var response = await client.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        }
        var user = await WithDbAsync(db => db.Users.FirstAsync(u => u.Id == userId));
        user.DeletionRequestedAt.ShouldNotBeNull();
        await ShouldBeProblemAsync(await CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = "Password123" }, Json), HttpStatusCode.Forbidden, "account_deletion_pending");
    }
}
