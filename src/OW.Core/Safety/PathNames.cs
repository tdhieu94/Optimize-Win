using System.Runtime.InteropServices;
using System.Text;

namespace OW.Core.Safety;

public static partial class PathNames
{
    /// <summary>
    /// Turns 8.3 short names (C:\PROGRA~1\...) into their real long names on Windows,
    /// so protection rules see the true folder. Paths that do not exist are returned unchanged.
    /// </summary>
    public static string ExpandShortNames(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(path) || !path.Contains('~'))
        {
            return path;
        }

        var buffer = new StringBuilder(1024);
        var length = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
        if (length > buffer.Capacity)
        {
            buffer.Capacity = (int)length;
            length = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
        }

        return length == 0 ? path : buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint bufferLength);
}
