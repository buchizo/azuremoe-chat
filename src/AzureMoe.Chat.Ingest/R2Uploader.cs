using Amazon.S3;
using Amazon.S3.Transfer;

namespace AzureMoe.Chat.Ingest;

/// <summary>
/// Uploads files to Cloudflare R2 using the S3-compatible API.
/// R2 uses path-style addressing; any region string is accepted.
/// </summary>
public sealed class R2Uploader : IDisposable
{
    private readonly AmazonS3Client _s3;
    private readonly TransferUtility _transfer;
    private readonly string _bucket;

    public R2Uploader(string accountId, string accessKeyId, string secretAccessKey, string bucket)
    {
        _bucket = bucket;
        var config = new AmazonS3Config
        {
            ServiceURL          = $"https://{accountId}.r2.cloudflarestorage.com",
            ForcePathStyle      = true,
            AuthenticationRegion = "us-east-1",   // R2 accepts any region string
        };
        _s3       = new AmazonS3Client(accessKeyId, secretAccessKey, config);
        _transfer = new TransferUtility(_s3);
    }

    /// <summary>Uploads a local file to the bucket under the given key.</summary>
    public async Task UploadAsync(
        string localPath,
        string key,
        string contentType = "application/octet-stream",
        CancellationToken ct = default)
    {
        var req = new TransferUtilityUploadRequest
        {
            BucketName  = _bucket,
            Key         = key,
            FilePath    = localPath,
            ContentType = contentType,
        };
        await _transfer.UploadAsync(req, ct);
    }

    public void Dispose()
    {
        _transfer.Dispose();
        _s3.Dispose();
    }
}
