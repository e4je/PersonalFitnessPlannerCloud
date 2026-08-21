using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PersonalFitnessPlanner.Infrastructure.Models;

namespace PersonalFitnessPlanner.Infrastructure.Security;

/// <summary>Encrypts credentials for the current Windows user with DPAPI.</summary>
public sealed class DpapiTokenStore
{
    private const int MaxTokenFileBytes = 64 * 1024;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PersonalFitnessPlanner/auth/v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DpapiTokenStore(AppPaths paths) => _paths = paths;

    public async Task SaveAsync(StoredTokens tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (string.IsNullOrWhiteSpace(tokens.AccessToken))
        {
            throw new ArgumentException("访问令牌不能为空。", nameof(tokens));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureCreated();
            var plain = JsonSerializer.SerializeToUtf8Bytes(tokens, JsonOptions);
            var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            CryptographicOperations.ZeroMemory(plain);
            var temporaryPath = _paths.TokenPath + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, _paths.TokenPath, true);
                File.SetAttributes(_paths.TokenPath, File.GetAttributes(_paths.TokenPath) | FileAttributes.Hidden);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredTokens?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_paths.TokenPath))
            {
                return null;
            }

            try
            {
                var fileInfo = new FileInfo(_paths.TokenPath);
                if (fileInfo.Length <= 0 || fileInfo.Length > MaxTokenFileBytes) return null;
                var protectedBytes = await File.ReadAllBytesAsync(_paths.TokenPath, cancellationToken).ConfigureAwait(false);
                if (protectedBytes.Length > MaxTokenFileBytes) return null;
                var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                try
                {
                    return JsonSerializer.Deserialize<StoredTokens>(plain, JsonOptions);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }
            }
            catch (CryptographicException)
            {
                // Copied profile/corrupted credential: fail closed and require login.
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_paths.TokenPath))
            {
                File.SetAttributes(_paths.TokenPath, FileAttributes.Normal);
                File.Delete(_paths.TokenPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
