using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WheelWizard.Features.CloudSync;

public sealed record ArchiveSafetyLimits(
    int MaximumEntries = 10_000,
    long MaximumEntryBytes = 64L * 1024 * 1024,
    long MaximumTotalBytes = 512L * 1024 * 1024
);

public sealed record ValidatedArchiveEntry(string ArchivePath, string StagedFilePath, long Length);

public sealed class ValidatedArchive : IDisposable
{
    internal ValidatedArchive(IReadOnlyList<ValidatedArchiveEntry> entries, string contentHash, string? rrVersion, string stagingDirectory)
    {
        Entries = new ReadOnlyCollection<ValidatedArchiveEntry>(entries.ToArray());
        ContentHash = contentHash;
        RrVersion = rrVersion;
        StagingDirectory = stagingDirectory;
    }

    public IReadOnlyList<ValidatedArchiveEntry> Entries { get; }
    public string ContentHash { get; }
    public string? RrVersion { get; }
    internal string StagingDirectory { get; }

    public void Dispose()
    {
        if (Directory.Exists(StagingDirectory))
            Directory.Delete(StagingDirectory, recursive: true);
    }
}

public interface IWheelSyncArchiveReader
{
    Task<ValidatedArchive> ValidateAsync(Stream zip, CancellationToken ct);
}

public sealed class WheelSyncArchiveReader : IWheelSyncArchiveReader
{
    private static readonly ArchiveSafetyLimits DefaultLimits = new();
    private readonly string _stagingRoot;
    private readonly ArchiveSafetyLimits _limits;

    public WheelSyncArchiveReader(string stagingRoot, ArchiveSafetyLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        _stagingRoot = Path.GetFullPath(stagingRoot);
        _limits = limits ?? DefaultLimits;
        if (_limits.MaximumEntries < 1 || _limits.MaximumEntryBytes < 0 || _limits.MaximumTotalBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    public async Task<ValidatedArchive> ValidateAsync(Stream zip, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ct.ThrowIfCancellationRequested();

        if (!zip.CanSeek || !zip.CanRead)
            throw new InvalidDataException("Save archive stream must be readable and seekable");
        IReadOnlyList<RawZipEntry> rawEntries;
        try
        {
            rawEntries = ReadRawEntries(zip);
        }
        catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException or EndOfStreamException)
        {
            throw new InvalidDataException("Save archive ZIP metadata is invalid", exception);
        }
        zip.Position = 0;
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException)
        {
            throw new InvalidDataException("Invalid save archive", exception);
        }

        using (archive)
        {
            if (archive.Entries.Count > _limits.MaximumEntries)
                throw new InvalidDataException("Save archive has too many entries");

            if (rawEntries.Count != archive.Entries.Count)
                throw new InvalidDataException("ZIP central directory entry count is inconsistent");

            var names = new HashSet<string>(StringComparer.Ordinal);
            long totalBytes = 0;
            var selected = new List<(ZipArchiveEntry Entry, string ArchivePath)>();
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!names.Add(entry.FullName))
                    throw new InvalidDataException("Duplicate save archive path");
                ValidateEntryPath(entry);
                if (entry.Length > _limits.MaximumEntryBytes)
                    throw new InvalidDataException("Save archive entry is too large");
                try
                {
                    totalBytes = checked(totalBytes + entry.Length);
                }
                catch (OverflowException exception)
                {
                    throw new InvalidDataException("Save archive is too large", exception);
                }
                if (totalBytes > _limits.MaximumTotalBytes)
                    throw new InvalidDataException("Save archive is too large");

                if (!entry.FullName.EndsWith("/", StringComparison.Ordinal) && entry.FullName != "manifest.json")
                {
                    var allowedPath = MapKnownPath(entry.FullName);
                    if (allowedPath is not null)
                        selected.Add((entry, allowedPath));
                }
            }

            if (archive.Entries.Count == 0 || archive.Entries[0].FullName != "manifest.json")
                throw new InvalidDataException("Save archive manifest must be the first entry");
            var (contentHash, manifestBytes) = await ValidateContentsAndHashAsync(zip, archive, rawEntries, ct);
            var rrVersion = ReadManifest(manifestBytes);

            var stagingDirectory = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);
            try
            {
                var staged = new List<ValidatedArchiveEntry>(selected.Count);
                long stagedTotal = 0;
                for (var index = 0; index < selected.Count; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (entry, path) = selected[index];
                    var stagedPath = Path.Combine(stagingDirectory, index.ToString("D5") + ".entry");
                    await using var input = entry.Open();
                    await using var output = new FileStream(
                        stagedPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        81920,
                        FileOptions.Asynchronous | FileOptions.WriteThrough
                    );
                    var remainingTotal = _limits.MaximumTotalBytes - stagedTotal;
                    var copied = await CopyBoundedAsync(input, output, entry.Length, _limits.MaximumEntryBytes, remainingTotal, ct);
                    stagedTotal = checked(stagedTotal + copied);
                    await output.FlushAsync(ct);
                    output.Flush(flushToDisk: true);
                    staged.Add(new ValidatedArchiveEntry(path, stagedPath, entry.Length));
                }

                return new ValidatedArchive(staged, contentHash, rrVersion, stagingDirectory);
            }
            catch
            {
                Directory.Delete(stagingDirectory, recursive: true);
                throw;
            }
        }
    }

    private async Task<(string ContentHash, byte[] ManifestBytes)> ValidateContentsAndHashAsync(
        Stream zip,
        ZipArchive archive,
        IReadOnlyList<RawZipEntry> rawEntries,
        CancellationToken ct
    )
    {
        using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var manifest = new MemoryStream();
        long totalBytes = 0;
        var entries = archive.Entries.Select((entry, index) => (Entry: entry, Raw: rawEntries[index]));
        foreach (var (entry, raw) in entries.OrderBy(item => item.Entry.FullName, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (raw.UncompressedLength != entry.Length)
                throw new InvalidDataException("ZIP entry size metadata is inconsistent");
            if ((raw.Flags & 1) != 0)
                throw new InvalidDataException("Encrypted save archives are not supported");
            using var entryHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var shouldHash = !entry.FullName.EndsWith("/", StringComparison.Ordinal) && entry.FullName != "manifest.json";
            using var compressed = new BoundedZipStream(zip, raw.DataOffset, raw.CompressedLength);
            using Stream input = raw.CompressionMethod switch
            {
                0 => compressed,
                8 => new DeflateStream(compressed, CompressionMode.Decompress, leaveOpen: true),
                _ => throw new InvalidDataException("Unsupported save archive compression method"),
            };
            var buffer = new byte[81920];
            long entryBytes = 0;
            uint crc = uint.MaxValue;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) != 0)
            {
                entryBytes = checked(entryBytes + read);
                totalBytes = checked(totalBytes + read);
                if (entryBytes > _limits.MaximumEntryBytes)
                    throw new InvalidDataException("Save archive entry is too large after decompression");
                if (totalBytes > _limits.MaximumTotalBytes)
                    throw new InvalidDataException("Save archive is too large after decompression");
                crc = UpdateCrc32(crc, buffer.AsSpan(0, read));
                if (shouldHash)
                    entryHash.AppendData(buffer, 0, read);
                if (entry.FullName == "manifest.json")
                    await manifest.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            if (entryBytes != raw.UncompressedLength || ~crc != raw.Crc32)
                throw new InvalidDataException("Save archive entry data does not match its ZIP metadata");
            if (shouldHash)
            {
                contentHash.AppendData(Encoding.UTF8.GetBytes(entry.FullName));
                contentHash.AppendData(entryHash.GetHashAndReset());
            }
        }
        if (manifest.Length == 0)
            throw new InvalidDataException("Save archive manifest is missing");
        return (Convert.ToHexString(contentHash.GetHashAndReset()).ToLowerInvariant(), manifest.ToArray());
    }

    private IReadOnlyList<RawZipEntry> ReadRawEntries(Stream zip)
    {
        var tailLength = (int)Math.Min(zip.Length, 65_557);
        var tailOffset = zip.Length - tailLength;
        var tail = ReadAt(zip, tailOffset, tailLength);
        var eocdIndex = -1;
        for (var index = tail.Length - 22; index >= 0; index--)
        {
            if (ReadUInt32(tail, index) == 0x06054b50 && index + 22 + ReadUInt16(tail, index + 20) == tail.Length)
            {
                eocdIndex = index;
                break;
            }
        }
        if (eocdIndex < 0)
            throw new InvalidDataException("ZIP end-of-directory record is missing");
        var eocdOffset = tailOffset + eocdIndex;
        var eocd = tail.AsSpan(eocdIndex);
        var diskNumber = ReadUInt16(eocd, 4);
        var centralDisk = ReadUInt16(eocd, 6);
        var entriesOnDisk = ReadUInt16(eocd, 8);
        var entryCount16 = ReadUInt16(eocd, 10);
        ulong entryCount = entryCount16;
        ulong centralSize = ReadUInt32(eocd, 12);
        ulong centralOffset = ReadUInt32(eocd, 16);
        var centralDirectoryEnd = eocdOffset;
        if (diskNumber != 0 || centralDisk != 0 || entriesOnDisk != entryCount16)
            throw new InvalidDataException("Multi-disk save archives are not supported");

        if (entryCount16 == ushort.MaxValue || centralSize == uint.MaxValue || centralOffset == uint.MaxValue)
        {
            var locator = ReadAt(zip, eocdOffset - 20, 20);
            if (ReadUInt32(locator, 0) != 0x07064b50 || ReadUInt32(locator, 4) != 0 || ReadUInt32(locator, 16) != 1)
                throw new InvalidDataException("ZIP64 end-of-directory locator is invalid");
            var zip64Offset = ToLong(ReadUInt64(locator, 8));
            var zip64Fixed = ReadAt(zip, zip64Offset, 56);
            if (ReadUInt32(zip64Fixed, 0) != 0x06064b50)
                throw new InvalidDataException("ZIP64 end-of-directory record is invalid");
            var recordSize = ToLong(ReadUInt64(zip64Fixed, 4));
            var zip64EndOffset = checked(zip64Offset + 12 + recordSize);
            if (zip64EndOffset != eocdOffset - 20)
                throw new InvalidDataException("ZIP64 end-of-directory record has an invalid size");
            entryCount = ReadUInt64(zip64Fixed, 32);
            centralSize = ReadUInt64(zip64Fixed, 40);
            centralOffset = ReadUInt64(zip64Fixed, 48);
            centralDirectoryEnd = zip64Offset;
        }
        if (entryCount > (ulong)_limits.MaximumEntries || entryCount > (ulong)int.MaxValue)
            throw new InvalidDataException("Save archive has too many entries");

        var centralStart = checked(centralDirectoryEnd - ToLong(centralSize));
        var archiveStart = checked(centralStart - ToLong(centralOffset));
        if (archiveStart < 0)
            throw new InvalidDataException("ZIP central directory offset is invalid");
        var position = centralStart;
        var centralEnd = checked(centralStart + ToLong(centralSize));
        var result = new List<RawZipEntry>((int)entryCount);
        for (var index = 0; index < (int)entryCount; index++)
        {
            var header = ReadAt(zip, position, 46);
            if (ReadUInt32(header, 0) != 0x02014b50)
                throw new InvalidDataException("ZIP central directory entry is invalid");
            var flags = ReadUInt16(header, 8);
            var method = ReadUInt16(header, 10);
            var crc32 = ReadUInt32(header, 16);
            ulong compressedLength = ReadUInt32(header, 20);
            ulong uncompressedLength = ReadUInt32(header, 24);
            var nameLength = ReadUInt16(header, 28);
            var extraLength = ReadUInt16(header, 30);
            var commentLength = ReadUInt16(header, 32);
            var diskStart = ReadUInt16(header, 34);
            ulong localHeaderOffset = ReadUInt32(header, 42);
            var variableLength = checked(nameLength + extraLength + commentLength);
            var variable = ReadAt(zip, checked(position + 46), variableLength);
            if (diskStart != 0)
                throw new InvalidDataException("Multi-disk save archives are not supported");
            if (uncompressedLength == uint.MaxValue || compressedLength == uint.MaxValue || localHeaderOffset == uint.MaxValue)
            {
                ReadZip64Values(
                    variable.AsSpan(nameLength, extraLength),
                    uncompressedLength == uint.MaxValue,
                    compressedLength == uint.MaxValue,
                    localHeaderOffset == uint.MaxValue,
                    ref uncompressedLength,
                    ref compressedLength,
                    ref localHeaderOffset
                );
            }
            var localOffset = checked(archiveStart + ToLong(localHeaderOffset));
            var localHeader = ReadAt(zip, localOffset, 30);
            if (ReadUInt32(localHeader, 0) != 0x04034b50 || ReadUInt16(localHeader, 8) != method)
                throw new InvalidDataException("ZIP local entry header is invalid");
            if ((flags & 1) != 0 || (ReadUInt16(localHeader, 6) & 1) != 0)
                throw new InvalidDataException("Encrypted save archives are not supported");
            var dataOffset = checked(localOffset + 30 + ReadUInt16(localHeader, 26) + ReadUInt16(localHeader, 28));
            var compressedSize = ToLong(compressedLength);
            if (dataOffset < 0 || compressedSize < 0 || dataOffset > zip.Length - compressedSize)
                throw new InvalidDataException("ZIP entry data lies outside the archive");
            result.Add(new RawZipEntry(method, flags, crc32, compressedSize, ToLong(uncompressedLength), dataOffset));
            position = checked(position + 46 + variableLength);
            if (position > centralEnd)
                throw new InvalidDataException("ZIP central directory size is invalid");
        }
        if (position != centralEnd)
            throw new InvalidDataException("ZIP central directory size is inconsistent");
        return result;
    }

    private static void ReadZip64Values(
        ReadOnlySpan<byte> extra,
        bool hasUncompressed,
        bool hasCompressed,
        bool hasLocalOffset,
        ref ulong uncompressed,
        ref ulong compressed,
        ref ulong localOffset
    )
    {
        var position = 0;
        while (position + 4 <= extra.Length)
        {
            var id = ReadUInt16(extra, position);
            var size = ReadUInt16(extra, position + 2);
            position += 4;
            if (position + size > extra.Length)
                throw new InvalidDataException("ZIP extra field is truncated");
            if (id == 0x0001)
            {
                var values = extra.Slice(position, size);
                var valuePosition = 0;
                if (hasUncompressed)
                {
                    uncompressed = ReadUInt64(values, valuePosition);
                    valuePosition += 8;
                }
                if (hasCompressed)
                {
                    compressed = ReadUInt64(values, valuePosition);
                    valuePosition += 8;
                }
                if (hasLocalOffset)
                    localOffset = ReadUInt64(values, valuePosition);
                return;
            }
            position += size;
        }
        throw new InvalidDataException("ZIP64 entry metadata is missing");
    }

    private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
            crc = Crc32Table[(crc ^ value) & 0xff] ^ (crc >> 8);
        return crc;
    }

    private static readonly uint[] Crc32Table = CreateCrc32Table();

    private static uint[] CreateCrc32Table()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        return table;
    }

    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset > stream.Length - count)
            throw new InvalidDataException("ZIP metadata lies outside the archive");
        stream.Position = offset;
        var bytes = new byte[count];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));

    private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8));

    private static long ToLong(ulong value) =>
        value <= long.MaxValue ? (long)value : throw new InvalidDataException("ZIP length exceeds supported range");

    private static string? ReadManifest(byte[] manifestBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(manifestBytes);
            var root = document.RootElement;
            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != "wheelwitch-save"
                || !root.TryGetProperty("version", out var version)
                || !version.TryGetInt32(out var versionNumber)
                || versionNumber is < 1 or > 2
            )
                throw new InvalidDataException("Unsupported save archive");
            if (
                root.TryGetProperty("rrVersion", out var rrVersion)
                && rrVersion.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
            )
                throw new InvalidDataException("Invalid save archive version");
            return rrVersion.ValueKind == JsonValueKind.String ? rrVersion.GetString() : null;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid save archive manifest", exception);
        }
    }

    private static async Task<long> CopyBoundedAsync(
        Stream input,
        Stream output,
        long declaredLength,
        long maximumEntryBytes,
        long maximumRemainingTotalBytes,
        CancellationToken ct
    )
    {
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            copied = checked(copied + read);
            if (copied > declaredLength)
                throw new InvalidDataException("Save archive entry length changed during extraction");
            if (copied > maximumEntryBytes || copied > maximumRemainingTotalBytes)
                throw new InvalidDataException("Save archive exceeds expanded size limits");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (copied != declaredLength)
            throw new InvalidDataException("Save archive entry length does not match its header");
        return copied;
    }

    private static void ValidateEntryPath(ZipArchiveEntry entry)
    {
        var name = entry.FullName;
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixType == 0xA000)
            throw new InvalidDataException("Symbolic links are not allowed in save archives");
        if (name.Length == 0 || name[0] == '/' || name[0] == '\\' || name.Contains('\\') || Path.IsPathRooted(name))
            throw new InvalidDataException("Unsafe save path");
        if (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':')
            throw new InvalidDataException("Unsafe save path");
        var components = name.Split('/');
        if (components.Any(component => component is ".." or "." || component.Length == 0 && !name.EndsWith("/", StringComparison.Ordinal)))
            throw new InvalidDataException("Unsafe save path");
        if (components.Any(component => component.Length == 0 && component != components[^1]))
            throw new InvalidDataException("Unsafe save path");
    }

    private sealed record RawZipEntry(
        ushort CompressionMethod,
        ushort Flags,
        uint Crc32,
        long CompressedLength,
        long UncompressedLength,
        long DataOffset
    );

    private sealed class BoundedZipStream(Stream source, long start, long length) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var permitted = (int)Math.Min(buffer.Length, length - _read);
            if (permitted <= 0)
                return 0;
            source.Position = start + _read;
            var read = source.Read(buffer[..permitted]);
            _read += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal static string? MapKnownPath(string path)
    {
        if (path.StartsWith("RetroWFC/", StringComparison.Ordinal))
        {
            var region = path["RetroWFC/".Length..];
            if (
                region.Length == "RMCP/rksys.dat".Length
                && (
                    region.StartsWith("RMCP/", StringComparison.Ordinal)
                    || region.StartsWith("RMCE/", StringComparison.Ordinal)
                    || region.StartsWith("RMCJ/", StringComparison.Ordinal)
                    || region.StartsWith("RMCK/", StringComparison.Ordinal)
                )
                && region.EndsWith("/rksys.dat", StringComparison.Ordinal)
            )
                return path;
            return null;
        }

        const string wiiPrefix = "Wii/";
        if (!path.StartsWith(wiiPrefix, StringComparison.Ordinal))
            return null;
        var wiiPath = path[wiiPrefix.Length..];
        if (wiiPath == "shared2/menu/FaceLib/RFL_DB.dat")
            return path;
        const string pulsarPrefix = "shared2/Pulsar/RetroRewind6/";
        if (!wiiPath.StartsWith(pulsarPrefix, StringComparison.Ordinal))
            return null;
        var pulsarPath = wiiPath[pulsarPrefix.Length..];
        if (pulsarPath is "RRRating.pul" or "RRGameSettings.pul" or "RRSettings.pul")
            return path;
        return pulsarPath.StartsWith("Ghosts/", StringComparison.Ordinal) && pulsarPath.Length > "Ghosts/".Length ? path : null;
    }
}
