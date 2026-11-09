using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Diffra.Protocol;

/// <summary>
/// A local content-addressed store that persists immutable objects by their SHA-256 digest with atomic writes and verified integrity.
/// </summary>
public sealed class ContentAddressedStore
{
    private static readonly Regex DigestRegex = new(@"^sha256:[a-f0-9]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string RootDirectory { get; }
    public string ObjectsDirectory { get; }
    public string TmpDirectory { get; }

    public ContentAddressedStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Store root directory cannot be empty.", nameof(rootDirectory));
        }

        RootDirectory = Path.GetFullPath(rootDirectory);
        ObjectsDirectory = Path.Combine(RootDirectory, "objects", "sha256");
        TmpDirectory = Path.Combine(RootDirectory, "tmp");

        Directory.CreateDirectory(ObjectsDirectory);
        Directory.CreateDirectory(TmpDirectory);
    }

    /// <summary>
    /// Stores the provided byte array in the content-addressed store after verifying its SHA-256 digest.
    /// </summary>
    public string Put(ReadOnlySpan<byte> bytes, string? expectedDigest = null)
    {
        var computedDigest = ComputeDigest(bytes);
        if (expectedDigest is not null)
        {
            ValidateDigestFormat(expectedDigest);
            if (!string.Equals(computedDigest, expectedDigest, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Payload digest mismatch. Expected '{expectedDigest}' but computed '{computedDigest}'.");
            }
        }

        var targetPath = GetObjectPath(computedDigest);
        if (File.Exists(targetPath))
        {
            // Already present, verify its size matches
            var existingInfo = new FileInfo(targetPath);
            if (existingInfo.Length == bytes.Length)
            {
                return computedDigest;
            }
        }

        var tempPath = Path.Combine(TmpDirectory, $".cas-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tempPath, bytes.ToArray());
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* ignore */ }
            }
        }

        return computedDigest;
    }

    /// <summary>
    /// Stores a file in the content-addressed store after computing and verifying its SHA-256 digest.
    /// </summary>
    public string PutFile(string sourceFilePath, string? expectedDigest = null)
    {
        if (!File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException($"Source file not found: {sourceFilePath}", sourceFilePath);
        }

        var bytes = File.ReadAllBytes(sourceFilePath);
        return Put(bytes, expectedDigest);
    }

    /// <summary>
    /// Retrieves object bytes by SHA-256 digest, verifying that the stored bytes have not been tampered with.
    /// </summary>
    public byte[] Get(string digest)
    {
        ValidateDigestFormat(digest);
        var targetPath = GetObjectPath(digest);
        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException($"Object with digest '{digest}' not found in CAS.", targetPath);
        }

        var bytes = File.ReadAllBytes(targetPath);
        var actualDigest = ComputeDigest(bytes);
        if (!string.Equals(actualDigest, digest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"CAS object corrupted for digest '{digest}'. Actual digest was '{actualDigest}'.");
        }

        return bytes;
    }

    /// <summary>
    /// Checks whether an object with the given digest exists in the store.
    /// </summary>
    public bool Contains(string digest)
    {
        if (!IsValidDigest(digest)) return false;
        return File.Exists(GetObjectPath(digest));
    }

    /// <summary>
    /// Returns the verified filesystem path for an object. Throws if the object does not exist or fails verification.
    /// </summary>
    public string GetVerifiedPath(string digest)
    {
        _ = Get(digest); // forces validation and integrity verification
        return GetObjectPath(digest);
    }

    public string GetObjectPath(string digest)
    {
        ValidateDigestFormat(digest);
        var hex = digest["sha256:".Length..];
        return Path.Combine(ObjectsDirectory, hex);
    }

    public static string ComputeDigest(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, hash);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool IsValidDigest(string digest) =>
        !string.IsNullOrWhiteSpace(digest) && DigestRegex.IsMatch(digest);

    public static void ValidateDigestFormat(string digest)
    {
        if (!IsValidDigest(digest))
        {
            throw new ArgumentException($"Invalid SHA-256 digest format '{digest}'. Expected 'sha256:<64 hex characters>'.", nameof(digest));
        }
    }
}
