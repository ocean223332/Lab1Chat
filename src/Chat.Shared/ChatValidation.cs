using System.Globalization;
using System.Text;

namespace Chat.Shared;

public static class ChatValidation
{
    public static string NormalizeUsername(string? username) =>
        (username ?? string.Empty).Trim().Normalize(NormalizationForm.FormC);

    public static string? ValidateUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return "Vui lòng nhập tên thành viên.";
        if (username.Length > ChatLimits.MaxUsernameLength)
            return $"Tên thành viên tối đa {ChatLimits.MaxUsernameLength} ký tự.";
        if (username != username.Trim())
            return "Tên thành viên không được có khoảng trắng ở đầu hoặc cuối.";

        // Cho phép tên tiếng Việt; chặn ký tự điều khiển, dấu xuống dòng và ký tự ẩn.
        foreach (var rune in username.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Surrogate || rune == Rune.ReplacementChar)
                return "Tên thành viên không được chứa ký tự điều khiển hoặc ký tự ẩn.";
        }

        return null;
    }

    public static string? ValidateMessage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Tin nhắn không được để trống.";
        if (text.Length > ChatLimits.MaxMessageLength)
            return $"Tin nhắn tối đa {ChatLimits.MaxMessageLength} ký tự.";
        if (text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
            return "Tin nhắn chứa ký tự điều khiển không hợp lệ.";

        return null;
    }
}
