using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Platform.Storage;

/// <summary>Private, write-once object storage. Objects are never exposed through public URLs.</summary>
public interface IObjectStore
{
    Task PutAsync(string key, byte[] content, CancellationToken ct);

    /// <summary>Reads the object and verifies its SHA-256; throws if the stored bytes were tampered with.</summary>
    Task<byte[]> GetVerifiedAsync(string key, string expectedSha256, CancellationToken ct);
}

public sealed class FileSystemObjectStoreOptions
{
    public string RootPath { get; set; } = Path.Combine(Path.GetTempPath(), "serk-objects");
}

/// <summary>Development / clinic-local filesystem implementation. Keys must be opaque server-generated identifiers.</summary>
public sealed class FileSystemObjectStore(Microsoft.Extensions.Options.IOptions<FileSystemObjectStoreOptions> options) : IObjectStore
{
    public async Task PutAsync(string key, byte[] content, CancellationToken ct)
    {
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await fs.WriteAsync(content, ct);
    }

    public async Task<byte[]> GetVerifiedAsync(string key, string expectedSha256, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(Resolve(key), ct);
        var actual = CanonicalJson.Sha256Hex(bytes);
        if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Stored object failed integrity verification.");
        }

        return bytes;
    }

    private string Resolve(string key)
    {
        // Keys are server-generated "<prefix>/<guid>.<ext>"; reject anything that could traverse paths.
        if (key.Length > 200 || key.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(key) || key.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid object key.", nameof(key));
        }

        var root = Path.GetFullPath(options.Value.RootPath);
        var full = Path.GetFullPath(Path.Combine(root, key));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid object key.", nameof(key));
        }

        return full;
    }
}
