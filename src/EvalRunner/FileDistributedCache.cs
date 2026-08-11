using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;

namespace EvalRunner;

/// <summary>
/// A file-backed response cache.
/// </summary>
/// <remarks>
/// An in-memory cache is useless for the thing caching is meant to solve here: CI starts a fresh
/// process for every run, so nothing would ever hit. Persisting to disk means a re-run with an
/// unchanged prompt and model costs nothing, which is what makes a judge affordable on every pull
/// request. Cache keys include the prompt and model, so a change to the agent correctly invalidates.
/// </remarks>
public sealed class FileDistributedCache(string directory) : IDistributedCache
{
    private string PathFor(string key)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(directory, Convert.ToHexString(hash) + ".bin");
    }

    public byte[]? Get(string key)
    {
        string path = PathFor(key);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
        Task.FromResult(Get(key));

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(PathFor(key), value);
    }

    public Task SetAsync(
        string key,
        byte[] value,
        DistributedCacheEntryOptions options,
        CancellationToken token = default)
    {
        Set(key, value, options);
        return Task.CompletedTask;
    }

    public void Refresh(string key)
    {
    }

    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

    public void Remove(string key)
    {
        string path = PathFor(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        Remove(key);
        return Task.CompletedTask;
    }
}
