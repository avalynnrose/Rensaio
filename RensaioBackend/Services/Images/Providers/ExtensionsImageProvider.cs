using RensaioBackend.Data;
using RensaioBackend.Models.Database;
using RensaioBackend.Services.Rpc;

namespace RensaioBackend.Services.Images.Providers
{
    /// <summary>
    /// Serves extension icons on demand.
    ///
    /// URLs use the form "ext://<packageId>". Icons are streamed from the
    /// isolated Rensaio.RPC process rather than read from the extension folder:
    /// RPC owns those files, so the backend must never touch them directly.
    /// </summary>
    public class ExtensionsImageProvider : IImageProvider
    {
        private readonly IExtensionRpcBridge _rpc;
        private readonly AppDbContext _db;

        public ExtensionsImageProvider(AppDbContext db, IExtensionRpcBridge rpc)
        {
            _db = db;
            _rpc = rpc;
        }

        public bool CanProcess(string url)
            => !string.IsNullOrEmpty(url) && url.StartsWith("ext://", StringComparison.OrdinalIgnoreCase);

        public async Task<Stream?> ObtainStreamAsync(EtagCacheEntity cache, CancellationToken token)
        {
            // "ext://" is 6 chars; the remainder is the extension package id.
            string packageId = cache.Url.Substring(6);
            if (string.IsNullOrWhiteSpace(packageId))
                return null;

            Stream? stream = await _rpc.OpenExtensionIconAsync(packageId, token).ConfigureAwait(false);
            if (stream == null)
                return null;

            // Compute and persist the etag on first access so conditional requests work.
            if (string.IsNullOrEmpty(cache.Etag))
            {
                cache.Etag = await UrlImageProvider.ComputeMd5HashFromStreamAsync(stream);
                stream.Position = 0;
                await _db.SaveChangesAsync(token);
            }

            return stream;
        }
    }
}
