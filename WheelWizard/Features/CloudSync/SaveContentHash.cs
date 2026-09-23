using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace WheelWizard.Features.CloudSync;

public static class SaveContentHash
{
    public static string Compute(Stream zip)
    {
        ArgumentNullException.ThrowIfNull(zip);
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (
            var entry in archive
                .Entries.Where(entry => !entry.FullName.EndsWith("/", StringComparison.Ordinal) && entry.FullName != "manifest.json")
                .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
        )
        {
            hash.AppendData(Encoding.UTF8.GetBytes(entry.FullName));
            using var entryStream = entry.Open();
            hash.AppendData(SHA256.HashData(entryStream));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
