using System.Text.Json;

namespace Speech2Issues.Core.Storage;

public static class AtomicFile
{
    public static async Task WriteJsonAsync<T>(string path, T value, JsonSerializerOptions options, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, options);
        await WriteBytesAsync(path, bytes, cancellationToken);
    }

    public static async Task WriteTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        await WriteBytesAsync(path, System.Text.Encoding.UTF8.GetBytes(content), cancellationToken);
    }

    public static async Task WriteBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new InvalidOperationException("File directory is missing.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public static async Task<string> WriteNewTextAsync(string desiredPath, string content, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(desiredPath)) ?? throw new InvalidOperationException("File directory is missing.");
        Directory.CreateDirectory(directory);
        var stem = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);

        for (var suffix = 0; suffix < 10_000; suffix++)
        {
            var candidate = suffix == 0 ? desiredPath : Path.Combine(directory, $"{stem}-{suffix}{extension}");
            try
            {
                await using var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, true);
                await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                await writer.WriteAsync(content.AsMemory(), cancellationToken);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
            }
        }

        throw new IOException("Could not allocate a unique file name.");
    }
}
