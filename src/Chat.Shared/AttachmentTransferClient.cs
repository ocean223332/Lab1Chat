using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace Chat.Shared;

/// <summary>
/// Progress for one file transfer.  <see cref="Percent"/> is clamped to the
/// inclusive range 0..100, including for a zero-byte transfer.
/// </summary>
public sealed record TransferProgress(long BytesTransferred, long TotalBytes)
{
    public double Percent => TotalBytes <= 0
        ? (BytesTransferred >= 0 ? 100d : 0d)
        : Math.Clamp(BytesTransferred * 100d / TotalBytes, 0d, 100d);
}

/// <summary>
/// Streams attachment uploads and downloads over a short-lived TCP connection.
/// The connection is deliberately independent from the room/chat connection.
/// </summary>
public static class AttachmentTransferClient
{
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(30);
    private const long ProgressByteInterval = 1024 * 1024;
    private const long MaxSha256HexLength = 64;

    public static async Task<ChatPacket> UploadAsync(
        string host,
        int port,
        string token,
        string filePath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ValidatePort(port);

        var safeName = GetUploadFileName(filePath);
        await using var file = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            ChatLimits.FileChunkBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var totalBytes = file.Length;
        // An image extension is eligible for image validation only while it is
        // within the image limit.  Larger files with the same extension remain
        // ordinary attachments and are governed by MaxFileSize.
        var isImage = IsImageFileName(safeName) && totalBytes <= ChatLimits.MaxImageFileSize;
        ValidateFileSize(totalBytes, isImage);

        using var client = new TcpClient { NoDelay = true };
        await WithIoTimeoutAsync(
            cancellationToken,
            operationToken => client.ConnectAsync(host, port, operationToken).AsTask()).ConfigureAwait(false);

        await using var connection = new JsonLineConnection(client.GetStream());
        var progressReporter = new ProgressReporter(progress);
        var metadata = new ChatPacket
        {
            Type = PacketTypes.FileUpload,
            TransferToken = token,
            FileName = safeName,
            FileSize = totalBytes,
            IsImage = isImage,
            Timestamp = DateTimeOffset.UtcNow
        };
        await WriteAsync(connection, metadata, cancellationToken).ConfigureAwait(false);

        var ready = await ReadRequiredAsync(connection, PacketTypes.FileReady, cancellationToken)
            .ConfigureAwait(false);
        ValidateReadyMetadata(ready, token, safeName, totalBytes, isImage);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChatLimits.FileChunkBytes];
        var offset = 0L;
        while (offset < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(ChatLimits.FileChunkBytes, totalBytes - offset);
            var read = await ReadFileAsync(file, buffer.AsMemory(0, requested), cancellationToken)
                .ConfigureAwait(false);
            if (read <= 0)
                throw new IOException("The file changed or ended before its advertised size was read.");

            hash.AppendData(buffer, 0, read);
            var data = read == ChatLimits.FileChunkBytes
                ? buffer
                : buffer.AsSpan(0, read).ToArray();
            var chunk = new ChatPacket
            {
                Type = PacketTypes.FileChunk,
                TransferToken = token,
                FileName = safeName,
                FileSize = totalBytes,
                Offset = offset,
                Data = data,
                IsImage = isImage,
                Timestamp = DateTimeOffset.UtcNow
            };
            await WriteAsync(connection, chunk, cancellationToken).ConfigureAwait(false);
            offset += read;
            progressReporter.Report(offset, totalBytes);
        }

        var sha256 = Convert.ToHexString(hash.GetHashAndReset());
        var completeRequest = new ChatPacket
        {
            Type = PacketTypes.FileComplete,
            TransferToken = token,
            FileName = safeName,
            FileSize = totalBytes,
            Sha256 = sha256,
            Offset = offset,
            IsImage = isImage,
            Timestamp = DateTimeOffset.UtcNow
        };
        await WriteAsync(connection, completeRequest, cancellationToken).ConfigureAwait(false);

        var completed = await ReadRequiredAsync(connection, PacketTypes.FileComplete, cancellationToken)
            .ConfigureAwait(false);
        ValidateUploadCompletion(completed, token, safeName, totalBytes, isImage, sha256);
        progressReporter.Report(totalBytes, totalBytes, force: true);
        return completed;
    }

    public static async Task DownloadAsync(
        string host,
        int port,
        string token,
        ChatPacket attachment,
        string destinationPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ValidatePort(port);

        var attachmentMetadata = ValidateAttachmentMetadata(attachment);
        var destination = Path.GetFullPath(destinationPath);
        var destinationDirectory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new IOException("The destination must have a directory.");
        if (string.IsNullOrWhiteSpace(Path.GetFileName(destination)))
            throw new IOException("The destination must have a file name.");
        if (!Directory.Exists(destinationDirectory))
            throw new DirectoryNotFoundException(destinationDirectory);

        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.attachment.part");
        var committed = false;
        var progressReporter = new ProgressReporter(progress);

        try
        {
            using var client = new TcpClient { NoDelay = true };
            await WithIoTimeoutAsync(
                cancellationToken,
                operationToken => client.ConnectAsync(host, port, operationToken).AsTask()).ConfigureAwait(false);

            await using var connection = new JsonLineConnection(client.GetStream());
            await WriteAsync(connection, new ChatPacket
            {
                Type = PacketTypes.FileDownload,
                TransferToken = token,
                AttachmentId = attachmentMetadata.AttachmentId,
                Timestamp = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);

            var ready = await ReadRequiredAsync(connection, PacketTypes.FileReady, cancellationToken)
                .ConfigureAwait(false);
            var downloadMetadata = ValidateDownloadReady(ready, attachmentMetadata, token);

            await using var temporary = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                ChatLimits.FileChunkBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var offset = 0L;
            progressReporter.Report(0, downloadMetadata.FileSize);

            while (offset < downloadMetadata.FileSize)
            {
                var packet = await ReadPacketAsync(connection, cancellationToken).ConfigureAwait(false);
                if (packet.Type == PacketTypes.Error)
                    throw CreateServerError(packet);
                if (!string.Equals(packet.Type, PacketTypes.FileChunk, StringComparison.Ordinal))
                    throw new IOException($"Expected {PacketTypes.FileChunk}, got {packet.Type}.");
                if (packet.TransferToken is not null && !string.Equals(packet.TransferToken, token, StringComparison.Ordinal))
                    throw new IOException("The download chunk has the wrong transfer token.");
                if (!string.Equals(packet.AttachmentId, downloadMetadata.AttachmentId, StringComparison.Ordinal))
                    throw new IOException("The download chunk has the wrong attachment id.");
                if (packet.FileSize != downloadMetadata.FileSize)
                    throw new IOException("The download chunk has the wrong advertised size.");
                if (packet.Offset != offset)
                    throw new IOException($"Unexpected download offset {packet.Offset}; expected {offset}.");

                var data = packet.Data;
                if (data is null || data.Length == 0 || data.Length > ChatLimits.FileChunkBytes)
                    throw new IOException("The download chunk size is invalid.");
                if (data.Length > downloadMetadata.FileSize - offset)
                    throw new IOException("The download contains more bytes than its advertised size.");

                await WriteFileAsync(temporary, data, cancellationToken).ConfigureAwait(false);
                hash.AppendData(data);
                offset += data.Length;
                progressReporter.Report(offset, downloadMetadata.FileSize);
            }

            var expectedHash = downloadMetadata.Sha256;
            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The downloaded file hash does not match its metadata.");

            var completed = await ReadRequiredAsync(connection, PacketTypes.FileComplete, cancellationToken)
                .ConfigureAwait(false);
            ValidateDownloadCompletion(completed, attachmentMetadata, downloadMetadata, token, actualHash, offset);

            await FlushAsync(temporary, cancellationToken).ConfigureAwait(false);
            await temporary.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwrite: true);
            committed = true;
            progressReporter.Report(offset, downloadMetadata.FileSize, force: true);
        }
        finally
        {
            if (!committed)
                TryDelete(temporaryPath);
        }
    }

    private static async Task<ChatPacket> ReadRequiredAsync(
        JsonLineConnection connection,
        string expectedType,
        CancellationToken cancellationToken)
    {
        var packet = await ReadPacketAsync(connection, cancellationToken).ConfigureAwait(false);
        if (packet.Type == PacketTypes.Error)
            throw CreateServerError(packet);
        if (!string.Equals(packet.Type, expectedType, StringComparison.Ordinal))
            throw new IOException($"Expected {expectedType}, got {packet.Type}.");
        return packet;
    }

    private static async Task<ChatPacket> ReadPacketAsync(
        JsonLineConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            var packet = await WithIoTimeoutAsync(
                cancellationToken,
                operationToken => connection.ReadAsync(operationToken).AsTask()).ConfigureAwait(false);
            return packet ?? throw new IOException("The transfer server closed the connection unexpectedly.");
        }
        catch (JsonException exception)
        {
            throw new IOException("The transfer server sent malformed JSON.", exception);
        }
    }

    private static async Task WriteAsync(
        JsonLineConnection connection,
        ChatPacket packet,
        CancellationToken cancellationToken)
    {
        await WithIoTimeoutAsync(
            cancellationToken,
            operationToken => connection.WriteAsync(packet, operationToken)).ConfigureAwait(false);
    }

    private static async Task<int> ReadFileAsync(
        FileStream file,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        return await WithIoTimeoutAsync(
            cancellationToken,
            operationToken => file.ReadAsync(destination, operationToken).AsTask()).ConfigureAwait(false);
    }

    private static async Task WriteFileAsync(
        FileStream file,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        await WithIoTimeoutAsync(
            cancellationToken,
            operationToken => file.WriteAsync(data, operationToken).AsTask()).ConfigureAwait(false);
    }

    private static async Task FlushAsync(FileStream file, CancellationToken cancellationToken)
    {
        await WithIoTimeoutAsync(
            cancellationToken,
            operationToken => file.FlushAsync(operationToken)).ConfigureAwait(false);
    }

    private static async Task WithIoTimeoutAsync(
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> operation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(IoTimeout);
        try
        {
            await operation(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"A transfer I/O operation exceeded {IoTimeout.TotalSeconds:0} seconds.");
        }
    }

    private static async Task<T> WithIoTimeoutAsync<T>(
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<T>> operation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(IoTimeout);
        try
        {
            return await operation(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"A transfer I/O operation exceeded {IoTimeout.TotalSeconds:0} seconds.");
        }
    }

    private static void ValidateReadyMetadata(
        ChatPacket ready,
        string token,
        string fileName,
        long fileSize,
        bool isImage)
    {
        if (ready.TransferToken is not null && !string.Equals(ready.TransferToken, token, StringComparison.Ordinal))
            throw new IOException("The server returned the wrong transfer token.");
        if (ready.FileSize != fileSize)
            throw new IOException("The server returned the wrong upload size.");
        if (ready.FileName is not null && !string.Equals(ready.FileName, fileName, StringComparison.Ordinal))
            throw new IOException("The server returned the wrong upload file name.");
        if (ready.IsImage != isImage)
            throw new IOException("The server returned the wrong image metadata.");
    }

    private static void ValidateUploadCompletion(
        ChatPacket completed,
        string token,
        string fileName,
        long fileSize,
        bool isImage,
        string sha256)
    {
        if (completed.TransferToken is not null && !string.Equals(completed.TransferToken, token, StringComparison.Ordinal))
            throw new IOException("The server returned the wrong transfer token.");
        if (completed.FileSize != fileSize)
            throw new IOException("The server returned the wrong completed file size.");
        if (completed.FileName is not null && !string.Equals(completed.FileName, fileName, StringComparison.Ordinal))
            throw new IOException("The server returned the wrong completed file name.");
        if (completed.IsImage != isImage)
            throw new IOException("The server returned the wrong completed image metadata.");
        if (!string.Equals(completed.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The server returned the wrong completed file hash.");
        if (string.IsNullOrWhiteSpace(completed.AttachmentId))
            throw new IOException("The server did not return an attachment id.");
    }

    private static AttachmentMetadata ValidateAttachmentMetadata(ChatPacket attachment)
    {
        var attachmentId = attachment.AttachmentId;
        var fileName = attachment.FileName;
        var sha256 = attachment.Sha256;
        if (string.IsNullOrWhiteSpace(attachmentId))
            throw new IOException("The attachment does not have an id.");
        if (string.IsNullOrWhiteSpace(fileName) || !IsSafeMetadataFileName(fileName))
            throw new IOException("The attachment has an unsafe file name.");
        ValidateFileSize(attachment.FileSize, attachment.IsImage);
        ValidateHash(sha256);
        return new AttachmentMetadata(
            attachmentId,
            fileName,
            attachment.FileSize,
            attachment.IsImage,
            sha256!);
    }

    private static AttachmentMetadata ValidateDownloadReady(
        ChatPacket ready,
        AttachmentMetadata expected,
        string token)
    {
        if (ready.TransferToken is not null && !string.Equals(ready.TransferToken, token, StringComparison.Ordinal))
            throw new IOException("The server returned the wrong transfer token.");
        if (ready.AttachmentId is not null && !string.Equals(ready.AttachmentId, expected.AttachmentId, StringComparison.Ordinal))
            throw new IOException("The server returned the wrong attachment id.");
        if (ready.FileSize != expected.FileSize)
            throw new IOException("The server returned the wrong download size.");
        if (ready.FileName is not null && !string.Equals(ready.FileName, expected.FileName, StringComparison.Ordinal))
            throw new IOException("The server returned the wrong download file name.");
        if (ready.IsImage != expected.IsImage)
            throw new IOException("The server returned the wrong download image metadata.");
        if (ready.Sha256 is not null && !string.Equals(ready.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The server returned the wrong download hash.");
        return expected with { Sha256 = ready.Sha256 ?? expected.Sha256 };
    }

    private static void ValidateDownloadCompletion(
        ChatPacket completed,
        AttachmentMetadata expected,
        AttachmentMetadata ready,
        string token,
        string actualHash,
        long actualSize)
    {
        if (completed.TransferToken is not null && !string.Equals(completed.TransferToken, token, StringComparison.Ordinal))
            throw new IOException("The server returned the wrong transfer token.");
        if (!string.Equals(completed.AttachmentId, expected.AttachmentId, StringComparison.Ordinal))
            throw new IOException("The server returned the wrong completed attachment id.");
        if (completed.FileSize != actualSize || completed.FileSize != expected.FileSize)
            throw new IOException("The server returned the wrong completed download size.");
        if (!string.Equals(completed.FileName, expected.FileName, StringComparison.Ordinal))
            throw new IOException("The server returned the wrong completed download file name.");
        if (completed.IsImage != expected.IsImage)
            throw new IOException("The server returned the wrong completed image metadata.");
        if (!string.Equals(completed.Sha256, actualHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(completed.Sha256, ready.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The server returned the wrong completed download hash.");
    }

    private static string GetUploadFileName(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name) || !IsSafeMetadataFileName(name))
            throw new IOException("The file name is empty or unsafe.");
        return name;
    }

    private static bool IsSafeMetadataFileName(string fileName)
    {
        if (fileName is "." or ".." || fileName.IndexOfAny(['/', '\\', ':']) >= 0)
            return false;
        foreach (var character in fileName)
        {
            if (char.IsControl(character) || character == '\0')
                return false;
        }

        return string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal);
    }

    private static bool IsImageFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateFileSize(long fileSize, bool isImage)
    {
        if (fileSize < 0 || fileSize > ChatLimits.MaxFileSize)
            throw new IOException($"The file exceeds the maximum size of {ChatLimits.MaxFileSize} bytes.");
        if (isImage && fileSize > ChatLimits.MaxImageFileSize)
            throw new IOException($"The image exceeds the maximum size of {ChatLimits.MaxImageFileSize} bytes.");
    }

    private static void ValidateHash(string? sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256) || sha256.Length != MaxSha256HexLength)
            throw new IOException("The attachment hash is missing or malformed.");
        foreach (var character in sha256)
        {
            if (!Uri.IsHexDigit(character))
                throw new IOException("The attachment hash is malformed.");
        }
    }

    private static IOException CreateServerError(ChatPacket packet)
    {
        var message = string.IsNullOrWhiteSpace(packet.Text)
            ? "The transfer server rejected the request."
            : packet.Text.Trim();
        return new IOException(message);
    }

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > 65_535)
            throw new ArgumentOutOfRangeException(nameof(port));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Preserve the original transfer exception; cleanup is best effort.
        }
    }

    private readonly record struct AttachmentMetadata(
        string AttachmentId,
        string FileName,
        long FileSize,
        bool IsImage,
        string Sha256);

    private sealed class ProgressReporter(IProgress<TransferProgress>? progress)
    {
        private long _lastBytes = -1;
        private long _lastTimestamp;

        public void Report(long bytesTransferred, long totalBytes, bool force = false)
        {
            if (progress is null)
                return;

            var now = Stopwatch.GetTimestamp();
            var elapsed = _lastTimestamp == 0
                ? TimeSpan.MaxValue
                : Stopwatch.GetElapsedTime(_lastTimestamp, now);
            if (!force && bytesTransferred >= totalBytes)
                return;
            if (!force && bytesTransferred - _lastBytes < ProgressByteInterval && elapsed < TimeSpan.FromMilliseconds(100))
                return;

            _lastBytes = bytesTransferred;
            _lastTimestamp = now;
            progress.Report(new TransferProgress(bytesTransferred, totalBytes));
        }
    }
}
