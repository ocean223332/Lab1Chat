namespace Chat.Shared;

/// <summary>Một gói JSON trên một dòng; emoji được gửi như văn bản Unicode.</summary>
public sealed record ChatPacket
{
    public string Type { get; init; } = string.Empty;
    public string? Username { get; init; }
    public string? Text { get; init; }
    public string[]? Users { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
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
}

public static class ChatLimits
{
    public const int DefaultPort = 5000;
    public const int MaxUsernameLength = 24;
    public const int MaxMessageLength = 2000;
    public const int MaxFrameBytes = 65_536;
    public const int MaxUsers = 100;
}
