# Kết quả kiểm thử Lab 2

Môi trường thực hiện: Windows x64, .NET SDK 9.0.306. Kiểm thử trên loopback, không thay thế kiểm tra firewall/router trên máy thành viên.

## Đã xác nhận

- Kiểm thử giao thức: 10/10 đạt, gồm JSON UTF-8, emoji, xuống dòng, TCP chia/gộp khối, ghi đồng thời, EOF/lỗi giới hạn, và khối nhị phân 32 KiB ở offset 512 MiB.
- Kiểm thử chat TCP: 10/10 đạt, gồm join, tên trùng, tên Unicode NFC, tên người gửi/thời gian do server quyết định, leave/mất kết nối, dữ liệu sai và chat đồng thời.
- Kiểm thử file: 6/6 đạt khi chạy với `--large`, gồm upload/download, token/tên file sai, kích thước/offset/hash sai, hủy giữ nguyên file đích, truyền đồng thời không chặn chat.
- Đã truyền **file thật 536.870.912 byte (512 MiB)** từ client lên server rồi tải xuống, kiểm tra độ dài và SHA-256 bằng nhau. Lượt kiểm thử có làm mới số liệu bộ nhớ của cả tiến trình test và server đạt điều kiện mức tăng dưới 256 MiB mỗi tiến trình. Đây là mức tăng đo trong bài test, không phải cam kết RAM cố định cho mọi máy.
- Kiểm thử vòng đời server: dừng với client đã join/chưa join, giải phóng port, 20 lượt start/stop nhanh đều đạt.
- Kiểm thử WPF ngoài màn hình: tải XAML, bộ emoji màu và render inline, nút gửi ảnh/file, upload qua đúng đường xử lý UI, nhận metadata, tự tải ảnh preview giữ tỷ lệ, thẻ lưu file, chat Unicode, trạng thái offline, timeout, ngắt/kết nối lại đều đạt; đã xem ảnh render ở 1120×760 và 920×640.

## Tái hiện

```powershell
dotnet build Lab1Chat.sln -c Release
dotnet run --project tests/Chat.ProtocolTests -c Release --no-build
dotnet run --project tests/Chat.IntegrationTests -c Release --no-build
dotnet run --project tests/Chat.AttachmentTests -c Release --no-build -- --large
```

Nên dành vài GiB trống trên ổ chứa thư mục tạm để chạy test file lớn. Tốc độ loopback không đại diện cho tốc độ Wi-Fi thực tế.
