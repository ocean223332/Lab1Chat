using System.IO;
using System.Net.Sockets;
using Chat.Shared;

namespace Chat.Client;

public sealed class JoinRejectedException(string message) : Exception(message);

/// <summary>
/// Một phiên TCP sau khi join thành công. Lớp này chỉ xử lý I/O, còn UI quản lý
/// vòng đời phiên và kiểm tra generation để bỏ qua callback cũ.
/// </summary>
public sealed class ChatClientSession : IAsyncDisposable
{
    private readonly TcpClient _tcpClient;
    private readonly JsonLineConnection _connection;
    private int _disposed;
    private int _leaveSent;

    private ChatClientSession(
        TcpClient tcpClient,
        JsonLineConnection connection,
        string host,
        int port,
        string username,
        string transferToken)
    {
        _tcpClient = tcpClient;
        _connection = connection;
        Host = host;
        Port = port;
        Username = username;
        TransferToken = transferToken;
    }

    public string Host { get; }
    public int Port { get; }
    public string Username { get; }
    public string TransferToken { get; }

    public static async Task<ChatClientSession> ConnectAsync(
        string host,
        int port,
        string username,
        CancellationToken cancellationToken = default)
    {
        var tcpClient = new TcpClient { NoDelay = true };
        JsonLineConnection? connection = null;

        try
        {
            await tcpClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            connection = new JsonLineConnection(tcpClient.GetStream());
            await connection.WriteAsync(
                new ChatPacket
                {
                    Type = PacketTypes.Join,
                    Username = username,
                    Timestamp = DateTimeOffset.UtcNow
                }, cancellationToken).ConfigureAwait(false);

            var firstPacket = await connection.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (firstPacket is null)
                throw new IOException("Máy chủ đã đóng kết nối trước khi chấp nhận bạn.");

            if (string.Equals(firstPacket.Type, PacketTypes.Error, StringComparison.OrdinalIgnoreCase))
            {
                var reason = string.IsNullOrWhiteSpace(firstPacket.Text)
                    ? "Máy chủ không chấp nhận yêu cầu tham gia."
                    : firstPacket.Text.Trim();
                throw new JoinRejectedException(reason);
            }

            if (!string.Equals(firstPacket.Type, PacketTypes.Welcome, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Phản hồi tham gia từ máy chủ không hợp lệ.");

            var acceptedName = string.IsNullOrWhiteSpace(firstPacket.Username)
                ? username
                : firstPacket.Username.Trim();
            return new ChatClientSession(
                tcpClient,
                connection,
                host,
                port,
                acceptedName,
                firstPacket.TransferToken ?? string.Empty);
        }
        catch
        {
            if (connection is not null)
                await connection.DisposeAsync().ConfigureAwait(false);
            tcpClient.Dispose();
            throw;
        }
    }

    public ValueTask<ChatPacket?> ReadAsync(CancellationToken cancellationToken = default) =>
        _connection.ReadAsync(cancellationToken);

    public Task SendChatAsync(string text, CancellationToken cancellationToken = default) =>
        _connection.WriteAsync(
            new ChatPacket
            {
                Type = PacketTypes.Chat,
                Username = Username,
                Text = text,
                Timestamp = DateTimeOffset.UtcNow
            }, cancellationToken);

    public Task<ChatPacket> UploadAttachmentAsync(
        string filePath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureTransferToken();
        return AttachmentTransferClient.UploadAsync(
            Host, Port, TransferToken, filePath, progress, cancellationToken);
    }

    public Task DownloadAttachmentAsync(
        ChatPacket attachment,
        string destinationPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureTransferToken();
        return AttachmentTransferClient.DownloadAsync(
            Host, Port, TransferToken, attachment, destinationPath, progress, cancellationToken);
    }

    private void EnsureTransferToken()
    {
        if (string.IsNullOrWhiteSpace(TransferToken))
            throw new InvalidOperationException("Server chưa cấp token truyền tệp cho phiên chat.");
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            if (Interlocked.Exchange(ref _leaveSent, 1) == 0)
            {
                try
                {
                    await _connection.WriteAsync(
                        new ChatPacket
                        {
                            Type = PacketTypes.Leave,
                            Username = Username,
                            Timestamp = DateTimeOffset.UtcNow
                        }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    // Kết nối đã mất, bị hủy, hoặc một vòng đọc khác đã đóng stream;
                    // đóng tài nguyên phía client vẫn là đường lui an toàn.
                }
            }
        }
        finally
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _tcpClient.Dispose();
        }
    }

    public ValueTask DisposeAsync() => new(DisconnectAsync());
}
