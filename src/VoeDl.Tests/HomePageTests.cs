using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using VoeDl.Web.Components.Pages;
using VoeDl.Web.Models;
using VoeDl.Web.Services;

namespace VoeDl.Tests;

/// <summary>
/// Integration tests for the Home page that simulate user interactions,
/// including clicking the "Download starten" button.
/// These tests are designed to run inside a Docker container via
/// <c>Dockerfile.tests</c>.
/// </summary>
public sealed class HomePageTests : TestContext
{
    private const string DownloadButtonText = "Download starten";
    private readonly IJobManagerService _jobManager;

    public HomePageTests()
    {
        _jobManager = Substitute.For<IJobManagerService>();
        _jobManager.GetJobs().Returns([]);
        _jobManager.LoadHistoryAsync()
            .Returns(Task.FromResult<IReadOnlyList<string>>([]));
        _jobManager.LoadHistoryEntriesAsync()
            .Returns(Task.FromResult<IReadOnlyList<DownloadHistoryEntry>>([]));
        _jobManager.ResolveInputUrlsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                string input = callInfo.ArgAt<string>(0);
                IReadOnlyList<string> resolved = [input];
                return Task.FromResult(resolved);
            });

        Services.AddSingleton(_jobManager);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddMudServices();

        // Allow all JS interop calls (required by MudBlazor components)
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public async Task ClickingDownloadButton_WithValidUrl_EnqueuesUrl()
    {
        // Arrange
        const string testUrl = "https://voe.sx/abc123";
        var cut = RenderComponent<Home>();

        // Act: type a URL into the text area
        var textarea = cut.Find("textarea");
        await textarea.ChangeAsync(new() { Value = testUrl });

        // Act: click the "Download starten" button
        var downloadButton = cut.FindAll("button")
            .First(b => b.TextContent.Contains(DownloadButtonText, StringComparison.OrdinalIgnoreCase));
        await downloadButton.ClickAsync(new());

        // Assert: the URL must have been passed to the job manager exactly once
        _jobManager.Received(1).Enqueue(testUrl);
    }

    [Fact]
    public async Task ClickingDownloadButton_WithEmptyInput_DoesNotEnqueueAnything()
    {
        // Arrange
        var cut = RenderComponent<Home>();

        // Act: click download without entering any URL
        var downloadButton = cut.FindAll("button")
            .First(b => b.TextContent.Contains(DownloadButtonText, StringComparison.OrdinalIgnoreCase));
        await downloadButton.ClickAsync(new());

        // Assert: no job must be enqueued
        _jobManager.DidNotReceive().Enqueue(Arg.Any<string>());
    }

    [Fact]
    public void HomePageRendersDownloadButton()
    {
        // Arrange & Act
        var cut = RenderComponent<Home>();

        // Assert: the button with the expected label is present
        var downloadButton = cut.FindAll("button")
            .FirstOrDefault(b => b.TextContent.Contains(DownloadButtonText, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(downloadButton);
    }
}
