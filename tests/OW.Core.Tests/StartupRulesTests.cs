using OW.Core.Startup;

namespace OW.Core.Tests;

public class StartupRulesTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData(new byte[0], true)]
    [InlineData(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, false)]
    [InlineData(new byte[] { 7, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, false)]
    public void Reads_task_manager_enabled_flag(byte[]? data, bool enabled) =>
        Assert.Equal(enabled, StartupRules.IsEnabled(data));

    [Theory]
    [InlineData(@"""C:\Program Files\App\app.exe"" --minimized", @"C:\Program Files\App\app.exe")]
    [InlineData(@"""C:\Program Files\App\app.exe""", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files\App\app.exe /background", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Tools\run.cmd", @"C:\Tools\run.cmd")]
    [InlineData(@"C:\Windows\system32\rundll32.exe,Something", @"C:\Windows\system32\rundll32.exe")]
    [InlineData(@"   ", null)]
    [InlineData(null, null)]
    [InlineData(@"""unterminated", null)]
    public void Finds_the_program_in_a_command(string? command, string? expected) =>
        Assert.Equal(expected, StartupRules.ExtractExecutable(command));

    [Fact]
    public void Expands_environment_variables_first()
    {
        var exe = StartupRules.ExtractExecutable(@"%ProgramFiles%\App\a.exe -x", s => s.Replace("%ProgramFiles%", @"C:\Program Files"));
        Assert.Equal(@"C:\Program Files\App\a.exe", exe);
    }

    [Theory]
    [InlineData("SecurityHealth", @"C:\Windows\system32\SecurityHealthSystray.exe", "Microsoft Corporation", true, StartupAdvice.KeepWindows)]
    [InlineData("Something", @"C:\Windows\System32\x.exe", null, true, StartupAdvice.KeepWindows)]
    [InlineData("RtkAudUService", @"C:\Program Files\Realtek\a.exe", "Realtek Semiconductor", true, StartupAdvice.KeepHardware)]
    [InlineData("Discord", @"C:\Users\h\AppData\Local\Discord\Update.exe", "Discord Inc.", true, StartupAdvice.None)]
    [InlineData("Old", @"C:\Gone\old.exe", null, false, StartupAdvice.Broken)]
    [InlineData("Windowsold", @"C:\Windowsold\x.exe", null, true, StartupAdvice.None)]
    public void Gives_simple_advice(string name, string exe, string? publisher, bool exists, StartupAdvice expected) =>
        Assert.Equal(expected, StartupRules.Advise(name, exe, publisher, exists, @"C:\Windows"));
}
