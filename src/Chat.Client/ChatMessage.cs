using System.Windows;
using System.Windows.Controls;
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

    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm");
    public string SenderText => string.IsNullOrWhiteSpace(Username) ? "Thành viên" : Username;
}

/// <summary>Cho phép dùng các mẫu bong bóng khác nhau cho tin của mình, người khác và hệ thống.</summary>
public sealed class ChatMessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate OwnTemplate { get; set; } = null!;
    public DataTemplate OtherTemplate { get; set; } = null!;
    public DataTemplate SystemTemplate { get; set; } = null!;

    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
        if (item is ChatMessage { Kind: ChatMessageKind.Own })
            return OwnTemplate;
        if (item is ChatMessage { Kind: ChatMessageKind.System })
            return SystemTemplate;
        return OtherTemplate;
    }
}
