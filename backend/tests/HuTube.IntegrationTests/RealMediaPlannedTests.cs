using System.Diagnostics;
using HuTube.Application.Videos;
using HuTube.Infrastructure.Videos;

namespace HuTube.IntegrationTests;

// Runs the production transcoder against actual local FFmpeg/ffprobe processes.
public sealed class RealMediaPlannedTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hutube-planned-media-" + Guid.NewGuid().ToString("N"));
    private static FfmpegVideoTranscoder Transcoder() => new(new VideoProcessingOptions { Enabled = true, Threads = 1 });

    [Fact]
    public async Task B_MEDIA_01_RealSourceAndEveryLowerRendition_HaveVideoAudioAndValidBytes()
    {
        Directory.CreateDirectory(root);
        var source = await SourceAsync();
        var transcoder = Transcoder();

        var probe = await transcoder.ProbeAsync(source);
        var outputs = await transcoder.CreateLowerRenditionsAsync(source, "720p", root);

        Assert.Equal(720, probe.Height);
        Assert.InRange(probe.Duration, 2, 4);
        Assert.Equal(new[] { "480p", "360p" }, outputs.Select(x => x.Quality));
        foreach (var output in outputs)
        {
            var actual = await transcoder.ProbeAsync(output.FilePath);
            Assert.Equal(output.Height, actual.Height);
            Assert.True(actual.Height < probe.Height);
            Assert.Equal(new FileInfo(output.FilePath).Length, output.FileSize);
            Assert.True(output.FileSize > 0);
            Assert.False(File.Exists(output.FilePath + ".part"));
            var audio = await RunAsync("ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0", output.FilePath);
            Assert.Contains("aac", audio);
        }
    }

    [Fact]
    public async Task B_MEDIA_02_TruncatedOutputAndPartialCheckpoint_AreReencodedOnRetry()
    {
        Directory.CreateDirectory(root);
        var source = await SourceAsync();
        await File.WriteAllTextAsync(Path.Combine(root, "480p.mp4"), "truncated mp4");
        await File.WriteAllTextAsync(Path.Combine(root, "480p.mp4.part"), "interrupted worker checkpoint");

        var output = await Transcoder().CreateRenditionAsync(source, "720p", "480p", root);

        Assert.NotNull(output);
        Assert.Equal(480, (await Transcoder().ProbeAsync(output.FilePath)).Height);
        Assert.False(File.Exists(output.FilePath + ".part"));
    }

    [Fact]
    public async Task B_MEDIA_02_MalformedMedia_IsRejectedByRealFfprobe()
    {
        Directory.CreateDirectory(root);
        var malformed = Path.Combine(root, "malformed.mp4");
        await File.WriteAllTextAsync(malformed, "not an MP4");

        var error = await Assert.ThrowsAsync<ContentException>(() => Transcoder().ProbeAsync(malformed));

        Assert.Equal("INVALID_MEDIA", error.Code);
        Assert.False(File.Exists(Path.Combine(root, "480p.mp4")));
    }

    private async Task<string> SourceAsync()
    {
        var path = Path.Combine(root, "source.mp4");
        await RunAsync("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=15", "-f", "lavfi", "-i", "sine=frequency=440", "-t", "2", "-c:v", "libx264", "-preset", "ultrafast", "-threads", "1", "-c:a", "aac", "-shortest", path);
        return path;
    }
    private static async Task<string> RunAsync(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, await stderr);
        return await stdout;
    }
    public void Dispose()
    {
        var resolved = Path.GetFullPath(root);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), resolved, StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
