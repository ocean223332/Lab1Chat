using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Chat.Shared;

namespace Chat.Server;

/// <summary>
/// TCP room server.  Membership changes and packet fan-out are serialized by
/// <see cref="_roomEvents"/>, while each member has a bounded outgoing queue.
/// A slow member therefore cannot block the accept loop or the other members.
/// </summary>
public sealed class ChatServer : IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TransferIdleTimeout = TimeSpan.FromSeconds(30);
    private const int OutboundQueueCapacity = 64;
    private const int MaxBusyRejections = 8;
    private const int MaxConcurrentTransfers = 4;
    private const long MaxTemporaryStorageBytes = 8L * 1024 * 1024 * 1024;

    private readonly IPAddress _address;
    private readonly int _port;
    private readonly Action<string>? _log;
    private readonly object _stateLock = new();
    private readonly object _membersLock = new();
    private readonly List<ClientSession> _members = new();
    private readonly Dictionary<string, ClientSession> _membersByUsername =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ClientSession> _membersByTransferToken =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _roomEvents = new(1, 1);
    private readonly SemaphoreSlim _connectionSlots =
        new(ChatLimits.MaxUsers + MaxConcurrentTransfers, ChatLimits.MaxUsers + MaxConcurrentTransfers);
    private readonly SemaphoreSlim _transferSlots =
        new(MaxConcurrentTransfers, MaxConcurrentTransfers);
    private readonly SemaphoreSlim _busyRejectionSlots =
        new(MaxBusyRejections, MaxBusyRejections);
    private readonly ConcurrentDictionary<int, Task> _handlers = new();
    private readonly CancellationTokenSource _stopCts = new();
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<int, Task> _busyRejections = new();
    private readonly AttachmentStore _attachmentStore;

    private TcpListener? _listener;
    private int _started;
    private int _stopping;
    private int _nextHandlerId;
    private int _nextRejectionId;
    private int _boundPort;

    public ChatServer(IPAddress address, int port, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (port is < 0 or > 65_535)
            throw new ArgumentOutOfRangeException(nameof(port), "Cổng phải nằm trong khoảng 0 đến 65535.");

        _address = address;
        _port = port;
        _boundPort = port;
        _log = log;
        _attachmentStore = new AttachmentStore(MaxTemporaryStorageBytes);
    }

    public IPAddress Address => _address;

    /// <summary>Configured port, or the assigned port after binding port 0.</summary>
    public int Port => Volatile.Read(ref _boundPort);

    public bool IsRunning => Volatile.Read(ref _started) != 0
        && Volatile.Read(ref _stopping) == 0;

    /// <summary>
    /// Starts accepting clients and completes when the server is stopped or the
    /// supplied cancellation token is cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("Chat server chỉ được chạy một lần.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _stopCts.Token);
        var serverToken = linked.Token;

        TcpListener? listener = null;
        try
        {
            if (serverToken.IsCancellationRequested)
                return;

            lock (_stateLock)
            {
                // Start while holding the same lock used by SignalStop. This
                // closes the small stop-before-bind race: a listener cannot be
                // started after a concurrent StopAsync has detached it.
                if (serverToken.IsCancellationRequested || IsStopping)
                    return;

                listener = new TcpListener(_address, _port);
                listener.Start();
                if (listener.LocalEndpoint is IPEndPoint endpoint)
                    Volatile.Write(ref _boundPort, endpoint.Port);
                _listener = listener;
            }

            if (listener is null)
                return;

            Log($"Máy chủ đang lắng nghe tại {_address}:{Port}. Nhấn Ctrl+C để dừng.");

            while (!serverToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(serverToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (serverToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException) when (serverToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException exception)
                {
                    Log($"Không thể nhận kết nối TCP: {exception.Message}");
                    break;
                }

                if (serverToken.IsCancellationRequested || IsStopping)
                {
                    client.Dispose();
                    break;
                }

                // The slot covers the handshake too. This keeps many clients
                // that never send a join packet from consuming unbounded memory.
                if (!_connectionSlots.Wait(0))
                {
                    StartBusyRejection(client, serverToken);
                    continue;
                }

                StartClientHandler(client, serverToken);
            }
        }
        finally
        {
            SignalStop();
            TcpListener? listenerToStop;
            lock (_stateLock)
            {
                listenerToStop = _listener;
                _listener = null;
            }

            StopListener(listenerToStop);
            if (listener is not null && !ReferenceEquals(listener, listenerToStop))
                StopListener(listener);
            await WaitForHandlersAsync().ConfigureAwait(false);
            await _attachmentStore.DisposeAsync().ConfigureAwait(false);
            _completion.TrySetResult();
            Log("Máy chủ đã dừng.");
        }
    }

    /// <summary>Requests a graceful stop and waits for all client handlers.</summary>
    public async Task StopAsync()
    {
        SignalStop();
        if (Volatile.Read(ref _started) != 0)
            await _completion.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _attachmentStore.DisposeAsync().ConfigureAwait(false);
        _roomEvents.Dispose();
        _connectionSlots.Dispose();
        _transferSlots.Dispose();
        _busyRejectionSlots.Dispose();
        _stopCts.Dispose();
    }

    private bool IsStopping => Volatile.Read(ref _stopping) != 0;

    private void SignalStop()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
            return;

        try
        {
            _stopCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // DisposeAsync can race with a second stop request; the listener is
            // still closed below and all handlers have their own cleanup path.
        }

        TcpListener? listener;
        lock (_stateLock)
        {
            listener = _listener;
            _listener = null;
        }

        StopListener(listener);
    }

    private void StartClientHandler(TcpClient client, CancellationToken serverToken)
    {
        var id = Interlocked.Increment(ref _nextHandlerId);
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _handlers[id] = completion.Task;
        _ = ExecuteClientHandlerAsync(id, client, serverToken, completion);
    }

    private async Task ExecuteClientHandlerAsync(
        int id,
        TcpClient client,
        CancellationToken serverToken,
        TaskCompletionSource completion)
    {
        try
        {
            await HandleClientWithSlotAsync(client, serverToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A handler should normally contain all network errors. Keep an
            // unexpected programming/runtime error from becoming unobserved.
            Log($"Tác vụ thành viên kết thúc do lỗi: {exception.Message}");
        }
        finally
        {
            completion.TrySetResult();
            _handlers.TryRemove(id, out _);
        }
    }

    private static void StopListener(TcpListener? listener)
    {
        try
        {
            listener?.Stop();
        }
        catch (ObjectDisposedException)
        {
            // A listener that was already closed is stopped successfully.
        }
        catch (SocketException)
        {
            // A listener that was already closed is stopped successfully.
        }
    }

    private void StartBusyRejection(TcpClient client, CancellationToken serverToken)
    {
        if (!_busyRejectionSlots.Wait(0))
        {
            client.Dispose();
            return;
        }

        var id = Interlocked.Increment(ref _nextRejectionId);
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _busyRejections[id] = completion.Task;
        _ = ExecuteBusyRejectionAsync(id, client, serverToken, completion);
    }

    private async Task ExecuteBusyRejectionAsync(
        int id,
        TcpClient client,
        CancellationToken serverToken,
        TaskCompletionSource completion)
    {
        try
        {
            await RejectBusyClientAsync(client, serverToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log($"Không thể trả lời kết nối khi phòng đầy: {exception.Message}");
        }
        finally
        {
            completion.TrySetResult();
            _busyRejections.TryRemove(id, out _);
        }
    }

    private async Task RejectBusyClientAsync(TcpClient client, CancellationToken serverToken)
    {
        try
        {
            await using var connection = new JsonLineConnection(client.GetStream());
            await TryWriteDirectAsync(
                connection,
                CreateError("Phòng chat đã đủ số thành viên."),
                serverToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The client may have disconnected while the busy response was sent.
        }
        catch (ObjectDisposedException)
        {
            // The client may have disconnected while the busy response was sent.
        }
        finally
        {
            client.Dispose();
            _busyRejectionSlots.Release();
        }
    }

    private async Task HandleClientWithSlotAsync(TcpClient client, CancellationToken serverToken)
    {
        try
        {
            await HandleClientAsync(client, serverToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Client input and socket failures must not terminate the server.
            Log($"Kết nối thành viên kết thúc do lỗi: {exception.Message}");
        }
        finally
        {
            client.Dispose();
            _connectionSlots.Release();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        client.NoDelay = true;
        await using var connection = new JsonLineConnection(client.GetStream());
        var session = new ClientSession(client, connection, OutboundQueueCapacity, WriteTimeout);

        try
        {
            var firstPacket = await ReadJoinPacketAsync(session, serverToken).ConfigureAwait(false);
            if (firstPacket is null)
                return;

            if (firstPacket.Type is PacketTypes.FileUpload or PacketTypes.FileDownload)
            {
                await HandleTransferSessionAsync(connection, firstPacket, serverToken)
                    .ConfigureAwait(false);
                return;
            }

            if (!string.Equals(firstPacket.Type, PacketTypes.Join, StringComparison.Ordinal))
            {
                await TryWriteDirectAsync(
                    connection,
                    CreateError("Gói tin đầu tiên phải là yêu cầu tham gia phòng."),
                    serverToken).ConfigureAwait(false);
                return;
            }

            string username;
            try
            {
                username = ChatValidation.NormalizeUsername(firstPacket.Username);
            }
            catch (ArgumentException)
            {
                await TryWriteDirectAsync(
                    connection,
                    CreateError("Tên thành viên không hợp lệ."),
                    serverToken).ConfigureAwait(false);
                return;
            }

            string? usernameError;
            try
            {
                usernameError = ChatValidation.ValidateUsername(username);
            }
            catch (ArgumentException)
            {
                usernameError = "Tên thành viên không hợp lệ.";
            }

            if (usernameError is not null)
            {
                await TryWriteDirectAsync(
                    connection,
                    CreateError(usernameError),
                    serverToken).ConfigureAwait(false);
                return;
            }

            var joinError = await JoinRoomAsync(session, username, serverToken)
                .ConfigureAwait(false);
            if (joinError is not null)
            {
                await TryWriteDirectAsync(
                    connection,
                    CreateError(joinError),
                    serverToken).ConfigureAwait(false);
                return;
            }

            await ReadMessagesAsync(session, serverToken).ConfigureAwait(false);
        }
        finally
        {
            if (session.IsJoined)
                await RemoveMemberAsync(session, !IsStopping).ConfigureAwait(false);

            session.RequestStop();
            await session.WaitForWriterAsync().ConfigureAwait(false);
        }
    }

    private async Task<ChatPacket?> ReadJoinPacketAsync(
        ClientSession session,
        CancellationToken serverToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            serverToken, session.StopToken);
        timeout.CancelAfter(HandshakeTimeout);

        try
        {
            return await session.Connection.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (serverToken.IsCancellationRequested || IsStopping)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            await TryWriteDirectAsync(
                session.Connection,
                CreateError("Hết thời gian chờ yêu cầu tham gia phòng."),
                serverToken).ConfigureAwait(false);
            return null;
        }
        catch (JsonException)
        {
            await TryWriteDirectAsync(
                session.Connection,
                CreateError("Gói tin JSON không hợp lệ."),
                serverToken).ConfigureAwait(false);
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task HandleTransferSessionAsync(
        JsonLineConnection connection,
        ChatPacket firstPacket,
        CancellationToken serverToken)
    {
        if (!TryGetMemberByTransferToken(firstPacket.TransferToken, out var owner))
        {
            await TryWriteDirectAsync(
                connection,
                CreateError("Phiên chuyển tệp không hợp lệ hoặc đã hết hạn."),
                serverToken).ConfigureAwait(false);
            return;
        }

        if (!_transferSlots.Wait(0))
        {
            await TryWriteDirectAsync(
                connection,
                CreateError("Máy chủ đang xử lý quá nhiều tệp; hãy thử lại sau."),
                serverToken).ConfigureAwait(false);
            return;
        }

        using var transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            serverToken, owner.StopToken);
        var transferToken = transferCancellation.Token;

        try
        {
            if (firstPacket.Type == PacketTypes.FileUpload)
            {
                await HandleUploadAsync(connection, owner, firstPacket, transferToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await HandleDownloadAsync(connection, owner, firstPacket, transferToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (transferToken.IsCancellationRequested)
        {
            // The room member disconnected or the server is shutting down.
        }
        catch (TransferTimeoutException exception)
        {
            await TryWriteTransferErrorAsync(connection, exception.Message, transferToken)
                .ConfigureAwait(false);
        }
        catch (TransferProtocolException exception)
        {
            await TryWriteTransferErrorAsync(connection, exception.Message, transferToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await TryWriteTransferErrorAsync(connection, "Gói tin chuyển tệp không hợp lệ.", transferToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            await TryWriteTransferErrorAsync(connection, "Không thể đọc hoặc ghi tệp.", transferToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _transferSlots.Release();
        }
    }

    private async Task HandleUploadAsync(
        JsonLineConnection connection,
        ClientSession owner,
        ChatPacket request,
        CancellationToken transferToken)
    {
        if (!TryValidateUploadRequest(request, out var fileName, out var validationError))
            throw new TransferProtocolException(validationError);

        if (!_attachmentStore.TryReserve(
                fileName,
                request.FileSize,
                request.IsImage,
                out var reservation,
                out var reserveError)
            || reservation is null)
        {
            throw new TransferProtocolException(reserveError);
        }

        string? committedAttachmentId = null;
        try
        {
            string? expectedHash = null;
            var ready = new ChatPacket
            {
                Type = PacketTypes.FileReady,
                AttachmentId = reservation.AttachmentId,
                FileName = reservation.FileName,
                FileSize = reservation.FileSize,
                IsImage = reservation.IsImage,
                Offset = 0,
                Timestamp = DateTimeOffset.UtcNow
            };

            await using (var output = new FileStream(
                reservation.TemporaryPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = ChatLimits.FileChunkBytes * 2,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                }))
            {
                await WriteTransferPacketAsync(connection, ready, transferToken)
                    .ConfigureAwait(false);

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var imagePrefix = new byte[8];
                var imagePrefixLength = 0;
                long received = 0;

                while (true)
                {
                    var packet = await ReadTransferPacketAsync(connection, transferToken)
                        .ConfigureAwait(false);
                    if (packet is null)
                        throw new TransferProtocolException("Kết nối chuyển tệp đã đóng trước khi hoàn tất.");

                    if (packet.Type == PacketTypes.FileChunk)
                    {
                        var data = packet.Data;
                        if (data is null || data.Length == 0 || data.Length > ChatLimits.FileChunkBytes)
                            throw new TransferProtocolException("Kích thước mảnh tệp không hợp lệ.");
                        if (packet.AttachmentId is not null
                            && !string.Equals(packet.AttachmentId, reservation.AttachmentId, StringComparison.Ordinal))
                            throw new TransferProtocolException("Mã tệp không khớp.");
                        if (packet.Offset != received)
                            throw new TransferProtocolException("Offset mảnh tệp không liên tục.");
                        if (received > reservation.FileSize - data.Length)
                            throw new TransferProtocolException("Dữ liệu tải lên vượt quá kích thước đã khai báo.");

                        await output.WriteAsync(data.AsMemory(), transferToken).ConfigureAwait(false);
                        hash.AppendData(data);
                        if (imagePrefixLength < imagePrefix.Length)
                        {
                            var copyLength = Math.Min(imagePrefix.Length - imagePrefixLength, data.Length);
                            data.AsSpan(0, copyLength).CopyTo(imagePrefix.AsSpan(imagePrefixLength));
                            imagePrefixLength += copyLength;
                        }

                        received += data.Length;
                        continue;
                    }

                    if (packet.Type != PacketTypes.FileComplete)
                        throw new TransferProtocolException("Gói tin chuyển tệp không được hỗ trợ.");
                    if (packet.AttachmentId is not null
                        && !string.Equals(packet.AttachmentId, reservation.AttachmentId, StringComparison.Ordinal))
                        throw new TransferProtocolException("Mã tệp không khớp.");
                    if (received != reservation.FileSize)
                        throw new TransferProtocolException("Tệp tải lên chưa đủ dữ liệu.");
                    if (packet.FileSize != reservation.FileSize)
                        throw new TransferProtocolException("Kích thước tệp hoàn tất không khớp.");
                    if (!TryParseSha256(packet.Sha256, out var suppliedHash))
                        throw new TransferProtocolException("SHA-256 của tệp không hợp lệ.");

                    var actualHash = hash.GetHashAndReset();
                    if (!CryptographicOperations.FixedTimeEquals(actualHash, suppliedHash))
                        throw new TransferProtocolException("SHA-256 của tệp không khớp.");
                    expectedHash = Convert.ToHexString(actualHash);

                    if (reservation.IsImage
                        && !HasSupportedImageSignature(imagePrefix, imagePrefixLength))
                        throw new TransferProtocolException("Ảnh phải là PNG, JPG hoặc JPEG hợp lệ.");

                    await output.FlushAsync(transferToken).ConfigureAwait(false);
                    break;
                }
            }

            if (expectedHash is null)
                throw new TransferProtocolException("Tệp tải lên chưa hoàn tất.");

            var committed = _attachmentStore.Commit(reservation, expectedHash);
            committedAttachmentId = committed.AttachmentId;
            var complete = new ChatPacket
            {
                Type = PacketTypes.FileComplete,
                AttachmentId = committed.AttachmentId,
                FileName = committed.FileName,
                FileSize = committed.FileSize,
                Sha256 = committed.Sha256,
                IsImage = committed.IsImage,
                Timestamp = DateTimeOffset.UtcNow
            };
            await WriteTransferPacketAsync(connection, complete, transferToken)
                .ConfigureAwait(false);

            await BroadcastAttachmentAsync(owner, committed, transferToken)
                .ConfigureAwait(false);
            committedAttachmentId = null;
        }
        finally
        {
            if (committedAttachmentId is not null)
                _attachmentStore.Remove(committedAttachmentId);
            _attachmentStore.Release(reservation);
        }
    }

    private async Task HandleDownloadAsync(
        JsonLineConnection connection,
        ClientSession owner,
        ChatPacket request,
        CancellationToken transferToken)
    {
        if (!_attachmentStore.TryGet(request.AttachmentId ?? string.Empty, out var attachment)
            || attachment is null)
            throw new TransferProtocolException("Không tìm thấy tệp đính kèm.");

        var ready = new ChatPacket
        {
            Type = PacketTypes.FileReady,
            AttachmentId = attachment.AttachmentId,
            FileName = attachment.FileName,
            FileSize = attachment.FileSize,
            Sha256 = attachment.Sha256,
            IsImage = attachment.IsImage,
            Offset = 0,
            Timestamp = DateTimeOffset.UtcNow
        };
        await WriteTransferPacketAsync(connection, ready, transferToken).ConfigureAwait(false);

        long offset = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = new FileStream(
            attachment.FilePath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = ChatLimits.FileChunkBytes * 2,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
        var buffer = new byte[ChatLimits.FileChunkBytes];

        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(), transferToken)
                .ConfigureAwait(false);
            if (count == 0)
                break;
            if (offset > attachment.FileSize - count)
                throw new TransferProtocolException("Tệp lưu trên máy chủ đã thay đổi.");

            var data = buffer.AsSpan(0, count).ToArray();
            hash.AppendData(data);
            await WriteTransferPacketAsync(connection, new ChatPacket
            {
                Type = PacketTypes.FileChunk,
                AttachmentId = attachment.AttachmentId,
                FileSize = attachment.FileSize,
                Offset = offset,
                Data = data,
                Timestamp = DateTimeOffset.UtcNow
            }, transferToken).ConfigureAwait(false);
            offset += count;
        }

        if (offset != attachment.FileSize)
            throw new TransferProtocolException("Tệp lưu trên máy chủ chưa đủ dữ liệu.");

        var actualHash = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(actualHash, attachment.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new TransferProtocolException("SHA-256 của tệp lưu trên máy chủ không khớp.");

        await WriteTransferPacketAsync(connection, new ChatPacket
        {
            Type = PacketTypes.FileComplete,
            AttachmentId = attachment.AttachmentId,
            FileName = attachment.FileName,
            FileSize = attachment.FileSize,
            Sha256 = attachment.Sha256,
            IsImage = attachment.IsImage,
            Offset = offset,
            Timestamp = DateTimeOffset.UtcNow
        }, transferToken).ConfigureAwait(false);
    }

    private async Task BroadcastAttachmentAsync(
        ClientSession owner,
        StoredAttachment attachment,
        CancellationToken transferToken)
    {
        await _roomEvents.WaitAsync(transferToken).ConfigureAwait(false);
        try
        {
            if (transferToken.IsCancellationRequested || IsStopping)
                return;

            // Build a fresh metadata-only packet. In particular, never copy
            // TransferToken or Data from a transfer request into the room.
            var announcement = new ChatPacket
            {
                Type = PacketTypes.Attachment,
                Username = owner.Username,
                AttachmentId = attachment.AttachmentId,
                FileName = attachment.FileName,
                FileSize = attachment.FileSize,
                Sha256 = attachment.Sha256,
                IsImage = attachment.IsImage,
                Timestamp = DateTimeOffset.UtcNow
            };
            foreach (var recipient in SnapshotMembers())
                QueueOrStop(recipient, announcement);
        }
        finally
        {
            _roomEvents.Release();
        }
    }

    private bool TryGetMemberByTransferToken(
        string? transferToken,
        out ClientSession member)
    {
        member = null!;
        if (string.IsNullOrWhiteSpace(transferToken))
            return false;

        lock (_membersLock)
        {
            if (!_membersByTransferToken.TryGetValue(transferToken, out var candidate)
                || !candidate.IsJoined
                || candidate.IsClosing)
                return false;
            member = candidate;
            return true;
        }
    }

    private static string CreateTransferToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryValidateUploadRequest(
        ChatPacket request,
        out string fileName,
        out string error)
    {
        fileName = request.FileName ?? string.Empty;
        error = string.Empty;
        if (fileName.Length == 0 || string.IsNullOrWhiteSpace(fileName) || fileName is "." or "..")
        {
            error = "Tên tệp không hợp lệ.";
            return false;
        }
        if (fileName.Length > 255
            || fileName.IndexOfAny(['/', '\\', ':']) >= 0
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || fileName.Any(char.IsControl)
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            error = "Tên tệp không hợp lệ.";
            return false;
        }
        if (request.FileSize < 0 || request.FileSize > ChatLimits.MaxFileSize)
        {
            error = $"Tệp không được vượt quá {ChatLimits.MaxFileSize / (1024 * 1024 * 1024)} GiB.";
            return false;
        }
        if (request.IsImage)
        {
            if (request.FileSize > ChatLimits.MaxImageFileSize)
            {
                error = "Ảnh không được vượt quá 20 MiB.";
                return false;
            }

            var extension = Path.GetExtension(fileName);
            if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                error = "Ảnh phải có phần mở rộng PNG, JPG hoặc JPEG.";
                return false;
            }
        }

        return true;
    }

    private static bool TryParseSha256(string? value, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
            return false;

        try
        {
            hash = Convert.FromHexString(value);
            return hash.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool HasSupportedImageSignature(byte[] prefix, int count)
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (count >= png.Length && prefix.AsSpan(0, png.Length).SequenceEqual(png))
            return true;
        return count >= 3 && prefix[0] == 0xFF && prefix[1] == 0xD8 && prefix[2] == 0xFF;
    }

    private static async Task<ChatPacket?> ReadTransferPacketAsync(
        JsonLineConnection connection,
        CancellationToken transferToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(transferToken);
        timeout.CancelAfter(TransferIdleTimeout);
        try
        {
            return await connection.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!transferToken.IsCancellationRequested)
        {
            throw new TransferTimeoutException("Kết nối chuyển tệp không hoạt động quá lâu.");
        }
    }

    private static async Task WriteTransferPacketAsync(
        JsonLineConnection connection,
        ChatPacket packet,
        CancellationToken transferToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(transferToken);
        timeout.CancelAfter(TransferIdleTimeout);
        try
        {
            await connection.WriteAsync(packet, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!transferToken.IsCancellationRequested)
        {
            throw new TransferTimeoutException("Kết nối chuyển tệp không nhận dữ liệu đủ nhanh.");
        }
    }

    private static async Task TryWriteTransferErrorAsync(
        JsonLineConnection connection,
        string message,
        CancellationToken transferToken)
    {
        try
        {
            await WriteTransferPacketAsync(connection, CreateError(message), transferToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Origin member/server stopped.
        }
        catch (TransferTimeoutException)
        {
            // The transfer peer is not reading its error response.
        }
        catch (IOException)
        {
            // The transfer peer disconnected.
        }
        catch (ObjectDisposedException)
        {
            // The transfer peer disconnected.
        }
    }

    /// <summary>
    /// Adds a member and enqueues Welcome, UserList, and the join notice while
    /// holding the room event gate. Queueing is non-blocking and preserves order
    /// in each member's single-reader channel.
    /// </summary>
    private async Task<string?> JoinRoomAsync(
        ClientSession session,
        string username,
        CancellationToken serverToken)
    {
        await _roomEvents.WaitAsync(serverToken).ConfigureAwait(false);
        try
        {
            if (serverToken.IsCancellationRequested || IsStopping)
                return "Máy chủ đang dừng.";

            string? error = null;
            lock (_membersLock)
            {
                if (_membersByUsername.ContainsKey(username))
                    error = "Tên thành viên đã được sử dụng.";
                else if (_members.Count >= ChatLimits.MaxUsers)
                    error = "Phòng chat đã đủ số thành viên.";
                else
                {
                    session.SetIdentity(username, CreateTransferToken());
                    _members.Add(session);
                    _membersByUsername.Add(username, session);
                    _membersByTransferToken.Add(session.TransferToken, session);
                }
            }

            if (error is not null)
                return error;

            session.StartWriter();
            if (!session.TryEnqueue(new ChatPacket
            {
                Type = PacketTypes.Welcome,
                Username = username,
                Text = "Chào mừng bạn đến phòng chat.",
                TransferToken = session.TransferToken,
                Timestamp = DateTimeOffset.UtcNow
            }))
            {
                RemoveMemberState(session);
                session.RequestStop();
                return "Không thể gửi lời chào từ máy chủ.";
            }

            var recipients = SnapshotMembers();
            var users = recipients.Select(member => member.Username).ToArray();
            var userList = new ChatPacket
            {
                Type = PacketTypes.UserList,
                Users = users,
                Timestamp = DateTimeOffset.UtcNow
            };
            var notice = new ChatPacket
            {
                Type = PacketTypes.System,
                Text = $"{username} đã tham gia phòng chat.",
                Timestamp = DateTimeOffset.UtcNow
            };

            foreach (var recipient in recipients)
            {
                QueueOrStop(recipient, userList);
                QueueOrStop(recipient, notice);
            }

            Log($"{username} đã tham gia ({recipients.Length}/{ChatLimits.MaxUsers}).");
            return null;
        }
        finally
        {
            _roomEvents.Release();
        }
    }

    private async Task ReadMessagesAsync(ClientSession session, CancellationToken serverToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            serverToken, session.StopToken);

        while (!linked.Token.IsCancellationRequested)
        {
            ChatPacket? packet;
            try
            {
                packet = await session.Connection.ReadAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
            {
                break;
            }
            catch (JsonException)
            {
                await TryWriteDirectAsync(
                    session.Connection,
                    CreateError("Gói tin JSON không hợp lệ."),
                    serverToken).ConfigureAwait(false);
                break;
            }
            catch (IOException)
            {
                break;
            }

            if (packet is null)
                break;

            if (string.Equals(packet.Type, PacketTypes.Leave, StringComparison.Ordinal))
                break;

            if (!string.Equals(packet.Type, PacketTypes.Chat, StringComparison.Ordinal))
            {
                if (!QueueError(session, "Loại gói tin không được hỗ trợ trong phòng chat."))
                    break;
                continue;
            }

            string? messageError;
            try
            {
                messageError = ChatValidation.ValidateMessage(packet.Text);
            }
            catch (ArgumentException)
            {
                messageError = "Tin nhắn không hợp lệ.";
            }

            if (messageError is not null)
            {
                if (!QueueError(session, messageError))
                    break;
                continue;
            }

            // Username and Timestamp from a client are deliberately ignored.
            // The room server is the authority for both fields.
            await BroadcastChatAsync(session.Username, packet.Text!, serverToken)
                .ConfigureAwait(false);
        }
    }

    private async Task BroadcastChatAsync(
        string username,
        string text,
        CancellationToken serverToken)
    {
        await _roomEvents.WaitAsync(serverToken).ConfigureAwait(false);
        try
        {
            if (serverToken.IsCancellationRequested || IsStopping)
                return;

            var packet = new ChatPacket
            {
                Type = PacketTypes.Chat,
                Username = username,
                Text = text,
                Timestamp = DateTimeOffset.UtcNow
            };

            foreach (var recipient in SnapshotMembers())
                QueueOrStop(recipient, packet);
        }
        finally
        {
            _roomEvents.Release();
        }
    }

    private async Task RemoveMemberAsync(ClientSession session, bool announce)
    {
        await _roomEvents.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!RemoveMemberState(session))
                return;

            var recipients = SnapshotMembers();
            if (!announce || IsStopping)
                return;

            var userList = new ChatPacket
            {
                Type = PacketTypes.UserList,
                Users = recipients.Select(member => member.Username).ToArray(),
                Timestamp = DateTimeOffset.UtcNow
            };
            var notice = new ChatPacket
            {
                Type = PacketTypes.System,
                Text = $"{session.Username} đã rời phòng chat.",
                Timestamp = DateTimeOffset.UtcNow
            };

            foreach (var recipient in recipients)
            {
                QueueOrStop(recipient, userList);
                QueueOrStop(recipient, notice);
            }

            Log($"{session.Username} đã rời ({recipients.Length}/{ChatLimits.MaxUsers}).");
        }
        finally
        {
            _roomEvents.Release();
        }
    }

    private ClientSession[] SnapshotMembers()
    {
        lock (_membersLock)
            return _members.ToArray();
    }

    private bool RemoveMemberState(ClientSession session)
    {
        if (!session.TryMarkRemoved())
            return false;

        lock (_membersLock)
        {
            _members.Remove(session);
            _membersByUsername.Remove(session.Username);
            _membersByTransferToken.Remove(session.TransferToken);
        }

        return true;
    }

    private static bool QueueError(ClientSession session, string message)
    {
        var packet = CreateError(message);
        if (session.TryEnqueue(packet))
            return true;

        session.RequestStop();
        return false;
    }

    private static void QueueOrStop(ClientSession recipient, ChatPacket packet)
    {
        if (!recipient.TryEnqueue(packet))
            recipient.RequestStop();
    }

    private static ChatPacket CreateError(string message) => new()
    {
        Type = PacketTypes.Error,
        Text = message,
        Timestamp = DateTimeOffset.UtcNow
    };

    private static async Task TryWriteDirectAsync(
        JsonLineConnection connection,
        ChatPacket packet,
        CancellationToken serverToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        timeout.CancelAfter(WriteTimeout);

        try
        {
            await connection.WriteAsync(packet, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Timeout or server shutdown; the caller closes the connection.
        }
        catch (ObjectDisposedException)
        {
            // The peer disconnected while the response was being written.
        }
        catch (IOException)
        {
            // The peer disconnected while the response was being written.
        }
    }

    private async Task WaitForHandlersAsync()
    {
        while (true)
        {
            var handlers = _handlers.Values
                .Concat(_busyRejections.Values)
                .ToArray();
            if (handlers.Length == 0)
                return;

            try
            {
                await Task.WhenAll(handlers).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Log($"Một tác vụ thành viên kết thúc do lỗi khi dừng: {exception.Message}");
            }
        }
    }

    private void Log(string message)
    {
        try
        {
            (_log ?? Console.WriteLine)(message);
        }
        catch (ObjectDisposedException)
        {
            // Console can be disposed by a host while the server is unwinding.
        }
    }

    private sealed class TransferProtocolException(string message) : Exception(message);

    private sealed class TransferTimeoutException(string message) : Exception(message);

    private sealed class ClientSession
    {
        private readonly TcpClient _client;
        private readonly JsonLineConnection _connection;
        private readonly TimeSpan _writeTimeout;
        private readonly Channel<ChatPacket> _outbound;
        private readonly CancellationTokenSource _stopCts = new();
        private int _joined;
        private int _removed;
        private int _writerStarted;
        private int _closing;
        private Task? _writerTask;

        public ClientSession(
            TcpClient client,
            JsonLineConnection connection,
            int queueCapacity,
            TimeSpan writeTimeout)
        {
            _client = client;
            _connection = connection;
            _writeTimeout = writeTimeout;
            _outbound = Channel.CreateBounded<ChatPacket>(new BoundedChannelOptions(queueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        }

        public JsonLineConnection Connection => _connection;
        public CancellationToken StopToken => _stopCts.Token;
        public string Username { get; private set; } = string.Empty;
        public string TransferToken { get; private set; } = string.Empty;
        public bool IsJoined => Volatile.Read(ref _joined) != 0;
        public bool IsClosing => Volatile.Read(ref _closing) != 0;

        public void SetIdentity(string username, string transferToken)
        {
            Username = username;
            TransferToken = transferToken;
            Volatile.Write(ref _joined, 1);
        }

        public bool TryMarkRemoved() => Interlocked.Exchange(ref _removed, 1) == 0;

        public void StartWriter()
        {
            if (Interlocked.Exchange(ref _writerStarted, 1) == 0)
                _writerTask = WriterLoopAsync();
        }

        public bool TryEnqueue(ChatPacket packet)
        {
            if (Volatile.Read(ref _closing) != 0)
                return false;
            return _outbound.Writer.TryWrite(packet);
        }

        public void RequestStop()
        {
            if (Interlocked.Exchange(ref _closing, 1) != 0)
                return;

            _outbound.Writer.TryComplete();
            try
            {
                _stopCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Cleanup can race with a second close request.
            }

            try
            {
                // Closing the socket wakes a blocked asynchronous read/write.
                _client.Close();
            }
            catch (SocketException)
            {
                // Already closed.
            }
        }

        public async Task WaitForWriterAsync()
        {
            if (_writerTask is null)
                return;

            try
            {
                await _writerTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Network failures are expected during disconnect cleanup.
            }
        }

        private async Task WriterLoopAsync()
        {
            try
            {
                await foreach (var packet in _outbound.Reader.ReadAllAsync(_stopCts.Token)
                    .ConfigureAwait(false))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopCts.Token);
                    timeout.CancelAfter(_writeTimeout);
                    try
                    {
                        await _connection.WriteAsync(packet, timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        RequestStop();
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        RequestStop();
                        break;
                    }
                    catch (IOException)
                    {
                        RequestStop();
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (_stopCts.IsCancellationRequested)
            {
                // Normal close path.
            }
            catch (ChannelClosedException)
            {
                // Normal close path.
            }
            catch (ObjectDisposedException)
            {
                // Normal close path.
            }
            catch (IOException)
            {
                RequestStop();
            }
        }
    }
}
