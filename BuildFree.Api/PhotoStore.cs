namespace BuildFree.Api;

// Where photo files live. The database only keeps details about each photo
// (who, when, which log); the image itself is saved through this interface.
// DiskPhotoStore is used for now. A Cloudflare R2 (S3-compatible) version can
// be added later without changing any endpoint.
public interface IPhotoStore
{
    Task SaveAsync(string key, Stream content, CancellationToken ct = default);
    Task<Stream?> OpenAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

// Saves photos as files in a folder (Photos:Path in appsettings.json, or
// "photo-uploads" next to the API by default).
public class DiskPhotoStore : IPhotoStore
{
    private readonly string _root;

    public DiskPhotoStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    // Turns a key like "<accountId>/<photoId>" into a file path, and refuses
    // anything that would point outside the photo folder.
    private string PathFor(string key)
    {
        var full = Path.GetFullPath(Path.Combine(_root, key));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Invalid photo key.", nameof(key));
        return full;
    }

    public async Task SaveAsync(string key, Stream content, CancellationToken ct = default)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(file, ct);
    }

    public Task<Stream?> OpenAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        Stream? stream = File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
            : null;
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
