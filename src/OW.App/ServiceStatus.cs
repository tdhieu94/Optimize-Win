using System.Globalization;
using System.Text;
using OW.Client;

namespace OW.App;

internal enum StatusLevel
{
    Info,
    Success,
    Warning,
    Error,
}

internal sealed record ServiceStatus(StatusLevel Level, string Title, string Message, string History)
{
    public static ServiceStatus Checking { get; } = new(StatusLevel.Info, "Đang kiểm tra dịch vụ OW...", string.Empty, string.Empty);

    /// <summary>Asks the OW service whether it is running and what it has done so far.</summary>
    public static async Task<ServiceStatus> QueryAsync()
    {
        try
        {
            await using var client = await OwServiceClient.ConnectAsync(TimeSpan.FromSeconds(3));
            var info = await client.Service.GetInfoAsync();
            var history = await client.Service.GetHistoryAsync(20);

            var text = "Chưa có lần dọn nào.";
            if (history.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var b in history)
                {
                    sb.AppendLine(string.Create(
                        CultureInfo.GetCultureInfo("vi-VN"),
                        $"{b.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm} · {b.Title} · {b.Done} file · {b.Bytes / 1048576.0:0.0} MB"));
                }

                text = sb.ToString();
            }

            return new ServiceStatus(
                StatusLevel.Success,
                "Dịch vụ OW đang chạy",
                $"Phiên bản {info.Version}. File bị dọn sẽ được giữ {info.QuarantineRetentionDays} ngày để bạn khôi phục nếu cần.",
                text);
        }
        catch (TimeoutException)
        {
            return new ServiceStatus(
                StatusLevel.Warning,
                "Chưa kết nối được dịch vụ OW",
                "Dịch vụ có thể chưa được cài hoặc chưa chạy. Hãy cài lại OW bằng file cài đặt rồi bấm \"Kiểm tra lại\".",
                string.Empty);
        }
        catch (Exception ex)
        {
            return new ServiceStatus(StatusLevel.Error, "Có lỗi khi nói chuyện với dịch vụ OW", ex.Message, string.Empty);
        }
    }
}
