using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Chat.Shared;

namespace Chat.Client;

public partial class MainWindow : Window
{
    private enum ConnectionState
    {
        Offline,
        Connecting,
        Connected,
        Disconnecting
    }

    private const int MaxVisibleMessages = 1000;
    private const int MaxRetainedPreviewCount = 30;
    private const long MaxRetainedPreviewBytes = 128L * 1024 * 1024;
    private const int MaxPreviewDecodeWidth = 800;
    private const int MaxPreviewDecodeHeight = 4096;
    private const long MaxPreviewPixels = 32L * 1024 * 1024;
    private static readonly TimeSpan PreviewRetryWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(1);

    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public ObservableCollection<string> OnlineUsers { get; } = [];

    private ChatClientSession? _session;
    private CancellationTokenSource? _sessionCts;
    private Task? _connectionTask;
    private long _connectionGeneration;
    private ConnectionState _connectionState;
    private bool _isClosing;
    private bool _isSending;
    private bool _keepMessagesAtBottom = true;
    private int _emojiCaretIndex;
    private int _emojiSelectionLength;
    private readonly SemaphoreSlim _previewSemaphore = new(2, 2);
    private readonly List<ChatAttachmentViewModel> _retainedPreviews = [];
    private long _retainedPreviewBytes;
    private CancellationTokenSource? _activeTransferCts;
    private Task? _activeTransferTask;
    private long _transferGeneration;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        UsernameInput.MaxLength = ChatLimits.MaxUsernameLength;
        MessageInput.MaxLength = ChatLimits.MaxMessageLength;
        BuildEmojiPicker();
        UpdateMessageCounter();
        SetConnectionState(ConnectionState.Offline);
        UpdateMessageView();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await EmojiCatalog.PreloadAsync();
            if (!_isClosing)
                BuildEmojiPicker();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or AggregateException or NotSupportedException)
        {
            FooterStatusText.Text = "Emoji màu cục bộ không tải được; vẫn có thể dùng emoji Unicode.";
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing || _connectionState == ConnectionState.Disconnecting)
            return;

        if (_connectionState == ConnectionState.Connected)
        {
            await DisconnectCurrentAsync("Bạn đã ngắt kết nối khỏi phòng chat.", addSystemMessage: true);
            return;
        }

        if (_connectionState == ConnectionState.Connecting)
            return;

        if (!TryReadConnectionSettings(out var host, out var port, out var username))
            return;

        await StartConnectionAsync(host, port, username);
    }

    private async void ImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Chọn ảnh để gửi",
            Filter = "Ảnh PNG/JPEG|*.png;*.jpg;*.jpeg|Tất cả tệp|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
            await StartUploadAsync(dialog.FileName);
    }

    private async void FileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Chọn tệp để gửi",
            Filter = "Tất cả tệp|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
            await StartUploadAsync(dialog.FileName);
    }

    private async Task StartUploadAsync(string filePath)
    {
        if (_connectionState != ConnectionState.Connected || _session is null)
            return;
        if (_activeTransferTask is { IsCompleted: false })
        {
            SetValidation("Đang có một tệp được tải lên. Hãy chờ hoặc bấm Hủy.");
            return;
        }

        FileInfo fileInfo;
        try
        {
            fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
                throw new FileNotFoundException("Không tìm thấy tệp.", filePath);
            if (fileInfo.Length > ChatLimits.MaxFileSize)
                throw new IOException("Tệp vượt giới hạn 2 GiB.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetValidation(exception.Message);
            return;
        }

        var session = _session;
        var generation = Volatile.Read(ref _connectionGeneration);
        using var transferCts = CancellationTokenSource.CreateLinkedTokenSource(
            _sessionCts?.Token ?? CancellationToken.None);
        _activeTransferCts = transferCts;
        var transferGeneration = Interlocked.Increment(ref _transferGeneration);
        SetTransferPanel(true, $"Đang chuẩn bị {fileInfo.Name}...");

        var operation = UploadAndAnnounceAsync(
            session,
            filePath,
            fileInfo,
            generation,
            transferGeneration,
            transferCts.Token);
        _activeTransferTask = operation;
        try
        {
            await operation;
        }
        finally
        {
            if (ReferenceEquals(_activeTransferTask, operation))
                _activeTransferTask = null;
            if (ReferenceEquals(_activeTransferCts, transferCts))
                _activeTransferCts = null;
            if (IsCurrentGeneration(generation))
                SetTransferPanel(false);
        }
    }

    private async Task UploadAndAnnounceAsync(
        ChatClientSession session,
        string filePath,
        FileInfo fileInfo,
        long generation,
        long transferGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            var progress = new Progress<TransferProgress>(value =>
            {
                if (IsCurrentGeneration(generation)
                    && transferGeneration == Volatile.Read(ref _transferGeneration))
                {
                    TransferProgressBar.Value = value.Percent;
                    TransferStatusText.Text = $"Đang tải {fileInfo.Name} • {value.Percent:0}%";
                }
            });
            await session.UploadAttachmentAsync(filePath, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentGeneration(generation) || !ReferenceEquals(_session, session))
                return;

            if (IsCurrentGeneration(generation))
            {
                TransferProgressBar.Value = 100;
                TransferStatusText.Text = $"Đã gửi {Path.GetFileName(filePath)}";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (IsCurrentGeneration(generation))
                SetValidation("Đã hủy tải tệp.");
        }
        catch (Exception exception) when (exception is IOException
                                             or SocketException
                                             or UnauthorizedAccessException
                                             or TimeoutException
                                             or InvalidOperationException
                                             or JsonException
                                             or FormatException)
        {
            if (IsCurrentGeneration(generation))
            {
                SetValidation($"Không thể gửi tệp: {exception.Message}");
                AddSystemMessage($"Gửi tệp thất bại: {exception.Message}");
            }
        }
    }

    private void TransferCancelButton_Click(object sender, RoutedEventArgs e)
    {
        _activeTransferCts?.Cancel();
        TransferStatusText.Text = "Đang hủy tải tệp...";
    }

    private async void DownloadAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionState != ConnectionState.Connected
            || _session is null
            || sender is not Button { Tag: ChatMessage { Attachment: not null } message })
            return;

        var attachment = message.Attachment;
        var dialog = new SaveFileDialog
        {
            Title = "Lưu tệp đính kèm",
            FileName = attachment.FileName,
            Filter = "Tất cả tệp|*.*",
            OverwritePrompt = true,
            AddExtension = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var session = _session;
        var generation = Volatile.Read(ref _connectionGeneration);
        using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(
            _sessionCts?.Token ?? CancellationToken.None);
        attachment.SetDownloadCancellationSource(downloadCts);
        attachment.BeginDownload();
        try
        {
            var progress = new Progress<TransferProgress>(value =>
            {
                if (IsCurrentGeneration(generation))
                    attachment.SetDownloadProgress(value.Percent);
            });
            await session.DownloadAttachmentAsync(
                attachment.Packet,
                dialog.FileName,
                progress,
                downloadCts.Token);
            if (IsCurrentGeneration(generation))
            {
                attachment.SetDownloadComplete();
                FooterStatusText.Text = $"Đã lưu {attachment.FileName}";
            }
        }
        catch (OperationCanceledException) when (downloadCts.IsCancellationRequested)
        {
            if (IsCurrentGeneration(generation))
                attachment.SetDownloadError("Đã hủy tải tệp.");
        }
        catch (Exception exception) when (exception is IOException
                                             or SocketException
                                             or UnauthorizedAccessException
                                             or TimeoutException
                                             or InvalidOperationException
                                             or InvalidDataException
                                             or NotSupportedException
                                             or JsonException
                                             or FormatException)
        {
            if (IsCurrentGeneration(generation))
                attachment.SetDownloadError($"Không thể tải tệp: {exception.Message}");
        }
        finally
        {
            attachment.SetDownloadCancellationSource(null);
        }
    }

    private void CancelAttachmentDownload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ChatMessage { Attachment: not null } message })
            message.Attachment.CancelDownload();
    }

    private async Task StartConnectionAsync(string host, int port, string username)
    {
        var generation = Interlocked.Increment(ref _connectionGeneration);
        var cancellation = new CancellationTokenSource();
        _sessionCts = cancellation;
        ClearValidation();
        SetConnectionState(ConnectionState.Connecting, $"Đang kết nối tới {host}:{port}...");

        var operation = ConnectAndRunAsync(host, port, username, generation, cancellation.Token);
        _connectionTask = operation;
        try
        {
            await operation;
        }
        finally
        {
            if (ReferenceEquals(_connectionTask, operation))
                _connectionTask = null;
            if (ReferenceEquals(_sessionCts, cancellation))
            {
                _sessionCts = null;
                cancellation.Dispose();
            }
        }
    }

    private async Task ConnectAndRunAsync(
        string host,
        int port,
        string username,
        long generation,
        CancellationToken cancellationToken)
    {
        ChatClientSession? session = null;
        try
        {
            using (var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectionTimeout.CancelAfter(ConnectionTimeout);
                session = await ChatClientSession.ConnectAsync(host, port, username, connectionTimeout.Token);
            }

            // A newer connection or a window shutdown won the race while TCP was opening.
            if (!IsCurrentGeneration(generation))
                return;

            _session = session;
            SetConnectionState(ConnectionState.Connected, $"Đã kết nối tới {host}:{port}.");
            AddSystemMessage($"Đã tham gia phòng chat với tên “{session.Username}”.");
            MessageInput.Focus();

            var connectionLossMessage = await ReceiveLoopAsync(session, generation, cancellationToken);
            if (IsCurrentGeneration(generation))
            {
                _session = null;
                SetConnectionState(ConnectionState.Offline, connectionLossMessage);
                if (!string.IsNullOrWhiteSpace(connectionLossMessage))
                    AddSystemMessage(connectionLossMessage);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is the expected path for a manual disconnect or app shutdown.
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentGeneration(generation))
            {
                const string message = "Kết nối quá thời gian chờ 10 giây. Hãy kiểm tra server rồi thử lại.";
                SetConnectionState(ConnectionState.Offline, message);
                SetValidation(message);
                AddSystemMessage(message);
            }
        }
        catch (JoinRejectedException exception)
        {
            if (IsCurrentGeneration(generation))
            {
                SetConnectionState(ConnectionState.Offline, "Server không cho phép tham gia phòng.");
                SetValidation(exception.Message);
                AddSystemMessage($"Không thể tham gia phòng: {exception.Message}");
            }
        }
        catch (Exception exception) when (exception is SocketException or IOException or JsonException)
        {
            if (IsCurrentGeneration(generation))
            {
                var message = GetConnectionErrorMessage(exception);
                SetConnectionState(ConnectionState.Offline, message);
                SetValidation(message);
                AddSystemMessage(message);
            }
        }
        catch (Exception exception)
        {
            if (IsCurrentGeneration(generation))
            {
                var message = GetConnectionErrorMessage(exception);
                SetConnectionState(ConnectionState.Offline, message);
                SetValidation(message);
                AddSystemMessage(message);
            }
        }
        finally
        {
            if (session is not null)
            {
                try
                {
                    // This is idempotent and also sends Leave when the socket is still healthy.
                    using var closeCancellation = new CancellationTokenSource(CloseTimeout);
                    await session.DisconnectAsync(closeCancellation.Token);
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
                {
                    // The receive loop already reported the useful connection-loss state.
                }
            }

            if (IsCurrentGeneration(generation) && ReferenceEquals(_session, session))
            {
                _session = null;
                if (_connectionState == ConnectionState.Connected)
                    SetConnectionState(ConnectionState.Offline, "Kết nối tới server đã đóng.");
            }
        }
    }

    private async Task<string?> ReceiveLoopAsync(
        ChatClientSession session,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            while (IsCurrentGeneration(generation))
            {
                var packet = await session.ReadAsync(cancellationToken);
                if (packet is null)
                    return "Server đã đóng kết nối.";

                if (!IsCurrentGeneration(generation))
                    return null;

                HandleIncomingPacket(packet, session.Username);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (ObjectDisposedException) when (!IsCurrentGeneration(generation))
        {
            return null;
        }
        catch (JsonException)
        {
            return "Dữ liệu server gửi về không hợp lệ; kết nối đã được đóng.";
        }
        catch (SocketException)
        {
            return "Kết nối tới server bị gián đoạn.";
        }
        catch (IOException)
        {
            return "Kết nối tới server bị gián đoạn.";
        }

        return null;
    }

    private async Task PreviewAttachmentAsync(
        ChatMessage message,
        long generation,
        CancellationToken cancellationToken)
    {
        var attachment = message.Attachment;
        var session = _session;
        if (attachment is null || session is null || !IsPreviewableImage(attachment.FileName))
            return;

        var entered = false;
        try
        {
            await _previewSemaphore.WaitAsync(cancellationToken);
            entered = true;
            if (!IsCurrentGeneration(generation) || !ReferenceEquals(_session, session))
                return;

            attachment.BeginPreview();
            var previewDirectory = Path.Combine(Path.GetTempPath(), "Lab1Chat-Lab2", "previews");
            Directory.CreateDirectory(previewDirectory);
            var previewPath = Path.Combine(
                previewDirectory,
                $"{Guid.NewGuid():N}{Path.GetExtension(attachment.FileName)}");

            try
            {
                var progress = new Progress<TransferProgress>(value =>
                {
                    if (IsCurrentGeneration(generation))
                        attachment.SetDownloadProgress(value.Percent);
                });
                await DownloadPreviewWithRetryAsync(
                    session,
                    attachment.Packet,
                    previewPath,
                    progress,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                // Decode from the completed local file on a worker thread. The
                // returned BitmapImage is OnLoad + frozen, so the temp file can
                // be removed immediately and no Downloads file is created.
                var image = await Task.Run(() => DecodePreview(previewPath), cancellationToken);
                if (!IsCurrentGeneration(generation))
                    return;

                attachment.SetPreview(image);
                RetainPreview(attachment, image);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Disconnect or shutdown cancellation is intentionally quiet.
            }
            catch (Exception exception) when (exception is IOException
                                                 or InvalidDataException
                                                 or NotSupportedException
                                                 or FormatException
                                                 or ArgumentException
                                                 or UnauthorizedAccessException
                                                 or SocketException
                                                 or TimeoutException)
            {
                if (IsCurrentGeneration(generation))
                    attachment.SetPreviewError(GetPreviewErrorMessage(exception));
            }
            catch (Exception exception)
            {
                // Preview work is fire-and-forget so that incoming packets do not
                // block the receive loop. Never let an unexpected decoder/network
                // failure become an unobserved task or leave a spinner running.
                if (IsCurrentGeneration(generation))
                    attachment.SetPreviewError(GetPreviewErrorMessage(exception));
            }
            finally
            {
                TryDeleteFile(previewPath);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The semaphore wait was cancelled as the session ended.
        }
        finally
        {
            if (entered)
                _previewSemaphore.Release();
        }
    }

    private async Task DownloadPreviewWithRetryAsync(
        ChatClientSession session,
        ChatPacket packet,
        string destinationPath,
        IProgress<TransferProgress> progress,
        CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var delay = 300;
        while (true)
        {
            try
            {
                await session.DownloadAttachmentAsync(
                    packet,
                    destinationPath,
                    progress,
                    cancellationToken);
                return;
            }
            catch (IOException exception) when (IsBusyTransferError(exception)
                                                 && DateTime.UtcNow - started < PreviewRetryWindow)
            {
                var jitter = Random.Shared.Next(0, 180);
                await Task.Delay(delay + jitter, cancellationToken);
                delay = Math.Min(delay * 2, 2_400);
            }
        }
    }

    private static bool IsBusyTransferError(IOException exception)
    {
        var text = exception.Message;
        return text.Contains("busy", StringComparison.OrdinalIgnoreCase)
            || text.Contains("bận", StringComparison.OrdinalIgnoreCase)
            || text.Contains("nhiều tệp", StringComparison.OrdinalIgnoreCase)
            || text.Contains("too many", StringComparison.OrdinalIgnoreCase)
            || text.Contains("capacity", StringComparison.OrdinalIgnoreCase);
    }

    private static BitmapImage DecodePreview(string path)
    {
        var sourceWidth = 0;
        var sourceHeight = 0;
        using (var metadataStream = File.OpenRead(path))
        {
            var decoder = BitmapDecoder.Create(
                metadataStream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.None);
            if (decoder.Frames.Count == 0)
                throw new InvalidDataException("Ảnh không có frame hợp lệ.");

            var frame = decoder.Frames[0];
            sourceWidth = frame.PixelWidth;
            sourceHeight = frame.PixelHeight;
            var sourcePixels = (long)frame.PixelWidth * frame.PixelHeight;
            if (frame.PixelWidth <= 0
                || frame.PixelHeight <= 0
                || sourcePixels > MaxPreviewPixels * 4)
            {
                throw new InvalidDataException("Kích thước ảnh vượt giới hạn xem trước.");
            }
        }

        using var stream = File.OpenRead(path);
        var scale = Math.Min(
            1d,
            Math.Min(
                MaxPreviewDecodeWidth / (double)sourceWidth,
                MaxPreviewDecodeHeight / (double)sourceHeight));
        var decodeWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var decodeHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.StreamSource = stream;
        image.DecodePixelWidth = decodeWidth;
        image.DecodePixelHeight = decodeHeight;
        image.EndInit();
        if (image.PixelWidth <= 0
            || image.PixelHeight <= 0
            || image.PixelWidth > MaxPreviewDecodeWidth
            || image.PixelHeight > MaxPreviewDecodeHeight
            || (long)image.PixelWidth * image.PixelHeight > MaxPreviewPixels)
        {
            throw new InvalidDataException("Ảnh vượt giới hạn kích thước xem trước.");
        }
        image.Freeze();
        return image;
    }

    private void RetainPreview(ChatAttachmentViewModel attachment, BitmapImage image)
    {
        var bytes = Math.Max(1L, (long)image.PixelWidth * image.PixelHeight * 4);
        if (attachment.AttachedPreviewBytes > 0)
            _retainedPreviewBytes -= attachment.AttachedPreviewBytes;
        _retainedPreviews.Remove(attachment);
        _retainedPreviews.Add(attachment);
        attachment.SetPreviewBytes(bytes);
        _retainedPreviewBytes += bytes;

        while (_retainedPreviews.Count > MaxRetainedPreviewCount
               || _retainedPreviewBytes > MaxRetainedPreviewBytes)
        {
            var oldest = _retainedPreviews[0];
            _retainedPreviews.RemoveAt(0);
            if (oldest.AttachedPreviewBytes > 0)
                _retainedPreviewBytes -= oldest.AttachedPreviewBytes;
            oldest.ClearPreview();
        }
    }

    private static bool IsPreviewableImage(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPreviewErrorMessage(Exception exception) => exception switch
    {
        InvalidDataException => "Ảnh không hợp lệ hoặc có kích thước quá lớn để xem trước.",
        NotSupportedException => "Định dạng ảnh này chưa được hỗ trợ xem trước.",
        _ => "Không thể tải ảnh xem trước; bạn vẫn có thể lưu tệp."
    };

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // A best-effort temp-cache cleanup must not interrupt chat.
        }
        catch (UnauthorizedAccessException)
        {
            // A best-effort temp-cache cleanup must not interrupt chat.
        }
    }

    private void HandleIncomingPacket(ChatPacket packet, string ownUsername)
    {
        switch (packet.Type)
        {
            case PacketTypes.Chat:
                if (string.IsNullOrWhiteSpace(packet.Text))
                    return;

                AddMessage(new ChatMessage
                {
                    Kind = string.Equals(packet.Username, ownUsername, StringComparison.OrdinalIgnoreCase)
                        ? ChatMessageKind.Own
                        : ChatMessageKind.Other,
                    Username = packet.Username,
                    Text = packet.Text,
                    Timestamp = SafeTimestamp(packet.Timestamp)
                });
                break;

            case PacketTypes.Attachment:
                HandleIncomingAttachment(packet, ownUsername);
                break;

            case PacketTypes.UserList:
                UpdateOnlineUsers(packet.Users);
                break;

            case PacketTypes.System:
                if (!string.IsNullOrWhiteSpace(packet.Text))
                    AddSystemMessage(packet.Text.Trim(), packet.Timestamp);
                break;

            case PacketTypes.Error:
                if (!string.IsNullOrWhiteSpace(packet.Text))
                    AddSystemMessage($"Server: {packet.Text.Trim()}", packet.Timestamp);
                break;
        }
    }

    private void HandleIncomingAttachment(ChatPacket packet, string ownUsername)
    {
        if (!TryValidateIncomingAttachment(packet, out var validationError))
        {
            AddSystemMessage($"Không thể hiển thị tệp đính kèm: {validationError}", packet.Timestamp);
            return;
        }

        var attachment = new ChatAttachmentViewModel(packet);
        var message = new ChatMessage
        {
            Kind = string.Equals(packet.Username, ownUsername, StringComparison.OrdinalIgnoreCase)
                ? ChatMessageKind.Own
                : ChatMessageKind.Other,
            Username = packet.Username,
            Text = attachment.FileName,
            Timestamp = SafeTimestamp(packet.Timestamp),
            Attachment = attachment
        };
        AddMessage(message);

        if (packet.IsImage && packet.FileSize <= ChatLimits.MaxImageFileSize)
        {
            var generation = Volatile.Read(ref _connectionGeneration);
            var token = _sessionCts?.Token ?? CancellationToken.None;
            _ = PreviewAttachmentAsync(message, generation, token);
        }
    }

    private static bool TryValidateIncomingAttachment(ChatPacket packet, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(packet.AttachmentId))
        {
            error = "thiếu mã tệp.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(packet.FileName)
            || Path.GetFileName(packet.FileName) != packet.FileName)
        {
            error = "tên tệp không hợp lệ.";
            return false;
        }
        if (packet.FileSize < 0 || packet.FileSize > ChatLimits.MaxFileSize)
        {
            error = "kích thước tệp vượt giới hạn 2 GiB.";
            return false;
        }
        if (packet.IsImage && packet.FileSize > ChatLimits.MaxImageFileSize)
        {
            error = "ảnh vượt giới hạn xem trước 20 MB.";
            return false;
        }
        return true;
    }

    private async Task DisconnectCurrentAsync(string? reason, bool addSystemMessage)
    {
        var cancellation = _sessionCts;
        var session = _session;
        var operation = _connectionTask;
        var transferOperation = _activeTransferTask;

        Interlocked.Increment(ref _connectionGeneration);
        _session = null;
        _sessionCts = null;
        cancellation?.Cancel();
        _activeTransferCts?.Cancel();
        SetConnectionState(ConnectionState.Disconnecting, "Đang đóng kết nối...");

        if (addSystemMessage && !string.IsNullOrWhiteSpace(reason))
            AddSystemMessage(reason);

        if (session is not null)
        {
            using var closeCancellation = new CancellationTokenSource(CloseTimeout);
            try
            {
                await session.DisconnectAsync(closeCancellation.Token);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
            {
                // The socket is already unavailable; local cleanup continues below.
            }
        }

        if (operation is not null && !operation.IsCompleted)
        {
            try
            {
                await operation;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
            {
                // Connect/receive cleanup should not prevent returning to the offline state.
            }
        }

        if (transferOperation is not null && !transferOperation.IsCompleted)
        {
            try
            {
                await Task.WhenAny(transferOperation, Task.Delay(CloseTimeout));
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // Transfer cancellation is best effort during disconnect.
            }
        }

        if (cancellation is not null)
            cancellation.Dispose();

        if (ReferenceEquals(_connectionTask, operation))
            _connectionTask = null;
        SetConnectionState(ConnectionState.Offline, "Đã ngắt kết nối. Bạn có thể kết nối lại.");
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        await SendCurrentMessageAsync();
    }

    private async void MessageInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            await SendCurrentMessageAsync();
        }
    }

    private async Task SendCurrentMessageAsync()
    {
        if (_isSending || _connectionState != ConnectionState.Connected || _session is null)
            return;

        var draft = MessageInput.Text;
        var validationError = ChatValidation.ValidateMessage(draft);
        if (validationError is not null)
        {
            SetValidation(validationError, MessageInput);
            MessageInput.Focus();
            return;
        }

        var content = draft.Trim();
        var session = _session;
        var sessionCancellation = _sessionCts;
        var generation = Volatile.Read(ref _connectionGeneration);
        _isSending = true;
        SendButton.IsEnabled = false;
        try
        {
            using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                sessionCancellation?.Token ?? CancellationToken.None);
            writeTimeout.CancelAfter(WriteTimeout);
            await session.SendChatAsync(content, writeTimeout.Token);
            if (IsCurrentGeneration(generation) && ReferenceEquals(_session, session))
            {
                // The user may have started composing another message while the write
                // awaited the socket. Never clear a newer draft.
                if (string.Equals(MessageInput.Text, draft, StringComparison.Ordinal))
                    MessageInput.Clear();
                ClearValidation();
                MessageInput.Focus();
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            if (IsCurrentGeneration(generation))
                await DisconnectCurrentAsync("Không thể gửi tin nhắn vì kết nối đã mất.", addSystemMessage: true);
        }
        finally
        {
            _isSending = false;
            if (_connectionState == ConnectionState.Connected)
                SendButton.IsEnabled = true;
        }
    }

    private void EmojiButton_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionState != ConnectionState.Connected || _session is null)
            return;

        _emojiCaretIndex = MessageInput.CaretIndex;
        _emojiSelectionLength = MessageInput.SelectionLength;
        EmojiPopup.IsOpen = !EmojiPopup.IsOpen;
    }

    private void Emoji_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionState != ConnectionState.Connected
            || _session is null
            || sender is not Button { Tag: string emoji })
            return;

        MessageInput.Focus();
        var index = Math.Clamp(_emojiCaretIndex, 0, MessageInput.Text.Length);
        var selectionLength = Math.Clamp(_emojiSelectionLength, 0, MessageInput.Text.Length - index);
        if (MessageInput.Text.Length - selectionLength + emoji.Length > ChatLimits.MaxMessageLength)
        {
            SetValidation($"Tin nhắn tối đa {ChatLimits.MaxMessageLength} ký tự.", MessageInput);
            MessageInput.Focus();
            EmojiPopup.IsOpen = false;
            return;
        }
        MessageInput.Select(index, selectionLength);
        MessageInput.SelectedText = emoji;
        MessageInput.CaretIndex = index + emoji.Length;
        _emojiCaretIndex = MessageInput.CaretIndex;
        _emojiSelectionLength = 0;
        EmojiPopup.IsOpen = false;
    }

    private void BuildEmojiPicker()
    {
        EmojiPanel.Children.Clear();
        foreach (var glyph in EmojiCatalog.All)
        {
            var emoji = glyph.Character;
            var button = new Button
            {
                Tag = emoji,
                Width = 38,
                Height = 36,
                FontSize = 20,
                Padding = new Thickness(0),
                Margin = new Thickness(1),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"Chèn {emoji}",
                Focusable = true
            };
            if (EmojiCatalog.TryGetImage(emoji, out var image) && image is not null)
            {
                button.Content = new Image
                {
                    Source = image,
                    Width = 25,
                    Height = 25,
                    Stretch = Stretch.Uniform
                };
            }
            else
            {
                button.Content = emoji;
            }
            button.Click += Emoji_Click;
            EmojiPanel.Children.Add(button);
        }
    }

    private bool TryReadConnectionSettings(out string host, out int port, out string username)
    {
        ClearValidation();
        host = HostInput.Text.Trim();
        username = string.Empty;
        port = 0;

        if (string.IsNullOrWhiteSpace(host))
        {
            SetValidation("Vui lòng nhập địa chỉ máy chủ.", HostInput);
            HostInput.Focus();
            return false;
        }

        if (!int.TryParse(PortInput.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
            || port is < 1 or > 65_535)
        {
            SetValidation("Cổng phải là số từ 1 đến 65535.", PortInput);
            PortInput.Focus();
            PortInput.SelectAll();
            return false;
        }

        try
        {
            username = ChatValidation.NormalizeUsername(UsernameInput.Text);
        }
        catch (ArgumentException)
        {
            SetValidation("Tên thành viên chứa văn bản Unicode không hợp lệ.", UsernameInput);
            UsernameInput.Focus();
            return false;
        }

        var usernameError = ChatValidation.ValidateUsername(username);
        if (usernameError is not null)
        {
            SetValidation(usernameError, UsernameInput);
            UsernameInput.Focus();
            return false;
        }

        return true;
    }

    private void UpdateOnlineUsers(IEnumerable<string>? users)
    {
        OnlineUsers.Clear();
        if (users is not null)
        {
            foreach (var user in users
                         .Where(static user => !string.IsNullOrWhiteSpace(user))
                         .Select(static user => user.Trim())
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Take(ChatLimits.MaxUsers))
            {
                OnlineUsers.Add(user);
            }
        }

        MemberCountText.Text = OnlineUsers.Count.ToString(CultureInfo.InvariantCulture);
        if (_connectionState == ConnectionState.Connected)
            ConnectionHintText.Text = OnlineUsers.Count == 0
                ? "Đã kết nối; đang chờ danh sách thành viên."
                : $"{OnlineUsers.Count} thành viên đang trực tuyến.";
    }

    private void AddSystemMessage(string text, DateTimeOffset timestamp = default)
    {
        if (_isClosing || string.IsNullOrWhiteSpace(text))
            return;

        AddMessage(new ChatMessage
        {
            Kind = ChatMessageKind.System,
            Text = text,
            Timestamp = SafeTimestamp(timestamp)
        });
    }

    private void AddMessage(ChatMessage message)
    {
        Messages.Add(message);
        while (Messages.Count > MaxVisibleMessages)
            Messages.RemoveAt(0);
        UpdateMessageView();
    }

    private void UpdateMessageView()
    {
        MessageCountText.Text = Messages.Count == 0
            ? string.Empty
            : $"{Messages.Count.ToString(CultureInfo.InvariantCulture)} tin nhắn";
        EmptyStatePanel.Visibility = Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (Messages.Count == 0)
        {
            EmptyStateTitle.Text = _connectionState == ConnectionState.Connected
                ? "Phòng chat đang chờ bạn"
                : "Chưa kết nối";
            EmptyStateDescription.Text = _connectionState == ConnectionState.Connected
                ? "Hãy gửi lời chào đầu tiên cho mọi người nhé."
                : "Nhập thông tin server rồi bấm Kết nối để bắt đầu.";
        }
        else if (_keepMessagesAtBottom && !_isClosing)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(MessagesScrollViewer.ScrollToEnd));
        }
    }

    private void UpdateMessageCounter()
    {
        var length = MessageInput.Text.Length;
        CharacterCountText.Text = $"{length.ToString(CultureInfo.InvariantCulture)} / {ChatLimits.MaxMessageLength.ToString(CultureInfo.InvariantCulture)}";
        CharacterCountText.Foreground = length >= ChatLimits.MaxMessageLength
            ? (Brush)FindResource("DangerBrush")
            : (Brush)FindResource("MutedInkBrush");
    }

    private void MessageInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (CharacterCountText is not null)
            UpdateMessageCounter();
    }

    private void MessagesScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0)
        {
            _keepMessagesAtBottom = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 24;
        }
    }

    private void SetConnectionState(ConnectionState state, string? detail = null)
    {
        _connectionState = state;
        if (state == ConnectionState.Offline)
        {
            OnlineUsers.Clear();
            MemberCountText.Text = "0";
            EmojiPopup.IsOpen = false;
            // Network loss also reaches this path (not only the explicit
            // disconnect button). Cancel the session-linked preview/download
            // operations immediately instead of waiting for their socket timeout.
            _sessionCts?.Cancel();
            _activeTransferCts?.Cancel();
            SetTransferPanel(false);
        }

        var canEditConnection = state is ConnectionState.Offline;
        var isConnected = state is ConnectionState.Connected;
        var isBusy = state is ConnectionState.Connecting or ConnectionState.Disconnecting;

        HostInput.IsEnabled = canEditConnection;
        PortInput.IsEnabled = canEditConnection;
        UsernameInput.IsEnabled = canEditConnection;
        ConnectButton.IsEnabled = !_isClosing && !isBusy;
        MessageInput.IsEnabled = isConnected;
        SendButton.IsEnabled = isConnected;
        EmojiButton.IsEnabled = isConnected;
        ImageButton.IsEnabled = isConnected && _activeTransferTask is not { IsCompleted: false };
        FileButton.IsEnabled = isConnected && _activeTransferTask is not { IsCompleted: false };

        switch (state)
        {
            case ConnectionState.Connected:
                ConnectionDot.Fill = Brushes.LimeGreen;
                ConnectionStatusText.Text = "Đang trực tuyến";
                ConnectButton.Content = "Ngắt kết nối";
                ConnectionHintText.Text = OnlineUsers.Count == 0
                    ? "Đã kết nối; đang chờ danh sách thành viên."
                    : $"{OnlineUsers.Count} thành viên đang trực tuyến.";
                FooterStatusText.Text = detail ?? "Kết nối ổn định • tin nhắn được cập nhật theo thời gian thực";
                break;

            case ConnectionState.Connecting:
                ConnectionDot.Fill = Brushes.Gold;
                ConnectionStatusText.Text = "Đang kết nối...";
                ConnectButton.Content = "Đang kết nối...";
                ConnectionHintText.Text = detail ?? "Đang mở kết nối tới server...";
                FooterStatusText.Text = detail ?? "Đang kết nối...";
                break;

            case ConnectionState.Disconnecting:
                ConnectionDot.Fill = Brushes.Gold;
                ConnectionStatusText.Text = "Đang ngắt...";
                ConnectButton.Content = "Đang ngắt kết nối...";
                ConnectionHintText.Text = detail ?? "Đang đóng kết nối...";
                FooterStatusText.Text = detail ?? "Đang đóng kết nối...";
                break;

            default:
                ConnectionDot.Fill = Brushes.LightGray;
                ConnectionStatusText.Text = "Chưa kết nối";
                ConnectButton.Content = Messages.Count == 0 ? "Kết nối" : "Kết nối lại";
                ConnectionHintText.Text = detail ?? "Nhập thông tin rồi kết nối để bắt đầu.";
                FooterStatusText.Text = detail ?? "Sẵn sàng • Lab1 Chat";
                break;
        }

        UpdateMessageView();
    }

    private void SetTransferPanel(bool visible, string? status = null)
    {
        TransferStatusPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TransferCancelButton.IsEnabled = visible;
        if (!string.IsNullOrWhiteSpace(status))
            TransferStatusText.Text = status;
        if (!visible)
            TransferProgressBar.Value = 0;

        var canChoose = _connectionState == ConnectionState.Connected && !visible;
        ImageButton.IsEnabled = canChoose;
        FileButton.IsEnabled = canChoose;
    }

    private void SetValidation(string message, TextBox? field = null)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
        if (field is not null)
            field.BorderBrush = (Brush)FindResource("DangerBrush");
    }

    private void ClearValidation()
    {
        ValidationText.Text = string.Empty;
        ValidationText.Visibility = Visibility.Collapsed;
        HostInput.ClearValue(TextBox.BorderBrushProperty);
        PortInput.ClearValue(TextBox.BorderBrushProperty);
        UsernameInput.ClearValue(TextBox.BorderBrushProperty);
        MessageInput.ClearValue(TextBox.BorderBrushProperty);
    }

    private bool IsCurrentGeneration(long generation) =>
        !_isClosing && generation == Volatile.Read(ref _connectionGeneration);

    private static DateTimeOffset SafeTimestamp(DateTimeOffset timestamp) =>
        timestamp == default ? DateTimeOffset.Now : timestamp;

    private static string GetConnectionErrorMessage(Exception exception)
    {
        var socketException = exception as SocketException ?? exception.InnerException as SocketException;
        return socketException?.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => "Không thể kết nối: server chưa chạy hoặc đang từ chối kết nối.",
            SocketError.HostNotFound => "Không tìm thấy máy chủ. Hãy kiểm tra địa chỉ host.",
            SocketError.TimedOut => "Kết nối bị quá thời gian chờ.",
            _ => "Không thể kết nối tới server. Hãy kiểm tra host, cổng và trạng thái server."
        };
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosing)
            return;

        e.Cancel = true;
        _isClosing = true;
        EmojiPopup.IsOpen = false;
        _activeTransferCts?.Cancel();
        await DisconnectCurrentAsync(null, addSystemMessage: false);
        Close();
    }
}
