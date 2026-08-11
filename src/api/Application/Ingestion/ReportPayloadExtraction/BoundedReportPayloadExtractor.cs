using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using DmarcAnalyzer.Api.Application.Reports;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>
/// Classifies and expands raw report payloads without allowing the request, an archive
/// entry, aggregate output, entry count, or compression ratio to grow without a bound.
/// It does not parse reports and has no source, authentication, or persistence knowledge.
/// </summary>
public sealed class BoundedReportPayloadExtractor(
    IOptions<ReportPayloadExtractionOptions> options) : IReportPayloadExtractor
{
    private const uint ZipLocalHeader = 0x04034B50;
    private const uint ZipCentralHeader = 0x02014B50;
    private const uint ZipEndOfCentralDirectory = 0x06054B50;
    private const uint ZipDataDescriptor = 0x08074B50;
    private const int CopyBufferBytes = 64 * 1024;

    private readonly ReportPayloadExtractionOptions _options = options.Value;

    public async Task<ReportPayloadExtractionResult> ExtractAsync(
        Stream payload,
        ReportPayloadMetadata metadata,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(metadata);

        var request = await ReadBoundedAsync(payload, _options.MaxRequestBytes, ct);
        if (request.Exceeded)
        {
            return Fatal(ReportPayloadRejectionCode.RequestTooLarge, payloadBytes: request.BytesRead);
        }

        var bytes = request.Bytes;
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));

        if (bytes.Length == 0)
        {
            return Fatal(ReportPayloadRejectionCode.EmptyPayload, digest, bytes.Length);
        }

        var magic = DetectContainerMagic(bytes);
        if (magic == ContainerKind.Unsupported)
        {
            return Fatal(ReportPayloadRejectionCode.UnsupportedContainer, digest, bytes.Length);
        }

        if (magic == ContainerKind.Zip)
        {
            return await ExtractZipAsync(bytes, digest, ct);
        }

        if (magic == ContainerKind.Gzip)
        {
            return await ExtractGzipAsync(bytes, metadata, digest, ct);
        }

        // Recognisable body content beats misleading .zip/.gz labels.
        var contentKind = ReportPayloadFormat.Classify(bytes);
        if (contentKind != ReportPayloadKind.Unknown)
        {
            return Accepted(contentKind, bytes, metadata.FileName, digest, bytes.Length);
        }

        var labelledContainer = DetectContainerLabel(metadata.FileName, metadata.MediaType);
        if (labelledContainer == ContainerKind.Zip)
        {
            return await ExtractZipAsync(bytes, digest, ct);
        }

        if (labelledContainer == ContainerKind.Gzip)
        {
            return await ExtractGzipAsync(bytes, metadata, digest, ct);
        }

        if (labelledContainer == ContainerKind.Unsupported)
        {
            return Fatal(ReportPayloadRejectionCode.UnsupportedContainer, digest, bytes.Length);
        }

        var labelledKind = ReportPayloadFormat.Classify(bytes, metadata.FileName, metadata.MediaType);
        return labelledKind == ReportPayloadKind.Unknown
            ? Fatal(ReportPayloadRejectionCode.UnsupportedFormat, digest, bytes.Length)
            : Accepted(labelledKind, bytes, metadata.FileName, digest, bytes.Length);
    }

    private async Task<ReportPayloadExtractionResult> ExtractGzipAsync(
        byte[] request,
        ReportPayloadMetadata metadata,
        string digest,
        CancellationToken ct)
    {
        if (request.Length < 18 || request[2] != 8 || (request[3] & 0xE0) != 0)
        {
            return Fatal(ReportPayloadRejectionCode.CorruptContainer, digest, request.Length);
        }

        var maxOutput = Math.Min(_options.MaxEntryBytes, _options.MaxExpandedBytes);

        try
        {
            await using var source = new MemoryStream(request, writable: false);
            await using var gzip = new GZipStream(source, CompressionMode.Decompress);
            var expanded = await ReadCompressedAsync(gzip, maxOutput, request.Length, ct);

            if (expanded.RatioExceeded)
            {
                return Fatal(ReportPayloadRejectionCode.CompressionRatioExceeded, digest, request.Length);
            }

            if (expanded.Exceeded)
            {
                var code = _options.MaxEntryBytes <= _options.MaxExpandedBytes
                    ? ReportPayloadRejectionCode.EntryTooLarge
                    : ReportPayloadRejectionCode.ExpandedSizeLimitExceeded;
                return Fatal(code, digest, request.Length);
            }

            var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(request.Length - 4));
            if (declaredSize != (uint)expanded.Bytes.Length)
            {
                return Fatal(ReportPayloadRejectionCode.CorruptContainer, digest, request.Length);
            }

            if (expanded.Bytes.Length == 0)
            {
                return Fatal(ReportPayloadRejectionCode.EmptyContainer, digest, request.Length);
            }

            var innerName = StripGzipSuffix(metadata.FileName);
            if (IsNestedContainer(expanded.Bytes, innerName, null))
            {
                return Fatal(ReportPayloadRejectionCode.NestedContainer, digest, request.Length);
            }

            var kind = ClassifyPayload(expanded.Bytes, innerName, null);
            return kind == ReportPayloadKind.Unknown
                ? Fatal(ReportPayloadRejectionCode.UnsupportedFormat, digest, request.Length)
                : Accepted(kind, expanded.Bytes, metadata.FileName, digest, request.Length);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return Fatal(ReportPayloadRejectionCode.CorruptContainer, digest, request.Length);
        }
    }

    private async Task<ReportPayloadExtractionResult> ExtractZipAsync(
        byte[] request,
        string digest,
        CancellationToken ct)
    {
        var inspection = InspectZip(request);
        if (inspection.Rejection is not null)
        {
            return Fatal(inspection.Rejection.Value, digest, request.Length);
        }

        if (inspection.EntryCount > _options.MaxArchiveEntries)
        {
            return Fatal(ReportPayloadRejectionCode.ArchiveEntryLimitExceeded, digest, request.Length);
        }

        if (inspection.MaxEntryBytes > _options.MaxEntryBytes)
        {
            return Fatal(ReportPayloadRejectionCode.EntryTooLarge, digest, request.Length);
        }

        if (inspection.TotalExpandedBytes > _options.MaxExpandedBytes)
        {
            return Fatal(ReportPayloadRejectionCode.ExpandedSizeLimitExceeded, digest, request.Length);
        }

        if (inspection.EntryRatioExceeded
            || ExceedsRatio(inspection.TotalExpandedBytes, inspection.TotalCompressedBytes))
        {
            return Fatal(ReportPayloadRejectionCode.CompressionRatioExceeded, digest, request.Length);
        }

        var payloads = new List<ExtractedReportPayload>();
        var rejections = new List<ReportPayloadRejection>();
        long actualExpanded = 0;
        var nonDirectoryEntries = 0;
        var nonEmptyEntries = 0;

        try
        {
            using var source = new MemoryStream(request, writable: false);
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: false);

            if (archive.Entries.Count != inspection.EntryCount)
            {
                return FatalAndDispose(payloads, ReportPayloadRejectionCode.CorruptContainer, digest, request.Length);
            }

            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (IsDirectory(entry))
                {
                    continue;
                }

                nonDirectoryEntries++;
                var remainingTotal = _options.MaxExpandedBytes - actualExpanded;
                var readLimit = (int)Math.Min(_options.MaxEntryBytes, remainingTotal);

                await using var entryStream = entry.Open();
                var expanded = await ReadCompressedAsync(entryStream, readLimit, entry.CompressedLength, ct);

                if (expanded.RatioExceeded)
                {
                    return FatalAndDispose(payloads, ReportPayloadRejectionCode.CompressionRatioExceeded, digest, request.Length);
                }

                if (expanded.Exceeded)
                {
                    var code = remainingTotal <= _options.MaxEntryBytes
                        ? ReportPayloadRejectionCode.ExpandedSizeLimitExceeded
                        : ReportPayloadRejectionCode.EntryTooLarge;
                    return FatalAndDispose(payloads, code, digest, request.Length);
                }

                actualExpanded += expanded.Bytes.Length;
                if (expanded.Bytes.Length == 0)
                {
                    rejections.Add(new(ReportPayloadRejectionCode.EmptyPayload, entry.FullName));
                    continue;
                }

                nonEmptyEntries++;
                if (IsNestedContainer(expanded.Bytes, entry.FullName, null))
                {
                    rejections.Add(new(ReportPayloadRejectionCode.NestedContainer, entry.FullName));
                    continue;
                }

                var kind = ClassifyPayload(expanded.Bytes, entry.FullName, null);
                if (kind == ReportPayloadKind.Unknown)
                {
                    rejections.Add(new(ReportPayloadRejectionCode.UnsupportedFormat, entry.FullName));
                    continue;
                }

                payloads.Add(new ExtractedReportPayload(
                    kind,
                    new MemoryStream(expanded.Bytes, writable: false),
                    entry.FullName));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            return FatalAndDispose(payloads, ReportPayloadRejectionCode.CorruptContainer, digest, request.Length);
        }

        if (nonDirectoryEntries == 0 || nonEmptyEntries == 0)
        {
            return FatalAndDispose(payloads, ReportPayloadRejectionCode.EmptyContainer, digest, request.Length);
        }

        return new(payloads, rejections, digest, request.Length);
    }

    private async Task<BoundedReadResult> ReadCompressedAsync(
        Stream source,
        int maxBytes,
        long compressedBytes,
        CancellationToken ct)
    {
        var output = new MemoryStream(Math.Min(maxBytes, CopyBufferBytes));
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);

        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0)
                {
                    return new(output.ToArray(), Exceeded: false, RatioExceeded: false);
                }

                var nextLength = output.Length + read;
                if (ExceedsRatio(nextLength, compressedBytes))
                {
                    return new([], Exceeded: false, RatioExceeded: true);
                }

                if (nextLength > maxBytes)
                {
                    return new([], Exceeded: true, RatioExceeded: false);
                }

                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            await output.DisposeAsync();
        }
    }

    private static async Task<BoundedRequestReadResult> ReadBoundedAsync(
        Stream source,
        int maxBytes,
        CancellationToken ct)
    {
        if (source.CanSeek && source.Length - source.Position > maxBytes)
        {
            return new([], Exceeded: true, BytesRead: source.Length - source.Position);
        }

        var output = new MemoryStream(Math.Min(maxBytes, CopyBufferBytes));
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);

        try
        {
            while (true)
            {
                var remaining = maxBytes - output.Length;
                var readLength = (int)Math.Min(buffer.Length, remaining + 1);
                var read = await source.ReadAsync(buffer.AsMemory(0, readLength), ct);
                if (read == 0)
                {
                    return new(output.ToArray(), Exceeded: false, BytesRead: output.Length);
                }

                if (output.Length + read > maxBytes)
                {
                    return new([], Exceeded: true, BytesRead: output.Length + read);
                }

                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            await output.DisposeAsync();
        }
    }

    private ZipInspection InspectZip(ReadOnlySpan<byte> bytes)
    {
        var eocd = FindEndOfCentralDirectory(bytes);
        if (eocd < 0 || eocd + 22 > bytes.Length)
        {
            return ZipInspection.Rejected(ReportPayloadRejectionCode.CorruptContainer);
        }

        var disk = ReadUInt16(bytes, eocd + 4);
        var centralDisk = ReadUInt16(bytes, eocd + 6);
        var entriesOnDisk = ReadUInt16(bytes, eocd + 8);
        var entryCount = ReadUInt16(bytes, eocd + 10);
        var centralSize = ReadUInt32(bytes, eocd + 12);
        var centralOffset = ReadUInt32(bytes, eocd + 16);
        var commentLength = ReadUInt16(bytes, eocd + 20);

        if (eocd + 22 + commentLength != bytes.Length
            || disk != 0
            || centralDisk != 0
            || entriesOnDisk != entryCount
            || entryCount == ushort.MaxValue
            || centralSize == uint.MaxValue
            || centralOffset == uint.MaxValue)
        {
            return ZipInspection.Rejected(ReportPayloadRejectionCode.UnsupportedContainer);
        }

        var centralEnd = (long)centralOffset + centralSize;
        if (centralEnd > eocd || centralEnd > bytes.Length)
        {
            return ZipInspection.Rejected(ReportPayloadRejectionCode.CorruptContainer);
        }

        long totalExpanded = 0;
        long totalCompressed = 0;
        long maxEntryExpanded = 0;
        var entryRatioExceeded = false;
        var cursor = (long)centralOffset;

        for (var i = 0; i < entryCount; i++)
        {
            if (cursor < 0 || cursor + 46 > bytes.Length
                || ReadUInt32(bytes, (int)cursor) != ZipCentralHeader)
            {
                return ZipInspection.Rejected(ReportPayloadRejectionCode.CorruptContainer);
            }

            var offset = (int)cursor;
            var flags = ReadUInt16(bytes, offset + 8);
            var method = ReadUInt16(bytes, offset + 10);
            var compressed = ReadUInt32(bytes, offset + 20);
            var expanded = ReadUInt32(bytes, offset + 24);
            var nameLength = ReadUInt16(bytes, offset + 28);
            var extraLength = ReadUInt16(bytes, offset + 30);
            var entryCommentLength = ReadUInt16(bytes, offset + 32);
            var diskStart = ReadUInt16(bytes, offset + 34);

            if ((flags & 0x0001) != 0)
            {
                return ZipInspection.Rejected(ReportPayloadRejectionCode.EncryptedContainer);
            }

            if (method is not (0 or 8) || diskStart != 0
                || compressed == uint.MaxValue || expanded == uint.MaxValue)
            {
                return ZipInspection.Rejected(ReportPayloadRejectionCode.UnsupportedContainer);
            }

            var next = cursor + 46L + nameLength + extraLength + entryCommentLength;
            if (next > centralEnd || next > bytes.Length)
            {
                return ZipInspection.Rejected(ReportPayloadRejectionCode.CorruptContainer);
            }

            totalExpanded += expanded;
            totalCompressed += compressed;
            entryRatioExceeded |= ExceedsRatio(expanded, compressed);
            if (expanded > maxEntryExpanded)
            {
                maxEntryExpanded = expanded;
            }

            cursor = next;
        }

        if (cursor != centralEnd)
        {
            return ZipInspection.Rejected(ReportPayloadRejectionCode.CorruptContainer);
        }

        return new(
            entryCount,
            maxEntryExpanded,
            totalExpanded,
            totalCompressed,
            entryRatioExceeded,
            null);
    }

    private bool ExceedsRatio(long expandedBytes, long compressedBytes)
    {
        if (expandedBytes == 0)
        {
            return false;
        }

        if (compressedBytes <= 0)
        {
            return true;
        }

        return expandedBytes > compressedBytes * _options.MaxCompressionRatio;
    }

    private static int FindEndOfCentralDirectory(ReadOnlySpan<byte> bytes)
    {
        var lowerBound = Math.Max(0, bytes.Length - (ushort.MaxValue + 22));
        for (var i = bytes.Length - 22; i >= lowerBound; i--)
        {
            if (ReadUInt32(bytes, i) == ZipEndOfCentralDirectory)
            {
                return i;
            }
        }

        return -1;
    }

    private static ContainerKind DetectContainerMagic(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 4)
        {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if (signature is ZipLocalHeader or ZipEndOfCentralDirectory or ZipDataDescriptor)
            {
                return ContainerKind.Zip;
            }

            if (bytes.StartsWith("Rar!"u8)
                || (bytes[0] == 0x37 && bytes[1] == 0x7A
                    && bytes[2] == 0xBC && bytes[3] == 0xAF))
            {
                return ContainerKind.Unsupported;
            }
        }

        if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            return ContainerKind.Gzip;
        }

        if (bytes.Length >= 262 && bytes.Slice(257, 5).SequenceEqual("ustar"u8))
        {
            return ContainerKind.Unsupported;
        }

        return ContainerKind.None;
    }

    private static ContainerKind DetectContainerLabel(string? fileName, string? mediaType)
    {
        var name = fileName ?? string.Empty;
        var mime = mediaType ?? string.Empty;

        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || mime.Equals("application/zip", StringComparison.OrdinalIgnoreCase))
        {
            return ContainerKind.Zip;
        }

        if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".gzip", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("gzip", StringComparison.OrdinalIgnoreCase)
            || mime.Equals("application/gzip", StringComparison.OrdinalIgnoreCase))
        {
            return ContainerKind.Gzip;
        }

        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".xz", StringComparison.OrdinalIgnoreCase))
        {
            return ContainerKind.Unsupported;
        }

        return ContainerKind.None;
    }

    private static bool IsNestedContainer(byte[] bytes, string? fileName, string? mediaType)
    {
        var magic = DetectContainerMagic(bytes);
        if (magic != ContainerKind.None)
        {
            return true;
        }

        // Real XML/JSON content wins over a misleading archive suffix.
        return ReportPayloadFormat.Classify(bytes) == ReportPayloadKind.Unknown
               && DetectContainerLabel(fileName, mediaType) != ContainerKind.None;
    }

    private static ReportPayloadKind ClassifyPayload(
        byte[] bytes,
        string? fileName,
        string? mediaType)
    {
        var fromContent = ReportPayloadFormat.Classify(bytes);
        return fromContent != ReportPayloadKind.Unknown
            ? fromContent
            : ReportPayloadFormat.Classify(bytes, fileName, mediaType);
    }

    private static bool IsDirectory(ZipArchiveEntry entry)
        => entry.FullName.EndsWith("/", StringComparison.Ordinal)
           || entry.FullName.EndsWith("\\", StringComparison.Ordinal);

    private static string? StripGzipSuffix(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return fileName;
        }

        if (fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            return fileName[..^3];
        }

        return fileName.EndsWith(".gzip", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^5]
            : fileName;
    }

    private static ReportPayloadExtractionResult Accepted(
        ReportPayloadKind kind,
        byte[] bytes,
        string? sourceName,
        string digest,
        long payloadBytes)
        => new(
            [new ExtractedReportPayload(kind, new MemoryStream(bytes, writable: false), sourceName ?? string.Empty)],
            [],
            digest,
            payloadBytes);

    private static ReportPayloadExtractionResult Fatal(
        ReportPayloadRejectionCode code,
        string? digest = null,
        long payloadBytes = 0)
        => new([], [new(code)], digest, payloadBytes);

    private static ReportPayloadExtractionResult FatalAndDispose(
        IEnumerable<ExtractedReportPayload> payloads,
        ReportPayloadRejectionCode code,
        string digest,
        long payloadBytes)
    {
        foreach (var payload in payloads)
        {
            payload.Stream.Dispose();
        }

        return Fatal(code, digest, payloadBytes);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));

    private enum ContainerKind
    {
        None,
        Gzip,
        Zip,
        Unsupported,
    }

    private sealed record BoundedRequestReadResult(byte[] Bytes, bool Exceeded, long BytesRead);
    private sealed record BoundedReadResult(byte[] Bytes, bool Exceeded, bool RatioExceeded);

    private sealed record ZipInspection(
        int EntryCount,
        long MaxEntryExpandedBytes,
        long TotalExpandedBytes,
        long TotalCompressedBytes,
        bool EntryRatioExceeded,
        ReportPayloadRejectionCode? Rejection)
    {
        public long MaxEntryBytes => MaxEntryExpandedBytes;

        public static ZipInspection Rejected(ReportPayloadRejectionCode code)
            => new(0, 0, 0, 0, false, code);
    }
}
