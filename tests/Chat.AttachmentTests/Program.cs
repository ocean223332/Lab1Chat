using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Chat.Shared;

namespace Chat.AttachmentTests;

internal static class Program
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan LargeTestTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);
    private static string _serverDll = string.Empty;
    private static bool _runLarge;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            _serverDll = ServerLocator.Resolve(args);
            _runLarge = args.Any(static arg => string.Equals(arg, "--large", StringComparison.OrdinalIgnoreCase));

            var tests = new List<(string Name, Func<Task> Run)>
            {
                ("upload and download preserve bytes, metadata, and safe destination handling", RoundTripAsync),
                ("invalid token and unsafe metadata are rejected", InvalidTokenAndMetadataAsync),
                ("wrong size, offset, hash, and chunk limits are contained", WrongMetadataAndChunkAsync),
                ("cancellation removes partial output and preserves the old file", CancellationCleanupAsync),
                ("concurrent transfers do not block ordinary chat", ConcurrentTransfersAndChatAsync)
            };
            if (_runLarge)
                tests.Add(("512 MiB streaming round trip stays bounded", LargeRoundTripAsync));

            var failures = 0;
            foreach (var test in tests)
            {
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    await test.Run().WaitAsync(_runLarge && test.Name.StartsWith("512 MiB", StringComparison.Ordinal)
                        ? LargeTestTimeout
                        : TestTimeout);
                    Console.WriteLine($"PASS {test.Name} ({stopwatch.Elapsed.TotalSeconds:0.0}s)");
                }
                catch (Exception exception)
                {
                    failures++;
                    Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
                }
            }

            Console.WriteLine($"Attachment tests: {tests.Count - failures}/{tests.Count} passed.");
            return failures == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Attachment test harness error: {exception.Message}");
            return 2;
        }
    }

    private static async Task RoundTripAsync()
    {
        await using var server = await ServerProcess.StartAsync(_serverDll);
        await using var peer = await ChatPeer.ConnectAsync(server.Port, "Uploader");
        var directory = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(directory, "greeting.bin");
            var destinationPath = Path.Combine(directory, "downloaded.bin");
            const long sourceLength = ChatLimits.FileChunkBytes * 4L + 123;
            await WritePatternFileAsync(sourcePath, sourceLength);
            var sourceHash = await ComputeSha256Async(sourcePath);
            await File.WriteAllTextAsync(destinationPath, "old destination must survive failed transfers");

            var reports = new List<TransferProgress>();
            var completion = await AttachmentTransferClient.UploadAsync(
                "127.0.0.1",
                server.Port,
                peer.TransferToken,
                sourcePath,
                new InlineProgress<TransferProgress>(reports.Add));

            AssertEqual(PacketTypes.FileComplete, completion.Type, "Upload should end with FileComplete.");
            Assert(!string.IsNullOrWhiteSpace(completion.AttachmentId), "Upload should return an attachment id.");
            AssertEqual(sourceLength, completion.FileSize, "Upload completion size mismatch.");
            AssertEqual(sourceHash, completion.Sha256, "Upload completion hash mismatch.");
            Assert(reports.Count > 0 && reports[^1].Percent == 100d,
                "Upload progress must end at 100% after the completion acknowledgement.");

            var attachment = await peer.ExpectAttachmentAsync(completion.AttachmentId!);
            AssertEqual("greeting.bin", attachment.FileName, "Room metadata should preserve the safe file name.");
            AssertEqual(sourceLength, attachment.FileSize, "Room metadata size mismatch.");
            AssertEqual(sourceHash, attachment.Sha256, "Room metadata hash mismatch.");

            var downloadReports = new List<TransferProgress>();
            await AttachmentTransferClient.DownloadAsync(
                "127.0.0.1",
                server.Port,
                peer.TransferToken,
                attachment,
                destinationPath,
                new InlineProgress<TransferProgress>(downloadReports.Add));

            AssertEqual(sourceLength, new FileInfo(destinationPath).Length, "Downloaded file length mismatch.");
            AssertEqual(sourceHash, await ComputeSha256Async(destinationPath), "Downloaded bytes hash mismatch.");
            Assert(downloadReports.Count > 0 && downloadReports[^1].Percent == 100d,
                "Download progress must end at 100% after verification and commit.");
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static async Task LargeRoundTripAsync()
    {
        using var transferTimeout = new CancellationTokenSource(LargeTestTimeout);
        var cancellationToken = transferTimeout.Token;
        await using var server = await ServerProcess.StartAsync(_serverDll);
        await using var peer = await ChatPeer.ConnectAsync(server.Port, "LargeUploader");
        var directory = CreateTempDirectory();
        try
        {
            const long length = 512L * 1024 * 1024;
            var sourcePath = Path.Combine(directory, "large-stream.bin");
            var destinationPath = Path.Combine(directory, "large-stream.download.bin");
            await WritePatternFileAsync(sourcePath, length, cancellationToken);
            var sourceHash = await ComputeSha256Async(sourcePath, cancellationToken);
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var baselineMemory = process.PrivateMemorySize64;
            var peakMemory = baselineMemory;
            server.RefreshProcess();
            var baselineServerMemory = server.PrivateMemorySize64;
            var peakServerMemory = baselineServerMemory;
            using var samplerCts = new CancellationTokenSource();
            var sampler = Task.Run(async () =>
            {
                while (!samplerCts.IsCancellationRequested)
                {
                    process.Refresh();
                    peakMemory = Math.Max(peakMemory, process.PrivateMemorySize64);
                    server.RefreshProcess();
                    peakServerMemory = Math.Max(peakServerMemory, server.PrivateMemorySize64);
                    try
                    {
                        await Task.Delay(100, samplerCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });

            try
            {
                var completion = await AttachmentTransferClient.UploadAsync(
                    "127.0.0.1", server.Port, peer.TransferToken, sourcePath,
                    cancellationToken: cancellationToken);
                var attachment = await peer.ExpectAttachmentAsync(completion.AttachmentId!);
                await AttachmentTransferClient.DownloadAsync(
                    "127.0.0.1", server.Port, peer.TransferToken, attachment, destinationPath,
                    cancellationToken: cancellationToken);
            }
            finally
            {
                samplerCts.Cancel();
                try { await sampler; }
                catch (Exception) { }
            }
            AssertEqual(length, new FileInfo(destinationPath).Length, "Large round-trip length mismatch.");
            AssertEqual(sourceHash, await ComputeSha256Async(destinationPath, cancellationToken), "Large round-trip hash mismatch.");
            Assert(peakMemory - baselineMemory < 256L * 1024 * 1024,
                $"Streaming transfer memory grew by {peakMemory - baselineMemory} bytes.");
            Assert(peakServerMemory - baselineServerMemory < 256L * 1024 * 1024,
                $"Server streaming transfer memory grew by {peakServerMemory - baselineServerMemory} bytes.");
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static async Task InvalidTokenAndMetadataAsync()
    {
        await using var server = await ServerProcess.StartAsync(_serverDll);
        await using var peer = await ChatPeer.ConnectAsync(server.Port, "TokenTester");
        var directory = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(directory, "small.bin");
            await WritePatternFileAsync(sourcePath, 7);

            await ThrowsIOExceptionAsync(() => AttachmentTransferClient.UploadAsync(
                "127.0.0.1", server.Port, "definitely-invalid-token", sourcePath));

            var validHash = new string('0', 64);
            var unsafeAttachment = new ChatPacket
            {
                Type = PacketTypes.Attachment,
                AttachmentId = "missing",
                FileName = "..\\escape.bin",
                FileSize = 7,
                Sha256 = validHash,
                Timestamp = DateTimeOffset.UtcNow
            };
            await ThrowsIOExceptionAsync(() => AttachmentTransferClient.DownloadAsync(
                "127.0.0.1", server.Port, peer.TransferToken, unsafeAttachment,
                Path.Combine(directory, "unsafe-output.bin")));

            var oversized = unsafeAttachment with
            {
                FileName = "oversized.bin",
                FileSize = ChatLimits.MaxFileSize + 1
            };
            await ThrowsIOExceptionAsync(() => AttachmentTransferClient.DownloadAsync(
                "127.0.0.1", server.Port, peer.TransferToken, oversized,
                Path.Combine(directory, "oversized-output.bin")));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static async Task WrongMetadataAndChunkAsync()
    {
        await using var server = await ServerProcess.StartAsync(_serverDll);
        await using var peer = await ChatPeer.ConnectAsync(server.Port, "ProtocolTester");

        await using (var wrongOffset = await TransferPeer.ConnectAsync(server.Port))
        {
            await wrongOffset.SendAsync(new ChatPacket
            {
                Type = PacketTypes.FileUpload,
                TransferToken = peer.TransferToken,
                FileName = "offset.bin",
                FileSize = 4,
                Timestamp = DateTimeOffset.UtcNow
            });
            await wrongOffset.ExpectAsync(PacketTypes.FileReady);
            await wrongOffset.SendAsync(new ChatPacket
            {
                Type = PacketTypes.FileChunk,
                Offset = 1,
                Data = [1, 2, 3, 4],
                FileSize = 4,
                Timestamp = DateTimeOffset.UtcNow
            });
            await wrongOffset.ExpectAsync(PacketTypes.Error);
        }

        await using (var wrongSize = await TransferPeer.ConnectAsync(server.Port))
        {
            await wrongSize.SendAsync(new ChatPacket
            {
                Type = PacketTypes.FileUpload,
                TransferToken = peer.TransferToken,
                FileName = "size.bin",
                FileSize = 4,
                Timestamp = DateTimeOffset.UtcNow
            });
            await wrongSize.ExpectAsync(PacketTypes.FileReady);
            var data = new byte[] { 1, 2, 3 };
            await wrongSize.SendAsync(new ChatPacket
            {
                Type = PacketTypes.FileChunk,
                Offset = 0,
                Data = data,
                FileSize = 4,
                Timestamp = DateTimeOffset.UtcNow
            });
            await wrongSize.SendAsync(new ChatPacket
            {
                Type = PacketTypes.FileComplete,
                FileSize = 4,
                Sha256 = Convert.ToHexString(SHA256.HashData(data)),
                Timestamp = DateTimeOffset.UtcNow
            });
            await wrongSize.ExpectAsync(PacketTypes.Error);
        }

        await using (var wrongHash = await TransferPeer.ConnectAsync(server.Port))
        {
            await wrongHash.SendAsync(new ChatPacket
            {
                Type = PacketTypes.FileUpload,
                TransferToken = peer.TransferToken,
                FileName = "hash.bin",
                FileSize = 4,
                Timestamp = DateTimeOffset.UtcNow
            });
            await wrongHash.ExpectAsync(PacketTypes.FileReady);
            var data = new byte[] { 4, 3, 2, 1 };
            await wrongHash.SendAsync(new ChatPacket
            {
                Type = PacketTypes.FileChunk,
                Offset = 0,
                Data = data,
                FileSize = 4,
                Timestamp = DateTimeOffset.UtcNow
            });
            await wrongHash.SendAsync(new ChatPacket
            {
                Type = PacketTypes.FileComplete,
                FileSize = 4,
                Sha256 = new string('0', 64),
                Timestamp = DateTimeOffset.UtcNow
            });
            await wrongHash.ExpectAsync(PacketTypes.Error);
        }

        await using (var oversizedChunk = await TransferPeer.ConnectAsync(server.Port))
        {
            await oversizedChunk.SendAsync(new ChatPacket
            {
                Type = PacketTypes.FileUpload,
                TransferToken = peer.TransferToken,
                FileName = "chunk.bin",
                FileSize = ChatLimits.FileChunkBytes + 1L,
                Timestamp = DateTimeOffset.UtcNow
            });
            await oversizedChunk.ExpectAsync(PacketTypes.FileReady);
            await oversizedChunk.SendAsync(new ChatPacket
            {
                Type = PacketTypes.FileChunk,
                Offset = 0,
                Data = new byte[ChatLimits.FileChunkBytes + 1],
                FileSize = ChatLimits.FileChunkBytes + 1L,
                Timestamp = DateTimeOffset.UtcNow
            });
            await oversizedChunk.ExpectAsync(PacketTypes.Error);
        }
    }

    private static async Task CancellationCleanupAsync()
    {
        await using var server = await ServerProcess.StartAsync(_serverDll);
        await using var peer = await ChatPeer.ConnectAsync(server.Port, "CancellationTester");
        var directory = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(directory, "cancel-source.bin");
            var destinationPath = Path.Combine(directory, "existing.bin");
            await WritePatternFileAsync(sourcePath, ChatLimits.FileChunkBytes * 128L + 1);
            await File.WriteAllTextAsync(destinationPath, "preserve this previous destination");

            var completion = await AttachmentTransferClient.UploadAsync(
                "127.0.0.1", server.Port, peer.TransferToken, sourcePath);
            var attachment = await peer.ExpectAttachmentAsync(completion.AttachmentId!);
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress<TransferProgress>(value =>
            {
                if (value.BytesTransferred > 0)
                    cancellation.Cancel();
            });

            await ThrowsOperationCanceledAsync(() => AttachmentTransferClient.DownloadAsync(
                "127.0.0.1", server.Port, peer.TransferToken, attachment, destinationPath,
                progress, cancellation.Token));

            AssertEqual("preserve this previous destination", await File.ReadAllTextAsync(destinationPath),
                "A canceled download must preserve the existing destination.");
            Assert(!Directory.EnumerateFiles(directory, ".*.attachment.part").Any(),
                "A canceled download must remove its temporary file.");
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static async Task ConcurrentTransfersAndChatAsync()
    {
        await using var server = await ServerProcess.StartAsync(_serverDll);
        await using var uploader = await ChatPeer.ConnectAsync(server.Port, "ConcurrentUploader");
        await using var observer = await ChatPeer.ConnectAsync(server.Port, "ConcurrentObserver");
        var directory = CreateTempDirectory();
        try
        {
            var sourceA = Path.Combine(directory, "concurrent-a.bin");
            var sourceB = Path.Combine(directory, "concurrent-b.bin");
            await WritePatternFileAsync(sourceA, ChatLimits.FileChunkBytes * 64L + 17);
            await WritePatternFileAsync(sourceB, ChatLimits.FileChunkBytes * 64L + 31);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var progress = new InlineProgress<TransferProgress>(_ =>
            {
                if (started.TrySetResult(true))
                    Thread.Sleep(1000);
            });

            var uploadA = AttachmentTransferClient.UploadAsync(
                "127.0.0.1", server.Port, uploader.TransferToken, sourceA, progress);
            var uploadB = AttachmentTransferClient.UploadAsync(
                "127.0.0.1", server.Port, observer.TransferToken, sourceB, progress);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            const string chatDuringTransfer = "chat remains available during transfer ✅";
            await observer.SendChatAsync(chatDuringTransfer);
            await uploader.ExpectChatAsync(chatDuringTransfer);
            await observer.ExpectChatAsync(chatDuringTransfer);

            var completions = await Task.WhenAll(uploadA, uploadB);
            await observer.ExpectAttachmentAsync(completions[0].AttachmentId!);
            await observer.ExpectAttachmentAsync(completions[1].AttachmentId!);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Lab1ChatAttachmentTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WritePatternFileAsync(
        string path,
        long length,
        CancellationToken cancellationToken = default)
    {
        await using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            ChatLimits.FileChunkBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[ChatLimits.FileChunkBytes];
        long offset = 0;
        while (offset < length)
        {
            var count = (int)Math.Min(buffer.Length, length - offset);
            for (var index = 0; index < count; index++)
                buffer[index] = unchecked((byte)((offset + index) * 31 + 17));
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            offset += count;
        }
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            ChatLimits.FileChunkBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChatLimits.FileChunkBytes];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0)
                break;
            hash.AppendData(buffer, 0, count);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Test cleanup should not hide the original assertion.
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T? actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
    }

    private static async Task ThrowsIOExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (IOException)
        {
            return;
        }

        throw new InvalidOperationException("Expected an IOException.");
    }

    private static async Task ThrowsOperationCanceledAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException("Expected an OperationCanceledException.");
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private static class ServerLocator
    {
        public static string Resolve(string[] args)
        {
            for (var index = 0; index < args.Length - 1; index++)
            {
                if (string.Equals(args[index], "--server", StringComparison.OrdinalIgnoreCase))
                    return RequireFile(args[index + 1]);
            }

            var environment = Environment.GetEnvironmentVariable("CHAT_SERVER_DLL");
            if (!string.IsNullOrWhiteSpace(environment))
                return RequireFile(environment);

            var candidates = new[]
            {
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Chat.Server/bin/Release/net9.0/Chat.Server.dll")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Chat.Server/bin/Debug/net9.0/Chat.Server.dll")),
                Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "src/Chat.Server/bin/Release/net9.0/Chat.Server.dll")),
                Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "src/Chat.Server/bin/Debug/net9.0/Chat.Server.dll"))
            };
            var found = candidates.FirstOrDefault(File.Exists);
            return found ?? throw new FileNotFoundException(
                "Chat.Server.dll was not found; build it or pass --server <path>.",
                candidates[0]);
        }

        private static string RequireFile(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"Chat.Server.dll does not exist: {fullPath}", fullPath);
            return fullPath;
        }
    }

    private sealed class ServerProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly CancellationTokenSource _outputLifetime = new();
        private readonly StringBuilder _stdout = new();
        private readonly StringBuilder _stderr = new();
        private readonly object _outputGate = new();
        private readonly Task _stdoutTask;
        private readonly Task _stderrTask;
        private bool _disposed;

        private ServerProcess(Process process, int port)
        {
            _process = process;
            Port = port;
            _stdoutTask = ConsumeAsync(process.StandardOutput, _stdout);
            _stderrTask = ConsumeAsync(process.StandardError, _stderr);
        }

        public int Port { get; }

        public long PrivateMemorySize64 => _process.PrivateMemorySize64;

        public void RefreshProcess() => _process.Refresh();

        public static async Task<ServerProcess> StartAsync(string serverDll)
        {
            var port = GetFreePort();
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
            startInfo.ArgumentList.Add("--address");
            startInfo.ArgumentList.Add("127.0.0.1");
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(port.ToString());

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
                throw new InvalidOperationException("Could not start Chat.Server.");

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
            using var timeout = new CancellationTokenSource(StartupTimeout);
            while (true)
            {
                if (_process.HasExited)
                    throw new InvalidOperationException($"Chat.Server exited during startup. {OutputSnapshot()}");
                try
                {
                    using var probe = new TcpClient();
                    await probe.ConnectAsync(IPAddress.Loopback, Port, timeout.Token);
                    return;
                }
                catch (SocketException) when (!timeout.IsCancellationRequested)
                {
                    await Task.Delay(50, timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    throw new TimeoutException($"Chat.Server did not listen on port {Port}. {OutputSnapshot()}");
                }
            }
        }

        private async Task ConsumeAsync(StreamReader reader, StringBuilder target)
        {
            try
            {
                while (await reader.ReadLineAsync(_outputLifetime.Token) is { } line)
                {
                    lock (_outputGate)
                    {
                        if (target.Length > 8192)
                            target.Remove(0, target.Length - 4096);
                        target.AppendLine(line);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private string OutputSnapshot()
        {
            lock (_outputGate)
                return $"stdout: {_stdout}{Environment.NewLine}stderr: {_stderr}";
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            _outputLifetime.Cancel();
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
            }
            catch (TimeoutException)
            {
            }
            finally
            {
                try { await Task.WhenAll(_stdoutTask, _stderrTask).WaitAsync(TimeSpan.FromSeconds(1)); }
                catch (Exception) { }
                _process.Dispose();
                _outputLifetime.Dispose();
            }
        }

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    private sealed class TransferPeer : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly JsonLineConnection _connection;
        private bool _disposed;

        private TransferPeer(TcpClient client)
        {
            _client = client;
            _connection = new JsonLineConnection(client.GetStream());
        }

        public static async Task<TransferPeer> ConnectAsync(int port)
        {
            var client = new TcpClient { NoDelay = true };
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                return new TransferPeer(client);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public Task SendAsync(ChatPacket packet) => _connection.WriteAsync(packet);

        public async Task<ChatPacket> ReceiveAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var packet = await _connection.ReadAsync(timeout.Token);
            return packet ?? throw new IOException("Transfer server closed the test connection.");
        }

        public async Task<ChatPacket> ExpectAsync(string packetType)
        {
            while (true)
            {
                var packet = await ReceiveAsync();
                if (packet.Type == packetType)
                    return packet;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            await _connection.DisposeAsync();
            _client.Dispose();
        }
    }

    private sealed class ChatPeer : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly JsonLineConnection _connection;
        private readonly Channel<ChatPacket> _inbound = Channel.CreateUnbounded<ChatPacket>();
        private readonly Queue<ChatPacket> _pending = new();
        private readonly CancellationTokenSource _readerLifetime = new();
        private readonly Task _readerTask;
        private bool _disposed;

        private ChatPeer(TcpClient client, JsonLineConnection connection, string transferToken)
        {
            _client = client;
            _connection = connection;
            TransferToken = transferToken;
            _readerTask = Task.Run(ReadLoopAsync);
        }

        public int Port => ((IPEndPoint)_client.Client.RemoteEndPoint!).Port;
        public string TransferToken { get; }

        public static async Task<ChatPeer> ConnectAsync(int port, string username)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                var connection = new JsonLineConnection(client.GetStream());
                await connection.WriteAsync(new ChatPacket
                {
                    Type = PacketTypes.Join,
                    Username = username,
                    Timestamp = DateTimeOffset.UtcNow
                }, timeout.Token);
                var welcome = await connection.ReadAsync(timeout.Token);
                if (welcome is null)
                    throw new IOException("Server closed the chat connection before welcome.");
                if (welcome.Type == PacketTypes.Error)
                    throw new IOException(welcome.Text ?? "Join rejected.");
                if (welcome.Type != PacketTypes.Welcome || string.IsNullOrWhiteSpace(welcome.TransferToken))
                    throw new IOException("Welcome did not include a transfer token.");
                return new ChatPeer(client, connection, welcome.TransferToken);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public async Task SendChatAsync(string text)
        {
            await _connection.WriteAsync(new ChatPacket
            {
                Type = PacketTypes.Chat,
                Text = text,
                Timestamp = DateTimeOffset.UtcNow
            });
        }

        public async Task<ChatPacket> ExpectAttachmentAsync(string attachmentId)
        {
            return await ReceiveMatchingAsync(packet =>
                packet.Type == PacketTypes.Attachment
                && string.Equals(packet.AttachmentId, attachmentId, StringComparison.Ordinal));
        }

        public async Task<ChatPacket> ExpectChatAsync(string text)
        {
            return await ReceiveMatchingAsync(packet => packet.Type == PacketTypes.Chat && packet.Text == text);
        }

        private async Task<ChatPacket> ReceiveMatchingAsync(Func<ChatPacket, bool> predicate)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            var pendingCount = _pending.Count;
            for (var index = 0; index < pendingCount; index++)
            {
                var pending = _pending.Dequeue();
                if (predicate(pending))
                    return pending;
                _pending.Enqueue(pending);
            }

            while (true)
            {
                var packet = await ReceiveChannelAsync(deadline - DateTime.UtcNow);
                if (predicate(packet))
                    return packet;
                _pending.Enqueue(packet);
            }
        }

        private async Task<ChatPacket> ReceiveAsync(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
                throw new TimeoutException("Timed out waiting for chat packet.");
            if (_pending.TryDequeue(out var pending))
                return pending;
            return await ReceiveChannelAsync(timeout);
        }

        private async Task<ChatPacket> ReceiveChannelAsync(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
                throw new TimeoutException("Timed out waiting for chat packet.");
            using var cancellation = new CancellationTokenSource(timeout);
            try
            {
                return await _inbound.Reader.ReadAsync(cancellation.Token);
            }
            catch (ChannelClosedException exception)
            {
                throw new IOException("Chat server closed the connection.", exception);
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
                        break;
                    await _inbound.Writer.WriteAsync(packet, _readerLifetime.Token);
                }
            }
            catch (OperationCanceledException) when (_readerLifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                _inbound.Writer.TryComplete(failure);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            _readerLifetime.Cancel();
            await _connection.DisposeAsync();
            _client.Dispose();
            try { await _readerTask.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (Exception) { }
            _readerLifetime.Dispose();
        }
    }
}
