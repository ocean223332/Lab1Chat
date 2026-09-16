using System.Net;
using System.Net.Sockets;
using System.Text;
using Chat.Shared;

namespace Chat.Server;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (!TryParseOptions(args, out var address, out var port, out var error, out var showHelp))
        {
            Console.Error.WriteLine(error);
            PrintUsage();
            return 2;
        }

        if (showHelp)
        {
            PrintUsage();
            return 0;
        }

        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            if (!stop.IsCancellationRequested)
                Console.WriteLine("Đang dừng máy chủ...");
            stop.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            await using var server = new ChatServer(address, port);
            await server.RunAsync(stop.Token).ConfigureAwait(false);
            return 0;
        }
        catch (SocketException exception)
        {
            Console.Error.WriteLine($"Không thể khởi động máy chủ: {exception.Message}");
            return 1;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Lỗi máy chủ: {exception.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static bool TryParseOptions(
        string[] args,
        out IPAddress address,
        out int port,
        out string? error,
        out bool showHelp)
    {
        address = IPAddress.Any;
        port = ChatLimits.DefaultPort;
        error = null;
        showHelp = false;

        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (option is "--help" or "-h")
            {
                showHelp = true;
                continue;
            }

            if (option is not "--port" and not "--address")
            {
                error = $"Tham số không được hỗ trợ: {option}";
                return false;
            }

            if (++index >= args.Length)
            {
                error = $"Thiếu giá trị cho {option}.";
                return false;
            }

            var value = args[index];
            if (option == "--port")
            {
                if (!int.TryParse(value, out port) || port is < 0 or > 65_535)
                {
                    error = "Cổng phải là số nguyên trong khoảng 0 đến 65535.";
                    return false;
                }
            }
            else if (string.Equals(value, "any", StringComparison.OrdinalIgnoreCase)
                || value == "*")
            {
                address = IPAddress.Any;
            }
            else if (!IPAddress.TryParse(value, out var parsedAddress) || parsedAddress is null)
            {
                error = $"Địa chỉ IP không hợp lệ: {value}";
                return false;
            }
            else
            {
                address = parsedAddress;
            }
        }

        return true;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Chat.Server - máy chủ chat TCP dòng JSON");
        Console.WriteLine("Cách dùng: dotnet run -- [--address any|<ip>] [--port <1-65535>]");
        Console.WriteLine($"Mặc định: any:{ChatLimits.DefaultPort}");
    }
}
