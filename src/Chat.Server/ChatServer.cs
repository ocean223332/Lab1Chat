using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
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
    private const int OutboundQueueCapacity = 64;
    private const int MaxBusyRejections = 8;

    private readonly IPAddress _address;
    private readonly int _port;
    private readonly Action<string>? _log;
    private readonly object _stateLock = new();
    private readonly object _membersLock = new();
    private readonly List<ClientSession> _members = new();
    private readonly Dictionary<string, ClientSession> _membersByUsername =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _roomEvents = new(1, 1);
    private readonly SemaphoreSlim _connectionSlots =
        new(ChatLimits.MaxUsers, ChatLimits.MaxUsers);
    private readonly SemaphoreSlim _busyRejectionSlots =
        new(MaxBusyRejections, MaxBusyRejections);
    private readonly ConcurrentDictionary<int, Task> _handlers = new();
    private readonly CancellationTokenSource _stopCts = new();
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<int, Task> _busyRejections = new();

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
        _roomEvents.Dispose();
        _connectionSlots.Dispose();
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
        var task = HandleClientWithSlotAsync(client, serverToken);
        _handlers[id] = task;
        _ = RemoveCompletedHandlerAsync(id, task);
    }

    private async Task RemoveCompletedHandlerAsync(int id, Task handler)
    {
        try
        {
            await handler.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A handler should normally contain all network errors. Keep an
            // unexpected programming/runtime error from becoming unobserved.
            Log($"Tác vụ thành viên kết thúc do lỗi: {exception.Message}");
        }
        finally
        {
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
        var task = RejectBusyClientAsync(client, serverToken);
        _busyRejections[id] = task;
        _ = RemoveCompletedRejectionAsync(id, task);
    }

    private async Task RemoveCompletedRejectionAsync(int id, Task rejection)
    {
        try
        {
            await rejection.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log($"Không thể trả lời kết nối khi phòng đầy: {exception.Message}");
        }
        finally
        {
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
                    session.SetUsername(username);
                    _members.Add(session);
                    _membersByUsername.Add(username, session);
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
        public bool IsJoined => Volatile.Read(ref _joined) != 0;

        public void SetUsername(string username)
        {
            Username = username;
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
