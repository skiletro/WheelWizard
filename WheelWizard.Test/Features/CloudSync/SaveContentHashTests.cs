using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using WheelWizard.Features.CloudSync;

namespace WheelWizard.Test.Features.CloudSync;

// Synthetic, non-Android archives exercise canonical behavior until an authentic export is available.
public class SaveContentHashTests
{
    [Fact]
    public void Compute_MatchesIndependentHashAndIgnoresEntryOrderAndManifestTimestamp()
    {
        var entries = new Dictionary<string, byte[]>
        {
            ["Wii/shared2/Pulsar/Ghosts/𐀀.ghost"] = [1, 2],
            ["Wii/shared2/Pulsar/Ghosts/.ghost"] = [3, 4],
            ["RetroWFC/RMCP/rksys.dat"] = [5, 6, 7],
        };
        using var first = CreateZip(entries, ["manifest.json", .. entries.Keys], "2026-01-01T00:00:00Z");
        using var second = CreateZip(entries, ["manifest.json", .. entries.Keys.Reverse()], "2027-02-03T04:05:06Z");

        Assert.Equal(IndependentHash(entries), SaveContentHash.Compute(first));
        Assert.Equal(SaveContentHash.Compute(first), SaveContentHash.Compute(second));

        entries["RetroWFC/RMCP/rksys.dat"] = [9];
        using var changed = CreateZip(entries, ["manifest.json", .. entries.Keys], "2026-01-01T00:00:00Z");
        Assert.NotEqual(SaveContentHash.Compute(second), SaveContentHash.Compute(changed));
    }

    private static MemoryStream CreateZip(Dictionary<string, byte[]> entries, IEnumerable<string> order, string createdAt)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in order)
            {
                var entry = archive.CreateEntry(name);
                using var output = entry.Open();
                var bytes =
                    name == "manifest.json"
                        ? Encoding.UTF8.GetBytes($"{{\"type\":\"wheelwitch-save\",\"version\":2,\"createdAt\":\"{createdAt}\"}}")
                        : entries[name];
                output.Write(bytes);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static string IndependentHash(IReadOnlyDictionary<string, byte[]> entries)
    {
        using var outer = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            outer.AppendData(Encoding.UTF8.GetBytes(entry.Key));
            outer.AppendData(SHA256.HashData(entry.Value));
        }
        return Convert.ToHexString(outer.GetHashAndReset()).ToLowerInvariant();
    }
}
