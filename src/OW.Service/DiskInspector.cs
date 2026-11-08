using System.Management;
using OW.Contracts;
using OW.Core.Disks;

namespace OW.Service;

/// <summary>
/// Reads disk information from Windows' storage management (WMI, root\Microsoft\Windows\Storage).
/// Read-only: nothing here changes the disks.
/// </summary>
public static class DiskInspector
{
    private const string StorageScope = @"\\.\root\Microsoft\Windows\Storage";

    public static IReadOnlyList<DiskInfo> GetDisks()
    {
        var scope = new ManagementScope(StorageScope);
        scope.Connect();

        var lettersByDisk = new Dictionary<int, List<char>>();
        using (var partitions = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_Partition")))
        {
            foreach (var p in partitions.Get().Cast<ManagementObject>())
            {
                using (p)
                {
                    var letter = ToLetter(p["DriveLetter"]);
                    if (letter is null || p["DiskNumber"] is null)
                    {
                        continue;
                    }

                    var disk = Convert.ToInt32(p["DiskNumber"]);
                    if (!lettersByDisk.TryGetValue(disk, out var list))
                    {
                        lettersByDisk[disk] = list = [];
                    }

                    list.Add(letter.Value);
                }
            }
        }

        var result = new List<DiskInfo>();
        using var physical = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"));
        foreach (var d in physical.Get().Cast<ManagementObject>())
        {
            using (d)
            {
                var number = int.TryParse(d["DeviceId"]?.ToString(), out var n) ? n : -1;
                var mediaType = Convert.ToInt32(d["MediaType"] ?? 0) switch
                {
                    3 => "HDD",
                    4 => "SSD",
                    5 => "SCM",
                    _ => "Không rõ",
                };
                var busType = Convert.ToInt32(d["BusType"] ?? 0) switch
                {
                    7 => "USB",
                    8 => "RAID",
                    10 => "SAS",
                    11 => "SATA",
                    17 => "NVMe",
                    _ => "Khác",
                };
                var health = Convert.ToInt32(d["HealthStatus"] ?? 5) switch
                {
                    0 => "Healthy",
                    1 => "Warning",
                    2 => "Unhealthy",
                    _ => null,
                };

                int? temperature = null, wear = null;
                long? hours = null, readErrors = null, writeErrors = null;
                try
                {
                    foreach (var c in d.GetRelated("MSFT_StorageReliabilityCounter").Cast<ManagementObject>())
                    {
                        using (c)
                        {
                            temperature = PositiveInt(c["Temperature"]);
                            wear = NullableInt(c["Wear"]);
                            hours = NullableLong(c["PowerOnHours"]);
                            readErrors = NullableLong(c["ReadErrorsUncorrected"]);
                            writeErrors = NullableLong(c["WriteErrorsUncorrected"]);
                        }
                    }
                }
                catch (ManagementException)
                {
                    // Some disks (USB enclosures, virtual disks) do not expose reliability counters.
                }

                var model = d["FriendlyName"]?.ToString()?.Trim() ?? "Ổ không tên";
                var assessment = DiskHealthEvaluator.Evaluate(new DiskReading(
                    model, mediaType == "SSD", health, temperature, wear, hours, readErrors, writeErrors));

                var volumes = lettersByDisk.TryGetValue(number, out var letters)
                    ? letters.Order().Select(ReadVolume).Where(v => v is not null).Cast<VolumeInfo>().ToList()
                    : [];

                result.Add(new DiskInfo(
                    number,
                    model,
                    mediaType,
                    busType,
                    Convert.ToInt64(d["Size"] ?? 0L),
                    health,
                    temperature,
                    wear,
                    hours,
                    readErrors,
                    writeErrors,
                    assessment.Verdict.ToString(),
                    assessment.Reasons,
                    volumes));
            }
        }

        return result.OrderBy(d => d.Number).ToList();
    }

    private static VolumeInfo? ReadVolume(char letter)
    {
        try
        {
            var drive = new DriveInfo(letter + @":\");
            return drive.IsReady
                ? new VolumeInfo($"{letter}:", drive.VolumeLabel, drive.DriveFormat, drive.TotalSize, drive.TotalFreeSpace)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static char? ToLetter(object? value)
    {
        var c = value switch
        {
            char ch => ch,
            ushort u => (char)u,
            string s when s.Length > 0 => s[0],
            _ => '\0',
        };
        return char.IsAsciiLetter(c) ? char.ToUpperInvariant(c) : null;
    }

    private static int? NullableInt(object? v) => v is null ? null : Convert.ToInt32(v);

    private static int? PositiveInt(object? v) => v is null || Convert.ToInt32(v) <= 0 ? null : Convert.ToInt32(v);

    private static long? NullableLong(object? v) => v is null ? null : Convert.ToInt64(v);
}
