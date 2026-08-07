using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

public class NotificationSenderTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly SecretProtector _protector;

    public NotificationSenderTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _protector = new SecretProtector(new EphemeralDataProtectionProvider(), NullLogger<SecretProtector>.Instance);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public HttpStatusCode StatusCodeToReturn { get; set; } = HttpStatusCode.OK;
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(StatusCodeToReturn));
        }
    }

    private class MockHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient(handler);
    }

    [Fact]
    public async Task DispatchAsync_SkipWhenAlertSeverityBelowMinSeverity()
    {
        var handler = new MockHttpMessageHandler();
        var factory = new MockHttpClientFactory(handler);
        var sender = new NotificationSender(factory, _protector, NullLogger<NotificationSender>.Instance);

        var cfg = new NotificationSettings { MinSeverity = "high", WebhookEnabled = true, WebhookUrl = "https://example.com/webhook" };
        var alert = new TriggeredAlert { Id = Guid.NewGuid(), PolicyId = Guid.NewGuid(), PolicyName = "Test Policy", Severity = "low", Status = "new", Condition = "c", MetricValue = 1, Threshold = 1, TriggeredAt = DateTimeOffset.UtcNow };

        await sender.DispatchAsync(_db, cfg, alert, CancellationToken.None);

        Assert.Equal(0, handler.RequestCount);
        Assert.Empty(_db.NotificationLogs);
    }

    [Fact]
    public async Task DispatchAsync_WebhookSuccess_LogsSuccessRow()
    {
        var handler = new MockHttpMessageHandler { StatusCodeToReturn = HttpStatusCode.OK };
        var factory = new MockHttpClientFactory(handler);
        var sender = new NotificationSender(factory, _protector, NullLogger<NotificationSender>.Instance);

        var cfg = new NotificationSettings { MinSeverity = "low", WebhookEnabled = true, WebhookUrl = "https://example.com/webhook" };
        var alert = new TriggeredAlert { Id = Guid.NewGuid(), PolicyId = Guid.NewGuid(), PolicyName = "Critical Policy", Severity = "critical", Status = "new", Condition = "c", MetricValue = 10, Threshold = 1, TriggeredAt = DateTimeOffset.UtcNow };

        await sender.DispatchAsync(_db, cfg, alert, CancellationToken.None);
        await _db.SaveChangesAsync();

        Assert.Equal(1, handler.RequestCount);
        var log = Assert.Single(_db.NotificationLogs);
        Assert.True(log.Success);
        Assert.Equal("webhook", log.Channel);
    }

    [Fact]
    public async Task DispatchAsync_WebhookFailure_LogsErrorRow()
    {
        var handler = new MockHttpMessageHandler { StatusCodeToReturn = HttpStatusCode.InternalServerError };
        var factory = new MockHttpClientFactory(handler);
        var sender = new NotificationSender(factory, _protector, NullLogger<NotificationSender>.Instance);

        var cfg = new NotificationSettings { MinSeverity = "low", WebhookEnabled = true, WebhookUrl = "https://example.com/webhook" };
        var alert = new TriggeredAlert { Id = Guid.NewGuid(), PolicyId = Guid.NewGuid(), PolicyName = "Fail Policy", Severity = "high", Status = "new", Condition = "c", MetricValue = 5, Threshold = 1, TriggeredAt = DateTimeOffset.UtcNow };

        await sender.DispatchAsync(_db, cfg, alert, CancellationToken.None);
        await _db.SaveChangesAsync();

        Assert.Equal(1, handler.RequestCount);
        var log = Assert.Single(_db.NotificationLogs);
        Assert.False(log.Success);
        Assert.NotNull(log.Error);
        Assert.Contains("500", log.Error);
    }

    [Fact]
    public async Task SendInviteEmailAsync_SmtpNotConfigured_ReturnsError()
    {
        var handler = new MockHttpMessageHandler();
        var factory = new MockHttpClientFactory(handler);
        var sender = new NotificationSender(factory, _protector, NullLogger<NotificationSender>.Instance);

        var cfg = new NotificationSettings { EmailEnabled = false, SmtpHost = "" };

        var (ok, err) = await sender.SendInviteEmailAsync(cfg, "user@contoso.com", "Analyst", "https://vigil365.local", CancellationToken.None);

        Assert.False(ok);
        Assert.NotNull(err);
        Assert.Contains("not configured", err);
    }
}
