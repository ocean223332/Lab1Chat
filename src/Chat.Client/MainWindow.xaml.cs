using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    private static readonly string[] Emojis =
    [
        "😀", "😃", "😄", "😁", "😆", "😅", "😂", "🙂", "🙃", "😉",
        "😊", "😍", "🥰", "😘", "😎", "🤗", "🤔", "😴", "😢", "😭",
        "😡", "😱", "🙌", "👍", "👎", "👋", "👏", "🙏", "💬", "❤️",
        "💙", "💚", "💛", "💜", "🧡", "🎉", "🔥", "✨", "✅", "🚀",
        "☕", "🌟", "🎯", "💡", "🤝", "🌈", "🍀", "🎵", "💪", "🥳"
    ];

    private const int MaxVisibleMessages = 1000;
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

    private async Task DisconnectCurrentAsync(string? reason, bool addSystemMessage)
    {
        var cancellation = _sessionCts;
        var session = _session;
        var operation = _connectionTask;

        Interlocked.Increment(ref _connectionGeneration);
        _session = null;
        _sessionCts = null;
        cancellation?.Cancel();
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
        foreach (var emoji in Emojis)
        {
            var button = new Button
            {
                Content = emoji,
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
        await DisconnectCurrentAsync(null, addSystemMessage: false);
        Close();
    }
}
