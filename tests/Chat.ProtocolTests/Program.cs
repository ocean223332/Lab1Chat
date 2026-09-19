using System.Text;
using System.Text.Json;
using Chat.Shared;

var tests = new (string Name, Func<Task> Run)[]
{
    ("UTF-8 / Vietnamese / emoji / multiline round trip", RoundTripAsync),
    ("32 KiB binary file chunk fits the bounded JSON frame", FileChunkAsync),
    ("Several packets in one TCP read", CoalescedAsync),
    ("Fragmented packet, including split UTF-8 sequence", FragmentedAsync),
    ("Concurrent writes remain complete JSON frames", ConcurrentWritesAsync),
    ("Incomplete frame at EOF is rejected", IncompleteAsync),
    ("Malformed / empty / null / missing-type JSON is rejected", InvalidJsonAsync),
    ("Oversized inbound and outbound frames are rejected", OversizedAsync),
    ("Username and message validation boundaries", ValidationAsync),
    ("Cancellation and disposal are respected", LifecycleAsync)
};

var failures = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run().WaitAsync(TimeSpan.FromSeconds(10));
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL  {name}: {exception}");
    }
}

Console.WriteLine($"Protocol tests: {tests.Length - failures}/{tests.Length} passed.");
return failures == 0 ? 0 : 1;

static async Task FileChunkAsync()
{
    using var stream = new MemoryStream();
    await using var connection = new JsonLineConnection(stream, leaveOpen: true);
    var data = new byte[ChatLimits.FileChunkBytes];
    System.Security.Cryptography.RandomNumberGenerator.Fill(data);
    await connection.WriteAsync(new ChatPacket
    {
        Type = PacketTypes.FileChunk, Data = data, Offset = 512L * 1024 * 1024
    });
    Check(stream.Length <= ChatLimits.MaxFrameBytes + 1, "Encoded chunk must fit the protocol limit.");
    stream.Position = 0;
    var actual = await connection.ReadAsync();
    Check(actual?.Offset == 512L * 1024 * 1024, "Large file offset corrupted.");
    Check(actual?.Data is not null && data.SequenceEqual(actual.Data), "Binary payload corrupted.");
}

static async Task RoundTripAsync()
{
    using var stream = new MemoryStream();
    await using var connection = new JsonLineConnection(stream, leaveOpen: true);
    var original = new ChatPacket
    {
        Type = PacketTypes.Chat,
        Username = "Bình",
        Text = "Xin chào 👋 😊 👨‍👩‍👧‍👦\nDòng hai\t\"JSON\" \\ cuối",
        Timestamp = DateTimeOffset.Parse("2026-09-14T10:00:00Z")
    };
    await connection.WriteAsync(original);
    Check(stream.ToArray().Count(b => b == '\n') == 1, "Exactly one framing LF expected.");
    stream.Position = 0;
    var actual = await connection.ReadAsync();
    Check(actual == original, "Round-tripped values differ.");
    Check(await connection.ReadAsync() is null, "Clean EOF should return null.");
}

static async Task CoalescedAsync()
{
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
        "{\"type\":\"chat\",\"text\":\"one\"}\n{\"type\":\"chat\",\"text\":\"two\"}\r\n"));
    await using var connection = new JsonLineConnection(stream);
    Check((await connection.ReadAsync())?.Text == "one", "First frame missing.");
    Check((await connection.ReadAsync())?.Text == "two", "Buffered second frame missing.");
    Check(await connection.ReadAsync() is null, "Expected EOF.");
}

static async Task FragmentedAsync()
{
    using var stream = new FragmentedStream(Encoding.UTF8.GetBytes(
        "{\"type\":\"chat\",\"text\":\"Tiếng Việt 👋\\nDòng 2\"}\n"));
    await using var connection = new JsonLineConnection(stream);
    Check((await connection.ReadAsync())?.Text == "Tiếng Việt 👋\nDòng 2", "Fragmented UTF-8 failed.");
}

static async Task ConcurrentWritesAsync()
{
    using var stream = new YieldingWriteStream();
    await using var connection = new JsonLineConnection(stream, leaveOpen: true);
    await Task.WhenAll(Enumerable.Range(0, 64).Select(index => connection.WriteAsync(
        new ChatPacket { Type = PacketTypes.Chat, Text = $"Message {index} 👋" })));
    stream.Position = 0;
    var messages = new HashSet<string>();
    while (await connection.ReadAsync() is { } packet)
        messages.Add(packet.Text!);
    Check(messages.Count == 64, "Concurrent writes lost or corrupted a packet.");
}

static async Task IncompleteAsync()
{
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"type\":\"chat\"}"));
    await using var connection = new JsonLineConnection(stream);
    await ThrowsAsync<IOException>(async () => { await connection.ReadAsync(); });
}

static async Task InvalidJsonAsync()
{
    foreach (var invalid in new[] { "\n", "null\n", "{}\n", "{\"type\":null}\n", "not json\n" })
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(invalid));
        await using var connection = new JsonLineConnection(stream);
        await ThrowsAsync<JsonException>(async () => { await connection.ReadAsync(); });
    }
}

static async Task OversizedAsync()
{
    using var inbound = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', ChatLimits.MaxFrameBytes + 1)));
    await using var reader = new JsonLineConnection(inbound);
    await ThrowsAsync<IOException>(async () => { await reader.ReadAsync(); });
    using var outbound = new MemoryStream();
    await using var writer = new JsonLineConnection(outbound);
    await ThrowsAsync<IOException>(() => writer.WriteAsync(new ChatPacket
    {
        Type = PacketTypes.Chat, Text = new string('x', ChatLimits.MaxFrameBytes)
    }));
    Check(outbound.Length == 0, "Rejected oversized output must not write a partial frame.");
}

static Task ValidationAsync()
{
    Check(ChatValidation.NormalizeUsername("  Bi\u0300nh  ") == "Bình", "Names should trim and normalize NFC.");
    foreach (var valid in new[] { "An", "Bình", "Nguyễn Văn A", new string('x', 24) })
        Check(ChatValidation.ValidateUsername(valid) is null, $"Valid name rejected: {valid}");
    foreach (var invalid in new[] { "", " ", " An", "An\nBình", "An\u200b", new string('x', 25) })
        Check(ChatValidation.ValidateUsername(invalid) is not null, "Invalid name accepted.");
    foreach (var valid in new[] { "Xin chào 👋", "Line1\r\nLine2\t!", new string('x', 2000) })
        Check(ChatValidation.ValidateMessage(valid) is null, "Valid message rejected.");
    foreach (var invalid in new[] { "", " \r\n\t", "null\0character", new string('x', 2001) })
        Check(ChatValidation.ValidateMessage(invalid) is not null, "Invalid message accepted.");
    return Task.CompletedTask;
}

static async Task LifecycleAsync()
{
    using var stream = new MemoryStream();
    var connection = new JsonLineConnection(stream, leaveOpen: true);
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    await ThrowsAsync<OperationCanceledException>(async () => { await connection.ReadAsync(canceled.Token); });
    await ThrowsAsync<OperationCanceledException>(() => connection.WriteAsync(
        new ChatPacket { Type = PacketTypes.Chat, Text = "hello" }, canceled.Token));
    await connection.DisposeAsync();
    await connection.DisposeAsync();
    await ThrowsAsync<ObjectDisposedException>(async () => { await connection.ReadAsync(); });
    Check(stream.CanRead, "leaveOpen should preserve stream ownership.");
}

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
}

sealed class YieldingWriteStream : MemoryStream
{
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        foreach (var value in buffer.ToArray())
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            WriteByte(value);
        }
    }
}
