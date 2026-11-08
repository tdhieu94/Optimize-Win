using OW.Core.Safety;

namespace OW.Core.Tests;

public class PathGuardTests
{
    private static readonly PathGuard Windows = new(
        PathStyle.Windows,
        [@"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData\Microsoft"],
        [@"C:\Users\hieu\Documents", @"C:\Users\hieu\Pictures", @"C:\Users\hieu\Downloads"]);

    [Theory]
    [InlineData(@"C:\Users\hieu\AppData\Local\Temp\abc.tmp")]
    [InlineData(@"D:\Cache\old.log")]
    [InlineData(@"C:/Users/hieu/AppData/Local/Temp/abc.tmp")]
    [InlineData(@"C:\Windowsold\x.txt")]
    public void Allows_ordinary_files_for_rules(string path) =>
        Assert.True(Windows.Check(path, ActionOrigin.Rule).Allowed);

    [Theory]
    [InlineData(@"C:\Windows\System32\kernel32.dll")]
    [InlineData(@"c:\windows\system32\drivers\etc\hosts")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\")]
    [InlineData(@"C:\Windows.\notepad.exe")]
    [InlineData(@"C:\Windows \notepad.exe")]
    [InlineData(@"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files (x86)\App\app.exe")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\x.dat")]
    [InlineData(@"C:\\Windows\\\System32\x.dll")]
    public void Never_allows_system_folders(string path)
    {
        Assert.False(Windows.Check(path, ActionOrigin.Rule).Allowed);
        Assert.False(Windows.Check(path, ActionOrigin.UserSelected).Allowed);
    }

    [Theory]
    [InlineData(@"C:\Users\hieu\AppData\Local\Temp\..\..\..\..\..\Windows\x.dll")]
    [InlineData(@"C:\Users\hieu\Temp\..")]
    [InlineData(@"C:\Users\hieu\Temp\.\x.txt")]
    [InlineData(@"C:\Users\hieu\Temp\...\x.txt")]
    [InlineData(@"relative\path.txt")]
    [InlineData(@"\Windows\x.dll")]
    [InlineData(@"\\server\share\x.txt")]
    [InlineData(@"\\?\C:\Windows\x.dll")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"C:\")]
    [InlineData(@"D:")]
    [InlineData(@"C:\Temp\*.tmp")]
    [InlineData(@"C:\Temp\file.txt:hidden")]
    [InlineData(@"C:\PROGRA~1\App\app.exe")]
    [InlineData("")]
    [InlineData("   ")]
    public void Refuses_tricky_paths(string path)
    {
        Assert.False(Windows.Check(path, ActionOrigin.Rule).Allowed);
        Assert.False(Windows.Check(path, ActionOrigin.UserSelected).Allowed);
    }

    [Fact]
    public void Refuses_null() => Assert.False(Windows.Check(null, ActionOrigin.UserSelected).Allowed);

    [Theory]
    [InlineData(@"C:\Users\hieu\Documents\report.docx")]
    [InlineData(@"C:\Users\hieu\Pictures\2024\IMG_0001.jpg")]
    [InlineData(@"c:\users\HIEU\downloads\setup.exe")]
    public void User_data_only_when_user_picked_it(string path)
    {
        Assert.False(Windows.Check(path, ActionOrigin.Rule).Allowed);
        Assert.True(Windows.Check(path, ActionOrigin.UserSelected).Allowed);
    }

    [Fact]
    public void Never_the_whole_user_folder()
    {
        Assert.False(Windows.Check(@"C:\Users\hieu\Documents", ActionOrigin.UserSelected).Allowed);
        Assert.False(Windows.Check(@"C:\Users\hieu\Documents\", ActionOrigin.UserSelected).Allowed);
    }

    [Fact]
    public void Gives_a_reason_when_refusing()
    {
        var result = Windows.Check(@"C:\Windows\x.dll", ActionOrigin.Rule);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public void Unix_style_rules()
    {
        var unix = new PathGuard(PathStyle.Unix, ["/usr", "/etc"], ["/home/hieu/Documents"]);
        Assert.True(unix.Check("/tmp/x.tmp", ActionOrigin.Rule).Allowed);
        Assert.False(unix.Check("/etc/passwd", ActionOrigin.UserSelected).Allowed);
        Assert.False(unix.Check("/tmp/../etc/passwd", ActionOrigin.UserSelected).Allowed);
        Assert.False(unix.Check("/home/hieu/Documents/a.txt", ActionOrigin.Rule).Allowed);
        Assert.True(unix.Check("/home/hieu/Documents/a.txt", ActionOrigin.UserSelected).Allowed);
        Assert.False(unix.Check("/", ActionOrigin.UserSelected).Allowed);
    }
}

public class PathGuardJunkFolderTests
{
    private static readonly PathGuard Guard = new(
        PathStyle.Windows,
        [@"C:\Windows", @"C:\ProgramData\Microsoft"],
        [@"C:\Users\hieu\Documents"],
        [@"C:\Windows\Temp", @"C:\Windows\SoftwareDistribution\Download", @"C:\ProgramData\Microsoft\Windows\WER\ReportArchive"]);

    [Theory]
    [InlineData(@"C:\Windows\Temp\setup.log")]
    [InlineData(@"c:\windows\temp\sub\x.tmp")]
    [InlineData(@"C:\Windows\SoftwareDistribution\Download\abc123\x.cab")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\WER\ReportArchive\r1\Report.wer")]
    public void Rules_may_clean_inside_windows_junk_folders(string path) =>
        Assert.True(Guard.Check(path, ActionOrigin.Rule).Allowed);

    [Theory]
    [InlineData(@"C:\Windows\Temp\setup.log")]
    [InlineData(@"C:\Windows\SoftwareDistribution\Download\abc123\x.cab")]
    public void A_user_pick_still_cannot_touch_windows(string path) =>
        Assert.False(Guard.Check(path, ActionOrigin.UserSelected).Allowed);

    [Theory]
    [InlineData(@"C:\Windows\Temp")]
    [InlineData(@"C:\Windows\Temp\")]
    [InlineData(@"C:\Windows\SoftwareDistribution\Download")]
    [InlineData(@"C:\Windows\TempX\x.tmp")]
    [InlineData(@"C:\Windows\Temp2\x.tmp")]
    [InlineData(@"C:\Windows\SoftwareDistribution\DataStore\DataStore.edb")]
    [InlineData(@"C:\Windows\System32\kernel32.dll")]
    [InlineData(@"C:\Windows\Temp\..\System32\kernel32.dll")]
    [InlineData(@"C:\WINDOW~1\Temp\x.tmp")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\WER\Temp\x.tmp")]
    public void Everything_else_in_windows_stays_protected(string path) =>
        Assert.False(Guard.Check(path, ActionOrigin.Rule).Allowed);

    [Fact]
    public void Without_junk_folders_windows_temp_is_protected() =>
        Assert.False(new PathGuard(PathStyle.Windows, [@"C:\Windows"], []).Check(@"C:\Windows\Temp\x.tmp", ActionOrigin.Rule).Allowed);
}
