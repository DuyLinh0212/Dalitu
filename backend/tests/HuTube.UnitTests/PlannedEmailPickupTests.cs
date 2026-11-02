using HuTube.Application.Auth;
using HuTube.Infrastructure.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace HuTube.UnitTests;

public sealed class PlannedEmailPickupTests
{
    [Fact]
    public async Task B_MEDIA_04_PickupWritesRealMimeMessageWithRecipientAndVerificationLink()
    {
        var root = Path.Combine(Path.GetTempPath(), "hutube-planned-mail-" + Guid.NewGuid().ToString("N"));
        var sender = new AuthEmailSender(new EmailOptions { Mode = "Pickup", PickupDirectory = root },
            NullLogger<AuthEmailSender>.Instance, new Factory());
        const string link = "http://localhost:4200/verify-email?token=test-only-token";
        try
        {
            await sender.SendAsync("planned@example.invalid", "Xác minh tài khoản", link, CancellationToken.None);

            var path = Assert.Single(Directory.GetFiles(root, "*.eml"));
            var message = await MimeMessage.LoadAsync(path);
            Assert.Equal("planned@example.invalid", Assert.Single(message.To.Mailboxes).Address);
            Assert.Contains(link, message.TextBody);
            Assert.Contains("Xác minh", message.Subject);
            Assert.NotEmpty(message.HtmlBody);
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), resolved, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
    private sealed class Factory : IHttpClientFactory { public HttpClient CreateClient(string name) => throw new InvalidOperationException("Pickup must not call a network sender"); }
}
