# Optimize Win (OW)

Phần mềm cá nhân giúp Windows 11 chạy gọn hơn: dọn file rác, xem ổ cứng đầy vì đâu, quản lý chương trình khởi động và theo dõi sức khỏe ổ SSD.

Nguyên tắc số 1: **không thao tác nào mà bạn không xem trước và không hoàn tác được.**

Kế hoạch chi tiết: [Kế hoạch thiết kế phần mềm Optimize Win](https://claude.ai/artifact/7BXKJZBPeZkaD9w82bs8t4)

## Tình trạng

| Giai đoạn | Nội dung | Trạng thái |
| --- | --- | --- |
| GĐ0 · Nền tảng | Lớp an toàn (nhật ký, cách ly, hoàn tác), dịch vụ admin, kênh giao tiếp, build tự động | Xong |
| GĐ1 · MVP | Phân tích dung lượng, dọn rác cơ bản, quản lý khởi động, thông tin ổ và SMART | Đang làm: sức khỏe ổ đĩa (xong), phân tích dung lượng |
| GĐ2 · Mở rộng | File trùng, tối ưu dịch vụ, dọn thành phần Windows, lịch dọn | Chưa bắt đầu |
| GĐ3 · Hoàn thiện | Gỡ ứng dụng, cảnh báo sức khỏe ổ, báo cáo | Chưa bắt đầu |
| GĐ4 · Đóng gói | Bộ cài MSI, hướng dẫn có hình | Chưa bắt đầu |

## Cách lấy bản chạy thử

1. Vào tab **Actions** của repo này, mở lần build mới nhất có dấu tích xanh.
2. Kéo xuống mục **Artifacts**, bấm vào `OptimizeWin-...` để tải file zip.
3. Giải nén, bấm đúp **Cai-dat.cmd**, đồng ý khi Windows hỏi quyền admin.
4. Mở **Optimize Win** từ Start Menu.

Muốn gỡ: bấm đúp **Go-cai-dat.cmd** trong `C:\Program Files\OptimizeWin`. Nhật ký và vùng cách ly ở `C:\ProgramData\OptimizeWin` được giữ lại.

## Cấu trúc

```
src/OW.Core        Lớp an toàn: kiểm tra đường dẫn, nhật ký SQLite, vùng cách ly, hoàn tác
src/OW.Contracts   Danh sách lệnh giao diện được phép gửi cho dịch vụ (chỉ lệnh có kiểu)
src/OW.Client      Kết nối từ giao diện tới dịch vụ qua named pipe
src/OW.Service     Dịch vụ Windows chạy quyền admin (C:\Program Files\OptimizeWin\Service)
src/OW.App         Giao diện WinUI 3, chạy quyền người dùng thường
tests/             Kiểm thử tự động cho lớp an toàn
scripts/           Script cài và gỡ
```

## Phát triển

- .NET 10 SDK. Kiểm thử lớp an toàn: `dotnet test tests/OW.Core.Tests`
- Giao diện WinUI 3 chỉ build được trên Windows; GitHub Actions build toàn bộ trên `windows-latest`.
