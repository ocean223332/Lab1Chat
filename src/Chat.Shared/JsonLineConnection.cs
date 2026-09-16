using System.Text.Json;

namespace Chat.Shared;

/// <summary>
/// TCP là luồng byte, không phải luồng tin nhắn. Lớp này tách JSON bằng LF,
/// xử lý cả gói bị chia nhỏ lẫn nhiều gói trong một lần đọc, và giới hạn bộ nhớ.
/// Chỉ dùng một vòng đọc; nhiều tác vụ ghi được tuần tự hóa bằng semaphore.
/// </summary>
public sealed class JsonLineConnection(Stream stream, bool leaveOpen = false) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        PropertyNameCaseInsensitive = false
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _readBuffer = new byte[8192];
    private int _readStart;
    private int _readEnd;
    private int _disposed;

    public async ValueTask<ChatPacket?> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var frame = new MemoryStream();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_readStart == _readEnd)
            {
                _readEnd = await stream.ReadAsync(_readBuffer, cancellationToken).ConfigureAwait(false);
                _readStart = 0;
                if (_readEnd == 0)
                {
                    if (frame.Length != 0)
                        throw new IOException("Kết nối bị đóng khi gói tin chưa hoàn tất.");
                    return null;
                }
            }

            var newline = Array.IndexOf(_readBuffer, (byte)'\n', _readStart, _readEnd - _readStart);
            var segmentEnd = newline < 0 ? _readEnd : newline;
            var segmentLength = segmentEnd - _readStart;
            if (frame.Length + segmentLength > ChatLimits.MaxFrameBytes)
                throw new IOException($"Gói tin vượt quá {ChatLimits.MaxFrameBytes} byte.");

            frame.Write(_readBuffer, _readStart, segmentLength);
            _readStart = newline < 0 ? _readEnd : newline + 1;
            if (newline >= 0)
            {
                var packet = JsonSerializer.Deserialize<ChatPacket>(
                    frame.GetBuffer().AsSpan(0, (int)frame.Length), JsonOptions);
                if (packet is null || string.IsNullOrWhiteSpace(packet.Type))
                    throw new JsonException("Gói tin phải có trường type hợp lệ.");
                return packet;
            }
        }
    }

    public async Task WriteAsync(ChatPacket packet, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(packet);
        var json = JsonSerializer.SerializeToUtf8Bytes(packet, JsonOptions);
        if (json.Length > ChatLimits.MaxFrameBytes)
            throw new IOException($"Gói tin vượt quá {ChatLimits.MaxFrameBytes} byte.");

        // Gộp JSON + LF thành một lần ghi để hai tin nhắn không thể xen byte vào nhau.
        var frame = new byte[json.Length + 1];
        json.CopyTo(frame, 0);
        frame[^1] = (byte)'\n';

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (!leaveOpen)
            await stream.DisposeAsync().ConfigureAwait(false);
        // Không Dispose semaphore khi tác vụ ghi đang hủy; nó không giữ OS handle
        // (AvailableWaitHandle không được sử dụng) và được GC cùng connection.
    }
}
