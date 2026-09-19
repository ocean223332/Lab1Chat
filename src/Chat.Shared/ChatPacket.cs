namespace Chat.Shared;

/// <summary>Một gói JSON trên một dòng; emoji được gửi như văn bản Unicode.</summary>
public sealed record ChatPacket
{
    public string Type { get; init; } = string.Empty;
    public string? Username { get; init; }
    public string? Text { get; init; }
    public string[]? Users { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    // Lab 2: chỉ truyền metadata trên kết nối chat. Byte file dùng kết nối TCP riêng.
    public string? TransferToken { get; init; }
    public string? AttachmentId { get; init; }
    public string? FileName { get; init; }
    public string? Sha256 { get; init; }
    public long FileSize { get; init; }
    public long Offset { get; init; }
    public byte[]? Data { get; init; }
    public bool IsImage { get; init; }
}

public static class PacketTypes
{
    public const string Join = "join";
    public const string Welcome = "welcome";
    public const string Chat = "chat";
    public const string UserList = "userList";
    public const string System = "system";
    public const string Error = "error";
    public const string Leave = "leave";
    public const string FileUpload = "fileUpload";
    public const string FileDownload = "fileDownload";
    public const string FileReady = "fileReady";
    public const string FileChunk = "fileChunk";
    public const string FileComplete = "fileComplete";
    public const string Attachment = "attachment";
}

public static class ChatLimits
{
    public const int DefaultPort = 5000;
    public const int MaxUsernameLength = 24;
    public const int MaxMessageLength = 2000;
    public const int MaxFrameBytes = 65_536;
    public const int MaxUsers = 100;
    public const int FileChunkBytes = 32 * 1024;
    public const long MaxFileSize = 2L * 1024 * 1024 * 1024;
    public const long MaxImageFileSize = 20L * 1024 * 1024;
}
