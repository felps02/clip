using System.Collections.Concurrent;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Amazon.Runtime;
using Amazon.S3;
using ClipForge.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClipForge.Api.Tests.Infrastructure;

/// <summary>
/// An in-memory S3 endpoint under the real AWS SDK client (path-style requests to http://s3.test): PutObject, the
/// multipart upload calls and DeleteObject. <see cref="S3ObjectStorage"/> runs through the SDK request pipeline, so the
/// checks the SDK makes before sending (e.g. the content length of a PutObject) apply as they would against MinIO.
/// </summary>
public sealed class FakeS3Handler : HttpMessageHandler
{
    public const string Bucket = "clipforge-test";

    private readonly ConcurrentDictionary<string, Upload> _uploads = new();

    public sealed record StoredObject(byte[] Content, string? ContentType);

    private sealed class Upload(string key, string? contentType)
    {
        public string Key { get; } = key;
        public string? ContentType { get; } = contentType;
        public ConcurrentDictionary<int, byte[]> Parts { get; } = new();
    }

    public ConcurrentDictionary<string, StoredObject> Objects { get; } = new();

    /// <summary>The operations received, in order: "PutObject key", "UploadPart key #1 65536", …</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>Multipart uploads neither completed nor aborted.</summary>
    public int OpenUploads => _uploads.Count;

    public AmazonS3Client CreateClient()
    {
        var config = S3ObjectStorage.CreateConfig(new S3StorageOptions
        {
            Bucket = Bucket, ServiceUrl = "http://s3.test", Region = "us-east-1", AccessKey = "test", SecretKey = "test-secret", ForcePathStyle = true,
        });
        config.HttpClientFactory = new HandlerFactory(this);
        config.MaxErrorRetry = 0;
        return new AmazonS3Client(new BasicAWSCredentials("test", "test-secret"), config);
    }

    public S3ObjectStorage CreateStorage(int partSizeBytes = S3ObjectStorage.DefaultPartSizeBytes)
        => new(CreateClient(), Bucket, NullLogger<S3ObjectStorage>.Instance, partSizeBytes);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        var slash = path.IndexOf('/');
        if (slash <= 0 || path[..slash] != Bucket) return Error(HttpStatusCode.NotFound, "NoSuchBucket");
        var key = path[(slash + 1)..];
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : string.Empty);
        var uploadId = query.GetValueOrDefault("uploadId");
        var contentType = request.Content?.Headers.ContentType?.ToString();

        switch (request.Method.Method)
        {
            case "PUT" when uploadId is not null:
            {
                if (!_uploads.TryGetValue(uploadId, out var upload)) return Error(HttpStatusCode.NotFound, "NoSuchUpload");
                var part = await ReadBodyAsync(request, cancellationToken);
                var number = int.Parse(query["partNumber"]);
                upload.Parts[number] = part;
                Calls.Enqueue($"UploadPart {key} #{number} {part.Length}");
                return Ok(etag: ETag(part));
            }
            case "PUT":
            {
                var content = await ReadBodyAsync(request, cancellationToken);
                Objects[key] = new StoredObject(content, contentType);
                Calls.Enqueue($"PutObject {key}");
                return Ok(etag: ETag(content));
            }
            case "POST" when query.ContainsKey("uploads"):
            {
                var id = Guid.NewGuid().ToString("N");
                _uploads[id] = new Upload(key, contentType);
                Calls.Enqueue($"CreateMultipartUpload {key}");
                return Ok(xml: $"<InitiateMultipartUploadResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Bucket>{Bucket}</Bucket>"
                    + $"<Key>{SecurityElement.Escape(key)}</Key><UploadId>{id}</UploadId></InitiateMultipartUploadResult>");
            }
            case "POST" when uploadId is not null:
            {
                if (!_uploads.TryGetValue(uploadId, out var upload)) return Error(HttpStatusCode.NotFound, "NoSuchUpload");
                var listed = XDocument.Parse(Encoding.UTF8.GetString(await ReadBodyAsync(request, cancellationToken)))
                    .Descendants().Where(e => e.Name.LocalName == "Part")
                    .Select(e => (Number: int.Parse(e.Elements().First(x => x.Name.LocalName == "PartNumber").Value),
                        ETag: e.Elements().First(x => x.Name.LocalName == "ETag").Value))
                    .ToList();
                using var assembled = new MemoryStream();
                foreach (var (number, etag) in listed)
                {
                    if (!upload.Parts.TryGetValue(number, out var part) || etag != ETag(part)) return Error(HttpStatusCode.BadRequest, "InvalidPart");
                    assembled.Write(part);
                }
                _uploads.TryRemove(uploadId, out _);
                Objects[upload.Key] = new StoredObject(assembled.ToArray(), upload.ContentType);
                Calls.Enqueue($"CompleteMultipartUpload {key} {listed.Count}");
                return Ok(xml: $"<CompleteMultipartUploadResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Location>http://s3.test/{Bucket}/{SecurityElement.Escape(key)}</Location>"
                    + $"<Bucket>{Bucket}</Bucket><Key>{SecurityElement.Escape(key)}</Key><ETag>\"{listed.Count}-parts\"</ETag></CompleteMultipartUploadResult>");
            }
            case "DELETE" when uploadId is not null:
                _uploads.TryRemove(uploadId, out _);
                Calls.Enqueue($"AbortMultipartUpload {key}");
                return Ok(status: HttpStatusCode.NoContent);
            case "DELETE":
                Objects.TryRemove(key, out _);
                Calls.Enqueue($"DeleteObject {key}");
                return Ok(status: HttpStatusCode.NoContent);
            default:
                return Error(HttpStatusCode.NotImplemented, "NotImplemented");
        }
    }

    /// <summary>The request body, decoded when the SDK sent it aws-chunked (streaming SigV4 payload).</summary>
    private static async Task<byte[]> ReadBodyAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content is null) return [];
        var raw = await request.Content.ReadAsByteArrayAsync(ct);
        var sha = request.Headers.TryGetValues("x-amz-content-sha256", out var values) ? values.FirstOrDefault() : null;
        var chunked = sha?.StartsWith("STREAMING-", StringComparison.Ordinal) == true
            || request.Content.Headers.ContentEncoding.Any(e => e.Contains("aws-chunked", StringComparison.OrdinalIgnoreCase));
        if (!chunked) return raw;
        // "<hex size>[;chunk-signature=…]\r\n<data>\r\n" repeated, ending with a chunk of size 0 (and optional trailers).
        using var output = new MemoryStream();
        var position = 0;
        while (position < raw.Length)
        {
            var lineEnd = raw.AsSpan(position).IndexOf("\r\n"u8) + position;
            var size = Convert.ToInt32(Encoding.ASCII.GetString(raw, position, lineEnd - position).Split(';')[0], 16);
            position = lineEnd + 2;
            if (size == 0) break;
            output.Write(raw, position, size);
            position += size + 2;
        }
        return output.ToArray();
    }

    private static string ETag(byte[] content) => "\"" + Convert.ToHexString(MD5.HashData(content)).ToLowerInvariant() + "\"";

    private static HttpResponseMessage Ok(string? etag = null, string? xml = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = xml is null ? new ByteArrayContent([]) : new StringContent("<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + xml, Encoding.UTF8, "application/xml"),
        };
        if (etag is not null) response.Headers.TryAddWithoutValidation("ETag", etag);
        response.Headers.TryAddWithoutValidation("x-amz-request-id", "fake-s3");
        return response;
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code)
    {
        var response = Ok(xml: $"<Error><Code>{code}</Code><Message>{code}</Message><RequestId>fake-s3</RequestId></Error>", status: status);
        return response;
    }

    /// <summary>Hands this handler to the SDK instead of a network client.</summary>
    private sealed class HandlerFactory(HttpMessageHandler handler) : Amazon.Runtime.HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(handler, disposeHandler: false);

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;
    }
}
