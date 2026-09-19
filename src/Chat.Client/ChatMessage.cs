using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Chat.Shared;

namespace Chat.Client;

public enum ChatMessageKind
{
    Own,
    Other,
    System
}

/// <summary>View-only representation of a packet in the conversation.</summary>
public sealed class ChatMessage
{
    public required ChatMessageKind Kind { get; init; }
    public string? Username { get; init; }
    public required string Text { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public ChatAttachmentViewModel? Attachment { get; init; }

    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm");
    public string SenderText => string.IsNullOrWhiteSpace(Username) ? "Thành viên" : Username;
    public bool HasAttachment => Attachment is not null;
    public HorizontalAlignment BubbleAlignment =>
        Kind == ChatMessageKind.Own ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public Brush AttachmentBackground =>
        Kind == ChatMessageKind.Own ? new SolidColorBrush(Color.FromRgb(18, 35, 63)) :
        new SolidColorBrush(Color.FromRgb(237, 242, 248));
    public Brush AttachmentForeground =>
        Kind == ChatMessageKind.Own ? Brushes.White : new SolidColorBrush(Color.FromRgb(23, 36, 58));
}

/// <summary>Mutable UI state for a server-authoritative attachment packet.</summary>
public sealed class ChatAttachmentViewModel : INotifyPropertyChanged
{
    private ImageSource? _preview;
    private bool _isPreviewing;
    private bool _isDownloading;
    private CancellationTokenSource? _downloadCts;
    private string? _errorText;
    private string _progressText = string.Empty;
    private long _attachedPreviewBytes;

    public ChatAttachmentViewModel(ChatPacket packet)
    {
        Packet = packet;
        FileName = SanitizeFileName(packet.FileName);
        FileSize = Math.Max(0, packet.FileSize);
        IsImage = packet.IsImage;
    }

    public ChatPacket Packet { get; }
    public string FileName { get; }
    public long FileSize { get; }
    public bool IsImage { get; }
    public bool CanDownload => !string.IsNullOrWhiteSpace(Packet.AttachmentId) && !_isDownloading;
    public bool CanCancelDownload => _isDownloading;
    public string SizeText => FormatBytes(FileSize);
    public string FileExtension => Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant();
    public ImageSource? Preview
    {
        get => _preview;
        private set
        {
            if (ReferenceEquals(_preview, value))
                return;
            _preview = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPreview));
            OnPropertyChanged(nameof(PreviewVisibility));
        }
    }

    public bool HasPreview => Preview is not null;
    public Visibility PreviewVisibility => HasPreview ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PreviewProgressVisibility => _isPreviewing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TransferProgressVisibility => _isDownloading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ErrorVisibility => string.IsNullOrWhiteSpace(_errorText) ? Visibility.Collapsed : Visibility.Visible;
    public string ErrorText => _errorText ?? string.Empty;
    public string ProgressText => _progressText;
    public long AttachedPreviewBytes => _attachedPreviewBytes;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void BeginPreview()
    {
        _isPreviewing = true;
        _errorText = null;
        _progressText = "Đang tải xem trước...";
        NotifyTransferState();
    }

    public void SetPreview(ImageSource image)
    {
        Preview = image;
        _isPreviewing = false;
        _errorText = null;
        _progressText = string.Empty;
        NotifyTransferState();
    }

    public void SetPreviewBytes(long bytes) => _attachedPreviewBytes = Math.Max(0, bytes);

    public void ClearPreview()
    {
        Preview = null;
        _attachedPreviewBytes = 0;
    }

    public void SetPreviewError(string message)
    {
        _isPreviewing = false;
        _errorText = message;
        _progressText = string.Empty;
        NotifyTransferState();
    }

    public void BeginDownload()
    {
        _isDownloading = true;
        _errorText = null;
        _progressText = "Đang tải 0%";
        NotifyTransferState();
    }

    public void SetDownloadCancellationSource(CancellationTokenSource? cancellationSource)
    {
        _downloadCts = cancellationSource;
        OnPropertyChanged(nameof(CanCancelDownload));
    }

    public void CancelDownload() => _downloadCts?.Cancel();

    public void SetDownloadProgress(double percent)
    {
        _progressText = $"Đang tải {Math.Clamp(percent, 0, 100):0}%";
        OnPropertyChanged(nameof(ProgressText));
    }

    public void SetDownloadComplete()
    {
        _isDownloading = false;
        _errorText = null;
        _progressText = string.Empty;
        NotifyTransferState();
    }

    public void SetDownloadError(string message)
    {
        _isDownloading = false;
        _errorText = message;
        NotifyTransferState();
    }

    private void NotifyTransferState()
    {
        OnPropertyChanged(nameof(PreviewProgressVisibility));
        OnPropertyChanged(nameof(TransferProgressVisibility));
        OnPropertyChanged(nameof(ErrorVisibility));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanCancelDownload));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string SanitizeFileName(string? fileName)
    {
        var value = Path.GetFileName(fileName ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(value) ? "Tệp đính kèm" : value;
    }

    private static string FormatBytes(long bytes)
    {
        const double scale = 1024;
        if (bytes < scale)
            return $"{bytes} B";
        if (bytes < scale * scale)
            return $"{bytes / scale:0.0} KB";
        if (bytes < scale * scale * scale)
            return $"{bytes / (scale * scale):0.0} MB";
        return $"{bytes / (scale * scale * scale):0.00} GB";
    }
}

/// <summary>Cho phép dùng các mẫu bong bóng khác nhau cho tin của mình, người khác và hệ thống.</summary>
public sealed class ChatMessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate OwnTemplate { get; set; } = null!;
    public DataTemplate OtherTemplate { get; set; } = null!;
    public DataTemplate SystemTemplate { get; set; } = null!;
    public DataTemplate AttachmentTemplate { get; set; } = null!;

    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
        if (item is ChatMessage { Attachment: not null })
            return AttachmentTemplate;
        if (item is ChatMessage { Kind: ChatMessageKind.Own })
            return OwnTemplate;
        if (item is ChatMessage { Kind: ChatMessageKind.System })
            return SystemTemplate;
        return OtherTemplate;
    }
}
