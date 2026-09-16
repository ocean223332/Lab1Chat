using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Chat.Shared;

namespace Chat.IntegrationTests;

internal static class Program
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProcessStartupTimeout = TimeSpan.FromSeconds(10);

    private static readonly (string Name, Func<Task> Run)[] Tests =
    [
        ("multiple clients receive welcome and complete user lists", TestJoinWelcomeAndListsAsync),
        ("a non-join first packet is rejected", TestHandshakeFirstPacketAsync),
        ("duplicate names are rejected case-insensitively", TestDuplicateNameRejectionAsync),
        ("equivalent Unicode name forms are treated as duplicates", TestNormalizedDuplicateNameAsync),
        ("Unicode and emoji chat uses the authoritative username", TestUnicodeEmojiAndAuthoritativeUsernameAsync),
        ("empty and oversized chats are rejected", TestChatValidationAsync),
        ("graceful leave and abrupt disconnect update user lists", TestLeaveAndDisconnectListsAsync),
        ("concurrent joins and departures converge on one user list", TestConcurrentJoinLeaveConvergenceAsync),
        ("malformed and oversized frames do not stop the server", TestMalformedFrameContainmentAsync),
        ("concurrent chats remain individually framed", TestConcurrentChatFramingAsync)
    ];

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var serverDll = ServerLocator.Resolve(args);
            Console.WriteLine($"Chat integration tests using: {serverDll}");

            var failures = 0;
            foreach (var test in Tests)
            {
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    await test.Run();
                    Console.WriteLine($"PASS {test.Name} ({stopwatch.Elapsed.TotalMilliseconds:0} ms)");
                }
                catch (Exception exception)
                {
                    failures++;
                    Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
                    if (exception.InnerException is not null)
                    {
                        Console.Error.WriteLine($"  {exception.InnerException.Message}");
                    }
                }
            }

            Console.WriteLine($"{Tests.Length - failures}/{Tests.Length} integration tests passed.");
            return failures == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Integration test harness error: {exception.Message}");
            return 2;
        }
    }

    private static async Task TestJoinWelcomeAndListsAsync()
    {
        await using var server = await ServerProcess.StartAsync(ServerLocator.Resolve(Environment.GetCommandLineArgs()));
        await using var alice = await Peer.ConnectAsync(server.Port);
        await alice.SendAsync(PacketTypes.Join, "Alice");
        var aliceWelcome = await alice.ExpectAsync(PacketTypes.Welcome);
        AssertEqual("Alice", aliceWelcome.Username, "Welcome should identify the joined member.");

        await using var bob = await Peer.ConnectAsync(server.Port);
        await bob.SendAsync(PacketTypes.Join, "Bob");
        var bobWelcome = await bob.ExpectAsync(PacketTypes.Welcome);
        AssertEqual("Bob", bobWelcome.Username, "Welcome should identify the joined member.");

        var aliceUsers = await alice.ExpectUsersAsync("Alice", "Bob");
        var bobUsers = await bob.ExpectUsersAsync("Alice", "Bob");
        AssertUsers(aliceUsers, "Alice", "Bob");
        AssertUsers(bobUsers, "Alice", "Bob");
    }

    private static async Task TestDuplicateNameRejectionAsync()
    {
        await using var server = await ServerProcess.StartAsync(ServerLocator.Resolve(Environment.GetCommandLineArgs()));
        await using var alice = await Peer.ConnectAsync(server.Port);
        await alice.SendAsync(PacketTypes.Join, "Alice");
        await alice.ExpectAsync(PacketTypes.Welcome);

        await using var duplicate = await Peer.ConnectAsync(server.Port);
        await duplicate.SendAsync(PacketTypes.Join, "alice");
        var error = await duplicate.ExpectAsync(PacketTypes.Error);
        Assert(!string.IsNullOrWhiteSpace(error.Text), "Duplicate-name rejection should include an error message.");

        var users = await alice.ExpectUsersAsync("Alice");
        AssertUsers(users, "Alice");
    }

    private static async Task TestHandshakeFirstPacketAsync()
    {
        await using var server = await ServerProcess.StartAsync(ServerLocator.Resolve(Environment.GetCommandLineArgs()));
        await using var peer = await Peer.ConnectAsync(server.Port);
        await peer.SendAsync(PacketTypes.Chat, username: "BeforeJoin", text: "This must not be accepted.");

        var error = await peer.ExpectAsync(PacketTypes.Error);
        Assert(!string.IsNullOrWhiteSpace(error.Text), "A non-join first packet should include an error message.");
    }

    private static async Task TestNormalizedDuplicateNameAsync()
    {
        await using var server = await ServerProcess.StartAsync(ServerLocator.Resolve(Environment.GetCommandLineArgs()));
        await using var decomposed = await Peer.ConnectAsync(server.Port);
        await decomposed.SendAsync(PacketTypes.Join, "Nguyễn".Normalize(NormalizationForm.FormD));
        var welcome = await decomposed.ExpectAsync(PacketTypes.Welcome);
        AssertEqual("Nguyễn", welcome.Username, "The accepted member name should be normalized to NFC.");

        await using var composed = await Peer.ConnectAsync(server.Port);
        await composed.SendAsync(PacketTypes.Join, "Nguyễn");
        var error = await composed.ExpectAsync(PacketTypes.Error);
        Assert(!string.IsNullOrWhiteSpace(error.Text), "Equivalent NFC/NFD names should be rejected as duplicates.");
    }

    private static async Task TestUnicodeEmojiAndAuthoritativeUsernameAsync()
    {
        await using var server = await ServerProcess.StartAsync(ServerLocator.Resolve(Environment.GetCommandLineArgs()));
        await using var alice = await Peer.ConnectAsync(server.Port);
        await using var bob = await Peer.ConnectAsync(server.Port);

        await alice.SendAsync(PacketTypes.Join, "Nguyễn");
        await alice.ExpectAsync(PacketTypes.Welcome);
        await bob.SendAsync(PacketTypes.Join, "😀");
        await bob.ExpectAsync(PacketTypes.Welcome);
        await alice.ExpectUsersAsync("Nguyễn", "😀");
        await bob.ExpectUsersAsync("Nguyễn", "😀");

        const string message = "Xin chào 👋 — tiếng Việt có dấu\nDòng thứ hai 日本語";
        var sentAt = DateTimeOffset.UtcNow;
        await alice.SendAsync(
            PacketTypes.Chat,
            username: "Impersonator",
            text: message,
            timestamp: DateTimeOffset.UnixEpoch);
        var receivedBy = DateTimeOffset.UtcNow.AddSeconds(1);

        var aliceChat = await alice.ExpectChatAsync(message);
        var bobChat = await bob.ExpectChatAsync(message);
        AssertEqual("Nguyễn", aliceChat.Username, "Server must set the sender to the accepted session member.");
        AssertEqual("Nguyễn", bobChat.Username, "Server must ignore an impersonated username in the chat payload.");
        AssertEqual(message, aliceChat.Text, "Unicode and emoji text must round-trip unchanged.");
        AssertEqual(message, bobChat.Text, "Unicode and emoji text must round-trip unchanged.");
        Assert(aliceChat.Timestamp >= sentAt && aliceChat.Timestamp <= receivedBy,
            "Server should replace a client-supplied epoch timestamp with its current timestamp.");
        Assert(bobChat.Timestamp >= sentAt && bobChat.Timestamp <= receivedBy,
            "Server should replace a client-supplied epoch timestamp for every recipient.");
    }

    private static async Task TestChatValidationAsync()
    {
        await using var server = await ServerProcess.StartAsync(ServerLocator.Resolve(Environment.GetCommandLineArgs()));
        await using var alice = await Peer.ConnectAsync(server.Port);
        await alice.SendAsync(PacketTypes.Join, "Alice");
        await alice.ExpectAsync(PacketTypes.Welcome);

        await alice.SendAsync(PacketTypes.Chat, username: "Alice", text: string.Empty);
        var emptyError = await alice.ExpectAsync(PacketTypes.Error);
        Assert(!string.IsNullOrWhiteSpace(emptyError.Text), "Empty chat rejection should include an error message.");

        var oversizedText = new string('x', ChatLimits.MaxMessageLength + 1);
        await alice.SendAsync(PacketTypes.Chat, username: "Alice", text: oversizedText);
        var oversizedError = await alice.ExpectAsync(PacketTypes.Error);
        Assert(!string.IsNullOrWhiteSpace(oversizedError.Text), "Oversized chat rejection should include an error message.");

        const string validMessage = "Tin nhắn hợp lệ ✅";
        await alice.SendAsync(PacketTypes.Chat, username: "Alice", text: validMessage);
        var echoed = await alice.ExpectChatAsync(validMessage);
        AssertEqual("Alice", echoed.Username, "A rejected chat must not make the member session unusable.");
    }

    private static async Task TestLeaveAndDisconnectListsAsync()
    {
        await using var server = await ServerProcess.StartAsync(ServerLocator.Resolve(Environment.GetCommandLineArgs()));
        await using var observer = await Peer.ConnectAsync(server.Port);
        await observer.SendAsync(PacketTypes.Join, "Observer");
        await observer.ExpectAsync(PacketTypes.Welcome);

        await using var graceful = await Peer.ConnectAsync(server.Port);
        await graceful.SendAsync(PacketTypes.Join, "Graceful");
        await graceful.ExpectAsync(PacketTypes.Welcome);
        await observer.ExpectUsersAsync("Observer", "Graceful");

        await graceful.SendAsync(PacketTypes.Leave, "Graceful");
        var afterGracefulLeave = await observer.ExpectUsersAsync("Observer");
        AssertUsers(afterGracefulLeave, "Observer");
        await graceful.DisposeAsync();

        await using var abrupt = await Peer.ConnectAsync(server.Port);
        await abrupt.SendAsync(PacketTypes.Join, "Abrupt");
        await abrupt.ExpectAsync(PacketTypes.Welcome);
        await observer.ExpectUsersAsync("Observer", "Abrupt");

        await abrupt.DisposeAsync();
        var afterAbruptDisconnect = await observer.ExpectUsersAsync("Observer");
        AssertUsers(afterAbruptDisconnect, "Observer");
    }

    private static async Task TestConcurrentJoinLeaveConvergenceAsync()
    {
        await using var server = await ServerProcess.StartAsync(ServerLocator.Resolve(Environment.GetCommandLineArgs()));
        await using var observer = await Peer.ConnectAsync(server.Port);
        await observer.SendAsync(PacketTypes.Join, "Observer");
        await observer.ExpectAsync(PacketTypes.Welcome);

        const int memberCount = 4;
        var members = await Task.WhenAll(
            Enumerable.Range(0, memberCount).Select(async index =>
            {
                var peer = await Peer.ConnectAsync(server.Port);
                try
                {
                    await peer.SendAsync(PacketTypes.Join, $"Member{index}");
                    await peer.ExpectAsync(PacketTypes.Welcome);
                    return peer;
                }
                catch
                {
                    await peer.DisposeAsync();
                    throw;
                }
            }));

        try
        {
            var expectedJoined = new[] { "Observer", "Member0", "Member1", "Member2", "Member3" };
            await observer.ExpectUsersAsync(expectedJoined);

            await Task.WhenAll(members.Select(member => member.DisposeAsync().AsTask()));
            var finalUsers = await observer.ExpectUsersAsync("Observer");
            AssertUsers(finalUsers, "Observer");
        }
        finally
        {
            foreach (var member in members)
            {
                await member.DisposeAsync();
            }
        }
    }

    private static async Task TestMalformedFrameContainmentAsync()
    {
        await using var server = await ServerProcess.StartAsync(ServerLocator.Resolve(Environment.GetCommandLineArgs()));
        await using var healthy = await Peer.ConnectAsync(server.Port);
        await healthy.SendAsync(PacketTypes.Join, "Healthy");
        await healthy.ExpectAsync(PacketTypes.Welcome);

        await using (var malformed = await RawPeer.ConnectAsync(server.Port))
        {
            await malformed.SendRawAsync(Encoding.UTF8.GetBytes("{ definitely-not-json }\n"));
            await malformed.WaitForCloseAsync();
        }

        await using (var oversized = await RawPeer.ConnectAsync(server.Port))
        {
            var body = "{\"type\":\"chat\",\"username\":\"bad\",\"text\":\"" +
                       new string('z', ChatLimits.MaxFrameBytes + 128) + "\"}\n";
            await oversized.SendRawAsync(Encoding.UTF8.GetBytes(body));
            await oversized.WaitForCloseAsync();
        }

        const string stillAvailable = "server vẫn hoạt động ✅";
        await healthy.SendAsync(PacketTypes.Chat, username: "Healthy", text: stillAvailable);
        var echoed = await healthy.ExpectChatAsync(stillAvailable);
        AssertEqual("Healthy", echoed.Username, "A malformed peer must not affect healthy peer state.");
    }

    private static async Task TestConcurrentChatFramingAsync()
    {
        await using var server = await ServerProcess.StartAsync(ServerLocator.Resolve(Environment.GetCommandLineArgs()));
        var peers = new List<Peer>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var peer = await Peer.ConnectAsync(server.Port);
                peers.Add(peer);
                var name = $"Sender{i}";
                await peer.SendAsync(PacketTypes.Join, name);
                await peer.ExpectAsync(PacketTypes.Welcome);
            }

            // Consume the join snapshots so the message assertions below only see chats.
            foreach (var peer in peers)
            {
                await peer.ExpectUsersAsync("Sender0", "Sender1", "Sender2", "Sender3");
            }

            const int messagesPerSender = 12;
            var sends = peers.SelectMany((peer, senderIndex) =>
                Enumerable.Range(0, messagesPerSender)
                    .Select(messageIndex => peer.SendAsync(
                        PacketTypes.Chat,
                        username: $"forged-{senderIndex}",
                        text: $"sender-{senderIndex}/message-{messageIndex} 🔢")))
                .ToArray();
            await Task.WhenAll(sends);

            var expected = new HashSet<string>(
                from senderIndex in Enumerable.Range(0, peers.Count)
                from messageIndex in Enumerable.Range(0, messagesPerSender)
                select $"sender-{senderIndex}/message-{messageIndex} 🔢",
                StringComparer.Ordinal);
            var expectedSenders = expected.ToDictionary(
                text => text,
                text => $"Sender{text[7] - '0'}",
                StringComparer.Ordinal);

            // Every client should receive every chat, and each packet must remain independently parseable.
            foreach (var peer in peers)
            {
                var received = new Dictionary<string, ChatPacket>(StringComparer.Ordinal);
                while (received.Count < expected.Count)
                {
                    var packet = await peer.ReceiveAsync();
                    if (packet.Type != PacketTypes.Chat)
                    {
                        // Join/leave announcements may be queued after the final
                        // user-list snapshot; they are not part of this framing check.
                        continue;
                    }

                    var text = packet.Text;
                    Assert(text is not null && expected.Contains(text), $"Unexpected chat text: {text}");
                    Assert(!received.ContainsKey(text!), $"Duplicate chat packet received: {text}");
                    received[text!] = packet;
                }

                AssertEqual(expected.Count, received.Count, "All concurrent chat packets should arrive exactly once per recipient.");
                foreach (var packet in received.Values)
                {
                    var packetText = packet.Text ?? throw new InvalidOperationException("Every received chat should have text.");
                    if (!expectedSenders.TryGetValue(packetText, out var expectedSender))
                    {
                        throw new InvalidOperationException($"Every received chat should have a known test message: {packetText}");
                    }

                    AssertEqual(expectedSender, packet.Username,
                        "The server should preserve the corresponding session member on concurrent chats.");
                }
            }
        }
        finally
        {
            foreach (var peer in peers)
            {
                await peer.DisposeAsync();
            }
        }
    }

    private static void AssertUsers(ChatPacket packet, params string[] expected)
    {
        AssertEqual(PacketTypes.UserList, packet.Type, "Expected a user-list packet.");
        var actual = packet.Users ?? [];
        var expectedSet = new HashSet<string>(expected, StringComparer.Ordinal);
        var actualSet = new HashSet<string>(actual, StringComparer.Ordinal);
        Assert(expectedSet.SetEquals(actualSet),
            $"Expected users [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T? actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
        }
    }

    private static class ServerLocator
    {
        public static string Resolve(string[] args)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--server", StringComparison.OrdinalIgnoreCase))
                {
                    return RequireFile(args[i + 1]);
                }
            }

            var environmentPath = Environment.GetEnvironmentVariable("CHAT_SERVER_DLL");
            if (!string.IsNullOrWhiteSpace(environmentPath))
            {
                return RequireFile(environmentPath);
            }

            var candidates = new[]
            {
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Chat.Server/bin/Release/net9.0/Chat.Server.dll")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Chat.Server/bin/Debug/net9.0/Chat.Server.dll")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../src/Chat.Server/bin/Release/net9.0/Chat.Server.dll")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../src/Chat.Server/bin/Debug/net9.0/Chat.Server.dll")),
                Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "src/Chat.Server/bin/Release/net9.0/Chat.Server.dll")),
                Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "src/Chat.Server/bin/Debug/net9.0/Chat.Server.dll"))
            };

            var found = candidates.FirstOrDefault(File.Exists);
            if (found is not null)
            {
                return found;
            }

            throw new FileNotFoundException(
                "Chat.Server.dll was not found. Build the server first or pass --server <path> (or set CHAT_SERVER_DLL).",
                candidates[0]);
        }

        private static string RequireFile(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Chat.Server.dll does not exist: {fullPath}", fullPath);
            }

            return fullPath;
        }
    }

    private sealed class ServerProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _stdoutTask;
        private readonly Task _stderrTask;
        private bool _disposed;

        private ServerProcess(Process process, int port)
        {
            _process = process;
            Port = port;
            _stdoutTask = ConsumeOutputAsync(process.StandardOutput, isError: false);
            _stderrTask = ConsumeOutputAsync(process.StandardError, isError: true);
        }

        public int Port { get; }

        public static async Task<ServerProcess> StartAsync(string serverDll)
        {
            var port = await GetFreePortAsync();
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Path.GetDirectoryName(serverDll)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(serverDll);
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(port.ToString());
            startInfo.ArgumentList.Add("--address");
            startInfo.ArgumentList.Add("127.0.0.1");

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("Could not start Chat.Server.");
            }

            var server = new ServerProcess(process, port);
            try
            {
                await server.WaitUntilReadyAsync();
                return server;
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }

        private async Task WaitUntilReadyAsync()
        {
            using var timeout = new CancellationTokenSource(ProcessStartupTimeout);
            while (!timeout.IsCancellationRequested)
            {
                if (_process.HasExited)
                {
                    var output = await GetOutputSnapshotAsync();
                    throw new InvalidOperationException($"Chat.Server exited during startup ({_process.ExitCode}).{Environment.NewLine}{output}");
                }

                try
                {
                    await using var probe = await Peer.ConnectAsync(Port, timeout.Token);
                    return;
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException)
                {
                    await Task.Delay(50, timeout.Token);
                }
                catch (IOException)
                {
                    await Task.Delay(50, timeout.Token);
                }
            }

            var finalOutput = await GetOutputSnapshotAsync();
            throw new TimeoutException($"Chat.Server did not accept connections on 127.0.0.1:{Port} within {ProcessStartupTimeout}.{Environment.NewLine}{finalOutput}");
        }

        private async Task<string> GetOutputSnapshotAsync()
        {
            // The stream consumers retain a bounded tail in OutputLog/ErrorLog.
            await Task.Yield();
            return $"stdout: {OutputLog}{Environment.NewLine}stderr: {ErrorLog}";
        }

        private readonly StringBuilder _output = new();
        private readonly StringBuilder _errors = new();
        private readonly object _outputLock = new();

        private string OutputLog
        {
            get { lock (_outputLock) return _output.ToString(); }
        }

        private string ErrorLog
        {
            get { lock (_outputLock) return _errors.ToString(); }
        }

        private async Task ConsumeOutputAsync(StreamReader reader, bool isError)
        {
            try
            {
                while (await reader.ReadLineAsync(_lifetime.Token) is { } line)
                {
                    lock (_outputLock)
                    {
                        var target = isError ? _errors : _output;
                        if (target.Length > 8192)
                        {
                            target.Remove(0, target.Length - 4096);
                        }

                        target.AppendLine(line);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during process cleanup.
            }
            catch (ObjectDisposedException)
            {
                // Expected during process cleanup.
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetime.Cancel();
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited between HasExited and Kill.
            }
            catch (TimeoutException)
            {
                // Do not block the test process indefinitely on a broken server.
            }
            finally
            {
                try
                {
                    await Task.WhenAll(_stdoutTask, _stderrTask).WaitAsync(TimeSpan.FromSeconds(1));
                }
                catch (Exception)
                {
                    // Output collection is diagnostic only.
                }

                _process.Dispose();
                _lifetime.Dispose();
            }
        }

        private static async Task<int> GetFreePortAsync()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var port = endpoint.Port;
            listener.Stop();
            await Task.Yield();
            return port;
        }
    }

    private sealed class Peer : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly JsonLineConnection _connection;
        private readonly Channel<ChatPacket> _messages = Channel.CreateUnbounded<ChatPacket>();
        private readonly CancellationTokenSource _readerLifetime = new();
        private readonly Task _readerTask;
        private bool _disposed;

        private Peer(TcpClient client)
        {
            _client = client;
            _connection = new JsonLineConnection(client.GetStream());
            _readerTask = Task.Run(ReadLoopAsync);
        }

        public static async Task<Peer> ConnectAsync(int port, CancellationToken cancellationToken = default)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                return new Peer(client);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public async Task SendAsync(
            string type,
            string? username = null,
            string? text = null,
            string[]? users = null,
            DateTimeOffset? timestamp = null)
        {
            await _connection.WriteAsync(new ChatPacket
            {
                Type = type,
                Username = username,
                Text = text,
                Users = users,
                Timestamp = timestamp ?? DateTimeOffset.UtcNow
            });
        }

        public async Task<ChatPacket> ReceiveAsync(TimeSpan? timeout = null)
        {
            return await ReadChannelAsync(timeout ?? DefaultTimeout);
        }

        public async Task<ChatPacket> ExpectAsync(string type, TimeSpan? timeout = null)
        {
            var observed = new List<string>();
            var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"Timed out waiting for '{type}'. Observed: {string.Join(", ", observed)}");
                }

                var packet = await ReadChannelAsync(remaining);
                observed.Add(packet.Type);
                if (string.Equals(packet.Type, type, StringComparison.Ordinal))
                {
                    return packet;
                }
            }
        }

        public async Task<ChatPacket> ExpectUsersAsync(params string[] expectedUsers)
        {
            var deadline = DateTime.UtcNow + DefaultTimeout;
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"Timed out waiting for user list [{string.Join(", ", expectedUsers)}].");
                }

                var packet = await ReceiveAsync(remaining);
                if (packet.Type != PacketTypes.UserList)
                {
                    continue;
                }

                var users = packet.Users ?? [];
                var expectedSet = new HashSet<string>(expectedUsers, StringComparer.Ordinal);
                if (expectedSet.SetEquals(users))
                {
                    return packet;
                }
            }
        }

        public async Task<ChatPacket> ExpectChatAsync(string text)
        {
            var deadline = DateTime.UtcNow + DefaultTimeout;
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"Timed out waiting for chat '{text}'.");
                }

                var packet = await ReceiveAsync(remaining);
                if (packet.Type == PacketTypes.Chat && packet.Text == text)
                {
                    return packet;
                }
            }
        }

        private async Task<ChatPacket> ReadChannelAsync(TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            try
            {
                return await _messages.Reader.ReadAsync(cancellation.Token);
            }
            catch (ChannelClosedException exception)
            {
                throw new InvalidOperationException("Server closed the peer connection unexpectedly.", exception);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for a packet from server after {timeout.TotalSeconds:0.#} seconds.");
            }
        }

        private async Task ReadLoopAsync()
        {
            Exception? failure = null;
            try
            {
                while (!_readerLifetime.IsCancellationRequested)
                {
                    var packet = await _connection.ReadAsync(_readerLifetime.Token);
                    if (packet is null)
                    {
                        break;
                    }

                    await _messages.Writer.WriteAsync(packet, _readerLifetime.Token);
                }
            }
            catch (OperationCanceledException) when (_readerLifetime.IsCancellationRequested)
            {
                // Expected during cleanup.
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                _messages.Writer.TryComplete(failure);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _readerLifetime.Cancel();
            try
            {
                await _connection.DisposeAsync();
            }
            catch (Exception)
            {
                // Cleanup should not hide the assertion that caused a test to fail.
            }

            _client.Dispose();
            try
            {
                await _readerTask.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception)
            {
                // Cleanup should be bounded.
            }

            _readerLifetime.Dispose();
        }
    }

    private sealed class RawPeer : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private bool _disposed;

        private RawPeer(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        public static async Task<RawPeer> ConnectAsync(int port)
        {
            var client = new TcpClient();
            try
            {
                using var timeout = new CancellationTokenSource(DefaultTimeout);
                await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                return new RawPeer(client);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public async Task SendRawAsync(byte[] bytes)
        {
            await _stream.WriteAsync(bytes);
            await _stream.FlushAsync();
        }

        public async Task WaitForCloseAsync()
        {
            var buffer = new byte[256];
            using var timeout = new CancellationTokenSource(DefaultTimeout);
            try
            {
                while (await _stream.ReadAsync(buffer, timeout.Token) is > 0)
                {
                    // A server may send an Error packet before closing; drain it.
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException("The malformed peer was not closed by the server.");
            }
            catch (IOException)
            {
                // A reset is also a valid way for the server to contain a bad peer.
            }
            catch (SocketException)
            {
                // A reset is also a valid way for the server to contain a bad peer.
            }
        }

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                _client.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }
}
