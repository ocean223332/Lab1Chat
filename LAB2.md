# Lab 2 — Emoji màu, ảnh và file lớn

Bản này phát triển trực tiếp từ Lab 1, giữ Console Server, WPF Client và tên solution `Lab1Chat.sln`.

## Chạy demo

**Dùng đồng thời server và client bản Lab 2.** Server Lab 1 chưa có chức năng truyền file. Không cần mở thêm cổng: chat, gửi ảnh và tải file đều dùng TCP **5000**, nhưng trên các kết nối riêng.

Với mã nguồn, cần .NET 9 SDK: chạy `Start-Server.cmd`, rồi mở `Start-Client.cmd` ở hai cửa sổ với hai tên khác nhau. Khi demo cùng máy, dùng `127.0.0.1`. Khi dùng máy khác, nhập IP LAN hiện tại của máy server (`ipconfig`), không nhập `0.0.0.0` hoặc `127.0.0.1`.

Với bản self-contained, giải nén **toàn bộ** ZIP và giữ nguyên DLL/thư mục đi kèm:

- Máy server: mở `Chat.Server.exe`, giữ cửa sổ chạy. Ctrl+C để dừng.
- Máy thành viên: mở `Lab1Chat.Client.exe`, nhập IP, port và tên rồi kết nối.
- Không cần cài .NET hoặc Visual Studio cho bản self-contained.
- Chỉ cho phép firewall trong mạng tin cậy; không tắt toàn bộ firewall.

## Các chức năng mới

1. **Emoji màu:** chọn emoji trong bảng; client hiển thị emoji thuộc bộ tích hợp bằng ảnh màu, vẫn truyền Unicode để giữ nội dung tương thích. Emoji ngoài bộ tích hợp dùng font hệ thống.
2. **Gửi ảnh:** chọn PNG/JPG/JPEG. Sau khi upload và kiểm tra thành công, ảnh xuất hiện trong phòng; client tải ảnh xem trước vào bộ nhớ đệm tạm và hiển thị ngay trong hội thoại.
3. **Gửi file:** chọn file, xem tiến độ và có thể hủy. Người nhận bấm lưu/tải trên thẻ file và tự chọn nơi lưu. Ứng dụng không tự mở file nhận được.
4. **File lớn:** tối đa **2 GiB/file**, đáp ứng file từ 500 MB trở lên. Preview chỉ áp dụng ảnh tối đa **20 MiB**; các file khác vẫn gửi dưới dạng tệp.

## Async và Parallel Programming

### I/O bất đồng bộ

`async`/`await` và `CancellationToken` được dùng khi mở TCP, đọc/ghi socket, đọc/ghi file và nhận tin nhắn. Truyền file theo khối **32 KiB**, không dùng `ReadAllBytes` cho file lớn. `IProgress<TransferProgress>` cập nhật tiến độ về giao diện; báo cáo tiến độ được giới hạn tần suất.

Việc upload/download mở kết nối TCP riêng trên cùng cổng server, nên dữ liệu file không chiếm hàng đợi tin nhắn của phòng. Server chỉ phát metadata của file sau khi đã xác minh file; không broadcast hàng trăm MB cho mọi người một cách tự động.

### Nhiều tác vụ đồng thời

Server xử lý các phiên chat và truyền file bằng các tác vụ độc lập; giới hạn số tác vụ truyền file bằng semaphore. Client giới hạn số ảnh preview tải đồng thời để không tải hàng loạt ảnh cùng lúc. Các bài kiểm thử dùng tác vụ đồng thời để chứng minh truyền file không chặn chat.

`EmojiCatalog.PreloadAsync` dùng `Task.Run` cùng `Parallel.ForEach` để giải mã các ảnh emoji độc lập trên các luồng nền, giới hạn mức song song. Mỗi bitmap được `Freeze()` trước khi giao cho luồng giao diện WPF.

I/O bất đồng bộ và CPU song song là hai khái niệm khác nhau: I/O nhường luồng khi đang chờ mạng/ổ đĩa; xử lý song song phân chia công việc độc lập trên nhiều luồng. Xem phần triển khai bộ emoji và kiểm thử để minh họa xử lý độc lập có giới hạn mức song song.

## Giao thức truyền file

Sau `join`, server trả `welcome` chứa token ngẫu nhiên dành riêng cho phiên. Chỉ phiên đang online mới được dùng token đó để mở kết nối upload/download. Token không xuất hiện trong tin nhắn phát cho phòng.

```text
Upload:   fileUpload → fileReady → fileChunk ... → fileComplete(SHA-256)
          server xác minh → fileComplete(metadata) → attachment trong phòng chat
Download: fileDownload → fileReady(metadata) → fileChunk ... → fileComplete(SHA-256)
          client xác minh → chuyển file tạm thành file đích
```

Mỗi khối đi cùng `offset`; server/client kiểm tra thứ tự, kích thước và SHA-256. Dữ liệu nhị phân được mã hóa base64 trong JSON để dùng chung bộ đóng khung hiện có (tăng dung lượng truyền khoảng 1/3). Kết nối TCP tự điều tiết tốc độ đọc/ghi; không tạo hàng đợi toàn bộ file trong RAM.

File trên server được lưu theo ID do server tạo, không dùng đường dẫn do người gửi chỉ định. File tải về được ghi vào tệp tạm trước; chỉ thay file đích sau khi kiểm tra toàn vẹn thành công. Hủy/lỗi không được để lại file tải dở mang tên file đích.

## Giới hạn và an toàn

- Server lưu file tạm trong phiên chạy; **không phải dịch vụ lưu trữ lâu dài**. Sau khi server dừng, liên kết file của phiên cũ không còn bảo đảm dùng được.
- Hạn mức lưu tạm server: **8 GiB**, tối đa **4 lượt upload/download đồng thời**. Cần dung lượng trống thực tế trên ổ đĩa ở cả hai máy.
- Khi mất kết nối/hủy, truyền file bị dừng; chưa hỗ trợ tiếp tục tải từ phần đã tải dở.
- SHA-256 phát hiện dữ liệu lỗi, **không** quét virus và không xác thực tác giả.
- Chưa có TLS hoặc tài khoản/mật khẩu; token phiên không bảo vệ trước người nghe lén mạng. Chỉ dùng LAN/VPN tin cậy; không mở port trực tiếp ra Internet.
- File đã được người nhận lưu sẽ không bị xóa khi họ ngắt kết nối.

## Build và kiểm thử

```powershell
dotnet build Lab1Chat.sln -c Release
dotnet run --project tests/Chat.ProtocolTests -c Release --no-build
dotnet run --project tests/Chat.IntegrationTests -c Release --no-build
dotnet run --project tests/Chat.AttachmentTests -c Release --no-build

# Truyền thật file 512 MiB. Cần vài GiB dung lượng trống để tạo nguồn/bản tải/file tạm.
dotnet run --project tests/Chat.AttachmentTests -c Release --no-build -- --large
```

Để tự đóng gói self-contained:

```powershell
./Publish-Lab2.ps1
```

Kết quả nằm trong `artifacts/`. Khi gửi cho thành viên, gửi ZIP Client hoàn chỉnh, không gửi riêng `.cmd` hay `.exe`.
