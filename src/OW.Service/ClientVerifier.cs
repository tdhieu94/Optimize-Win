using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace OW.Service;

/// <summary>
/// Makes sure the program on the other end of the pipe is OW's own UI, installed one folder above the service
/// (in Program Files, which only administrators can write to). Any other program is disconnected.
/// </summary>
public sealed class ClientVerifier(ILogger<ClientVerifier> logger)
{
    // Installed layout: ...\OptimizeWin\OptimizeWin.exe and ...\OptimizeWin\Service\OptimizeWin.Service.exe
    private readonly string _expectedExe = Path.Combine(
        Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.FullName,
        ServiceConstants.AppExeName);

    public bool IsTrusted(NamedPipeServerStream pipe)
    {
        var path = GetClientImagePath(pipe);
        if (path is null)
        {
            logger.LogWarning("Rejected pipe client: could not identify the process");
            return false;
        }

        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(_expectedExe), StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Rejected pipe client {Path}", path);
            return false;
        }

        return true;
    }

    /// <summary>The Windows account (SID) of the program on the other end of the pipe, read from its process token.</summary>
    public static string? GetClientUserSid(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
        {
            return null;
        }

        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token))
            {
                return null;
            }

            try
            {
                using var identity = new System.Security.Principal.WindowsIdentity(token);
                return identity.User?.Value;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static string? GetClientImagePath(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
        {
            return null;
        }

        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
