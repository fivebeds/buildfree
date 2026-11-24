using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace BuildFree.Api;

// Where photo files live. The database only keeps details about each photo
// (who, when, which log); the image itself is saved through this interface.
// DiskPhotoStore keeps them in a folder; S3PhotoStore keeps them in
// S3-compatible storage such as Cloudflare R2. Photos:Storage picks one.
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

// Saves photos in S3-compatible storage such as Cloudflare R2, so they survive
// restarts and redeploys on hosts whose disks are wiped. Turned on with
// Photos:Storage = S3 plus the Photos:S3 settings (see the README).
public class S3PhotoStore : IPhotoStore
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly bool _https;

    public S3PhotoStore(string serviceUrl, string bucket, string accessKey, string secretKey, string region)
    {
        _bucket = bucket;
        _https = serviceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        _s3 = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), new AmazonS3Config
        {
            ServiceURL = serviceUrl,
            AuthenticationRegion = region,
            ForcePathStyle = true,
            // R2 doesn't accept the extra checksums newer SDKs add to every request.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        });
    }

    public async Task SaveAsync(string key, Stream content, CancellationToken ct = default)
    {
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = content,
            AutoCloseStream = false,
            // R2 needs this; it's only allowed over https.
            DisablePayloadSigning = _https
        }, ct);
    }

    public async Task<Stream?> OpenAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var response = await _s3.GetObjectAsync(_bucket, key, ct);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default) =>
        await _s3.DeleteObjectAsync(_bucket, key, ct);
}
