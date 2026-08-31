using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Speech2Issues.Core.Configuration;
using Speech2Issues.Core.Services;
using Speech2Issues.Core.Storage;

namespace Speech2Issues.Tests;

public sealed class WhisperComponentInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "speech2issues-components-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CpuPackageIsVerifiedAndExtractsOnlyRequiredWinX64Files()
    {
        var package = CreatePackage(includeCuda: false, includeTraversal: true);
        using var client = new HttpClient(new PackageHandler(package));
        var source = new RuntimePackageSource("https://packages.test/cpu.nupkg", Hash(package));
        var paths = new AppPaths(_root);
        var installer = new WhisperComponentInstaller(paths, client, source);

        await installer.EnsureRuntimeAsync(WhisperRuntimeKind.Cpu);

        Assert.True(installer.IsRuntimeInstalled(WhisperRuntimeKind.Cpu));
        Assert.True(File.Exists(installer.CpuLibraryPath));
        Assert.Contains(Path.Combine("cpu", "runtimes", "win-x64"), installer.CpuLibraryPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(paths.RuntimeDirectory, "linux.so")));
    }

    [Fact]
    public async Task CudaSelectionInstallsCpuFallbackAndCudaRuntime()
    {
        var cpu = CreatePackage(includeCuda: false);
        var cuda = CreatePackage(includeCuda: true);
        using var client = new HttpClient(new MultiPackageHandler(cpu, cuda));
        var paths = new AppPaths(_root);
        var installer = new WhisperComponentInstaller(
            paths,
            client,
            new RuntimePackageSource("https://packages.test/cpu.nupkg", Hash(cpu)),
            new RuntimePackageSource("https://packages.test/cuda.nupkg", Hash(cuda)));

        await installer.EnsureRuntimeAsync(WhisperRuntimeKind.Cuda);

        Assert.True(installer.IsRuntimeInstalled(WhisperRuntimeKind.Cpu));
        Assert.True(installer.IsRuntimeInstalled(WhisperRuntimeKind.Cuda));
        Assert.True(File.Exists(installer.CudaLibraryPath));
        Assert.Contains(Path.Combine("cuda", "runtimes", "cuda", "win-x64"), installer.CudaLibraryPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExistingFlatRuntimeIsMigratedWithoutDownloadingAgain()
    {
        var package = CreatePackage(includeCuda: false);
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        var legacyDirectory = Path.Combine(paths.RuntimeDirectory, "cpu");
        Directory.CreateDirectory(legacyDirectory);
        foreach (var name in new[] { "whisper.dll", "ggml-whisper.dll", "ggml-base-whisper.dll", "ggml-cpu-whisper.dll" })
            await File.WriteAllTextAsync(Path.Combine(legacyDirectory, name), name);

        using var client = new HttpClient(new PackageHandler(package));
        var installer = new WhisperComponentInstaller(
            paths,
            client,
            new RuntimePackageSource("https://packages.test/cpu.nupkg", Convert.ToBase64String(new byte[64])));

        await installer.EnsureRuntimeAsync(WhisperRuntimeKind.Cpu);

        Assert.True(installer.IsRuntimeInstalled(WhisperRuntimeKind.Cpu));
        Assert.True(File.Exists(installer.CpuLibraryPath));
        Assert.False(File.Exists(Path.Combine(legacyDirectory, "whisper.dll")));
    }

    [Fact]
    public async Task InvalidHashLeavesNoInstalledRuntimeAndCanBeRetried()
    {
        var package = CreatePackage(includeCuda: false);
        var paths = new AppPaths(_root);
        using var badClient = new HttpClient(new PackageHandler(package));
        var bad = new WhisperComponentInstaller(paths, badClient, new RuntimePackageSource("https://packages.test/cpu.nupkg", Convert.ToBase64String(new byte[64])));

        await Assert.ThrowsAsync<InvalidDataException>(() => bad.EnsureRuntimeAsync(WhisperRuntimeKind.Cpu));
        Assert.False(bad.IsRuntimeInstalled(WhisperRuntimeKind.Cpu));

        using var goodClient = new HttpClient(new PackageHandler(package));
        var good = new WhisperComponentInstaller(paths, goodClient, new RuntimePackageSource("https://packages.test/cpu.nupkg", Hash(package)));
        await good.EnsureRuntimeAsync(WhisperRuntimeKind.Cpu);
        Assert.True(good.IsRuntimeInstalled(WhisperRuntimeKind.Cpu));
    }

    [Fact]
    public async Task CanceledDownloadLeavesNoInstalledOrPartialRuntime()
    {
        var paths = new AppPaths(_root);
        using var client = new HttpClient(new CancelingHandler());
        var source = new RuntimePackageSource("https://packages.test/cpu.nupkg", Convert.ToBase64String(new byte[64]));
        var installer = new WhisperComponentInstaller(paths, client, source);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            installer.EnsureRuntimeAsync(WhisperRuntimeKind.Cpu, cancellationToken: cancellation.Token));

        Assert.False(installer.IsRuntimeInstalled(WhisperRuntimeKind.Cpu));
        Assert.Empty(Directory.GetFiles(paths.DownloadsDirectory));
    }

    private static byte[] CreatePackage(bool includeCuda, bool includeTraversal = false)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "whisper.dll", "ggml-whisper.dll", "ggml-base-whisper.dll", "ggml-cpu-whisper.dll" })
                Write(zip, "build/win-x64/" + name, name);
            if (includeCuda) Write(zip, "build/win-x64/ggml-cuda-whisper.dll", "cuda");
            Write(zip, "build/linux-x64/linux.so", "ignored");
            if (includeTraversal) Write(zip, "../../escaped.txt", "ignored");
        }
        return memory.ToArray();
    }

    private static void Write(ZipArchive zip, string path, string value)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open());
        writer.Write(value);
    }

    private static string Hash(byte[] data) => Convert.ToBase64String(SHA512.HashData(data));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class PackageHandler(byte[] package) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
    }

    private sealed class MultiPackageHandler(byte[] cpu, byte[] cuda) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.Contains("cuda", StringComparison.OrdinalIgnoreCase) ? cuda : cpu),
            });
    }

    private sealed class CancelingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }
}
