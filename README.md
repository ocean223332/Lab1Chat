# Lab 1 — Ứng dụng chat TCP: Console Server + WPF Client

Ứng dụng C# cho nhiều thành viên cùng tham gia một phòng chat. Server là Console App; client là WPF App trên Windows. Không sử dụng thư viện NuGet bên ngoài.

## Chức năng

- Kết nối server bằng IP/hostname, port và tên thành viên.
- Nhắn tin chung theo thời gian thực; hỗ trợ tiếng Việt, emoji và tin nhắn nhiều dòng.
- Bảng chọn emoji; Enter để gửi, Shift+Enter để xuống dòng.
- Hiển thị danh sách thành viên online, thời gian tin nhắn, thông báo vào/rời phòng.
- Phân biệt tin nhắn của bản thân, người khác và hệ thống.
- Từ chối tên trùng (không phân biệt chữ hoa/thường), tên không hợp lệ, tin nhắn trống/quá dài.
- Xử lý mất kết nối và kết nối lại; giao tiếp bất đồng bộ để không khóa giao diện.
- Giới hạn chờ kết nối 10 giây; giữ tối đa 1.000 tin gần nhất trong cửa sổ để hạn chế bộ nhớ.

## Yêu cầu

- Windows với **.NET 9 SDK** để build/chạy mã nguồn.
- Nếu dùng IDE: Visual Studio hỗ trợ .NET 9 và workload **.NET desktop development**. Có thể dùng hoàn toàn bằng terminal, không cần Visual Studio.
- Các máy client cần kết nối được đến máy server. Mặc định TCP port **5000**.

## Chạy nhanh trên một máy

1. Giải nén toàn bộ dự án.
2. Chạy `Start-Server.cmd` và giữ cửa sổ server mở.
3. Chạy `Start-Client.cmd` hai lần để mở hai cửa sổ chat.
4. Dùng server `127.0.0.1`, port `5000`; nhập hai tên khác nhau, ví dụ `An` và `Bình`.
5. Bấm kết nối ở mỗi client, rồi gửi tin nhắn hoặc chọn emoji.

Hoặc chạy trong terminal tại thư mục chứa `Lab1Chat.sln`:

```powershell
dotnet build Lab1Chat.sln -c Release

# Terminal 1: server (địa chỉ IPv4, cổng tùy chọn)
dotnet run --project src/Chat.Server -c Release --no-build -- --address 0.0.0.0 --port 5000

# Terminal 2 và 3: chạy lệnh này ở mỗi terminal để mở một client
dotnet run --project src/Chat.Client -c Release --no-build
```

Bấm ngắt kết nối hoặc đóng cửa sổ để rời phòng. Nhấn **Ctrl+C** tại server để dừng server.

## Chạy qua mạng LAN

1. Chạy server trên một máy; địa chỉ lắng nghe mặc định là `0.0.0.0` (mọi giao diện IPv4).
2. Dùng `ipconfig` tại máy server để xem địa chỉ IPv4 LAN, ví dụ `192.168.1.10`.
3. Nếu Windows Firewall hỏi, chỉ cho phép trên mạng **Private** đáng tin cậy. Nếu cần, cấu hình rule TCP inbound cho đúng port trên máy server; không tắt toàn bộ firewall.
4. Trên client, nhập IP LAN thật của server và cùng port. Không dùng `127.0.0.1` hoặc `0.0.0.0` làm địa chỉ server khi client ở máy khác.

Nếu không kết nối được, kiểm tra server đang chạy, IP/port, hai máy có thể liên lạc với nhau, và firewall. Mạng Wi-Fi khách có thể chặn liên lạc giữa các thiết bị.

## Cấu trúc mã nguồn

```text
Lab1Chat.sln
src/
  Chat.Shared/              Gói tin, kiểm tra dữ liệu, đóng khung TCP/JSON
  Chat.Server/              Console server, quản lý thành viên và phát tin
  Chat.Client/              Giao diện WPF, dịch vụ kết nối và hiển thị tin nhắn
tests/
  Chat.ProtocolTests/       Kiểm thử bộ đóng khung và validation
  Chat.IntegrationTests/    Kiểm thử nhiều client với server TCP thật
Start-Server.cmd
Start-Client.cmd
```

## Nguyên lý hoạt động

Client mở một `TcpClient` đến `TcpListener` của server. Mỗi gói tin là một đối tượng JSON UTF-8 kết thúc bằng byte xuống dòng LF. Xuống dòng bên trong nội dung chat được JSON escape, nên không làm đứt gói tin. TCP có thể chia/gộp dữ liệu tùy ý; `JsonLineConnection` tích lũy và tách gói đúng ranh giới.

| Loại gói | Chiều gửi | Ý nghĩa |
| --- | --- | --- |
| `join` | Client → server | Xin tham gia bằng `username` |
| `welcome` | Server → client mới | Xác nhận tên được chấp nhận |
| `chat` | Hai chiều | Client gửi `text`; server gắn tên người gửi thật và phát cho cả phòng |
| `userList` | Server → các client | Danh sách `users` đang online |
| `system` | Server → các client | Thông báo vào/rời phòng |
| `error` | Server → client | Lý do dữ liệu/kết nối không được chấp nhận |
| `leave` | Client → server | Chủ động rời phòng |

Ví dụ hai gói client gửi, mỗi JSON trên một dòng:

```json
{"type":"join","username":"An"}
{"type":"chat","text":"Xin chào mọi người 👋"}
```

Server không tin trường tên người gửi do client tự khai trong gói `chat`: tên được lấy từ phiên đã tham gia. Emoji là chuỗi Unicode, không cần giao thức riêng. WPF có thể hiển thị emoji màu hoặc đơn sắc tùy font/hệ thống.

Giới hạn mặc định: 100 thành viên, tên 24 đơn vị ký tự UTF-16, tin nhắn 2.000 đơn vị ký tự UTF-16, gói JSON 65.536 byte. Một emoji có thể chiếm nhiều đơn vị UTF-16. Các giới hạn nằm trong `Chat.Shared/ChatPacket.cs`.

## Kiểm thử

```powershell
dotnet build Lab1Chat.sln -c Release
dotnet run --project tests/Chat.ProtocolTests -c Release --no-build
dotnet run --project tests/Chat.IntegrationTests -c Release --no-build
```

Các chương trình kiểm thử trả exit code khác 0 nếu thất bại. Bộ integration tự mở server trên port loopback còn trống và đóng tiến trình do chính nó tạo, không cần chạy server thủ công.

Kiểm tra thủ công thêm: mở hai client với hai tên khác nhau; gửi tiếng Việt và emoji hai chiều; thử tên trùng; đóng một client; dừng server; khởi động lại server và kết nối lại.

## Phạm vi bài lab

Đây là **chat phòng chung trong mạng tin cậy**, không phải dịch vụ chat production. Chưa có đăng nhập/mật khẩu, TLS, lưu lịch sử, gửi file, chat riêng hoặc nhiều phòng. Tên thành viên chỉ dùng nhận diện trong phiên, không phải xác thực danh tính. Tin nhắn chỉ được gửi cho thành viên đang online; không lưu lại khi server tắt. Không mở port ứng dụng trực tiếp ra Internet.
