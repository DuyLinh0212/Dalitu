using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace HuTube.IntegrationTests;

public sealed class RateLimitIntegrationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Search_RateLimitExceeded_ShouldReturnRetryContract()
    {
        await using var limited = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("RateLimit:SearchPermitLimit", "2"));
        using var client = limited.CreateClient(new() { HandleCookies = false });

        await client.GetAsync("/api/v1/videos/search?q=rate-limit");
        await client.GetAsync("/api/v1/videos/search?q=rate-limit");
        var response = await client.GetAsync("/api/v1/videos/search?q=rate-limit");

        await AssertRateLimitResponseAsync(response);
    }

    [Fact]
    public async Task UploadPreflight_RateLimitExceeded_ShouldReturnRetryContract()
    {
        await using var limited = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("RateLimit:UploadPreflightPermitLimit", "2"));
        using var client = limited.CreateClient(new() { HandleCookies = false });

        await client.PostAsJsonAsync("/api/v1/videos/upload-preflight", new { fileName = "video.mp4", fileSize = 1 });
        await client.PostAsJsonAsync("/api/v1/videos/upload-preflight", new { fileName = "video.mp4", fileSize = 1 });
        var response = await client.PostAsJsonAsync("/api/v1/videos/upload-preflight", new { fileName = "video.mp4", fileSize = 1 });

        await AssertRateLimitResponseAsync(response);
    }

    [Fact]
    public async Task PaymentWebhook_RateLimitExceeded_ShouldReturnRetryContract()
    {
        await using var limited = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("RateLimit:PaymentWebhookPermitLimit", "2"));
        using var client = limited.CreateClient(new() { HandleCookies = false });

        await client.PostAsJsonAsync("/api/v1/payments/webhook/sepay", new { eventId = "rate-limit" });
        await client.PostAsJsonAsync("/api/v1/payments/webhook/sepay", new { eventId = "rate-limit" });
        var response = await client.PostAsJsonAsync("/api/v1/payments/webhook/sepay", new { eventId = "rate-limit" });

        await AssertRateLimitResponseAsync(response);
    }

    [Fact]
    public async Task CommentCreation_RateLimitExceeded_ShouldReturnTwoSecondRetryContract()
    {
        await using var limited = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimit:CommentPermitLimit", "2");
            builder.UseSetting("RateLimit:CommentWindowSeconds", "2");
        });
        using var firstUser = await CreateAuthenticatedUserAsync(limited, "comment_limiter_a");
        using var secondUser = await CreateAuthenticatedUserAsync(limited, "comment_limiter_b");
        var url = $"/api/v1/videos/{Guid.NewGuid()}/comments";

        var first = await firstUser.PostAsJsonAsync(url, new { content = "first", parentCommentId = (Guid?)null });
        var second = await firstUser.PostAsJsonAsync(url, new { content = "second", parentCommentId = (Guid?)null });
        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);

        var response = await firstUser.PostAsJsonAsync(url, new { content = "third", parentCommentId = (Guid?)null });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(2), response.Headers.RetryAfter?.Delta);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("COMMENT_RATE_LIMIT_EXCEEDED", body.GetProperty("code").GetString());

        var otherFirst = await secondUser.PostAsJsonAsync(url, new { content = "other first", parentCommentId = (Guid?)null });
        var otherSecond = await secondUser.PostAsJsonAsync(url, new { content = "other second", parentCommentId = (Guid?)null });
        var otherThird = await secondUser.PostAsJsonAsync(url, new { content = "other third", parentCommentId = (Guid?)null });
        Assert.Equal(HttpStatusCode.NotFound, otherFirst.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherSecond.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, otherThird.StatusCode);
    }

    private async Task<HttpClient> CreateAuthenticatedUserAsync(WebApplicationFactory<Program> app, string prefix)
    {
        var client = app.CreateClient(new() { HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-HuTube-Client", "web");
        var suffix = Guid.NewGuid().ToString("N");
        var email = $"{prefix}_{suffix}@example.com";
        var username = $"{prefix}_{suffix}";
        var registration = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username, email, displayName = "Rate Limit User", password = "Password123!"
        });
        registration.EnsureSuccessStatusCode();
        var verification = await client.PostAsJsonAsync("/api/v1/auth/verify-email", new
        {
            token = factory.Emails.Token(email)
        });
        verification.EnsureSuccessStatusCode();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email, password = "Password123!", clientType = "web", deviceInfo = "Rate limit integration test"
        });
        login.EnsureSuccessStatusCode();
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", body.GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task AssertRateLimitResponseAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("RATE_LIMIT_EXCEEDED", body.GetProperty("code").GetString());
        Assert.NotNull(response.Headers.RetryAfter);
    }
}
