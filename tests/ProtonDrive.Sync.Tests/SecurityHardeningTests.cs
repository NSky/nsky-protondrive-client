using NSkyLabs.ProtonDrive.Cli.Terminal;
using NSkyLabs.ProtonDrive.Core.Diagnostics;
using NSkyLabs.ProtonDrive.Core.IO;

namespace NSkyLabs.ProtonDrive.Sync.Tests;

/// <summary>Names cannot control the terminal, downloads never write through a planted link, traces hide tokens.</summary>
public sealed class SecurityHardeningTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("protondrive-tests-").FullName;

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("a\u001b]0;owned\u0007b", "a\\x1B]0;owned\\x07b")]
    [InlineData("line\rover", "line\\x0Dover")]
    [InlineData("two\nlines", "two\\x0Alines")]
    [InlineData("tab\there", "tab\\x09here")]
    [InlineData("csi\u009b2J", "csi\\x9B2J")]
    [InlineData("gpj.\u202eexe", "gpj.\\u202Eexe")]
    [InlineData("caf\u00e9 \u65e5\u672c", "caf\u00e9 \u65e5\u672c")]
    public void A_name_is_shown_on_one_line_without_control_characters(string name, string shown)
    {
        Assert.Equal(shown, TerminalOutput.Escape(name, keepLayout: false));
    }

    [Fact]
    public void Messages_keep_line_breaks_and_this_programs_colors_only()
    {
        var colored = "\u001b[1;33mWarning\u001b[0m: two\nlines\tok";
        Assert.Equal(colored, TerminalOutput.Escape(colored, keepLayout: true));

        // Cursor movement, clearing, hidden text and window titles are escaped.
        Assert.Equal("\\x1B[2A\\x1B[J", TerminalOutput.Escape("\u001b[2A\u001b[J", keepLayout: true));
        Assert.Equal("\\x1B[8mhidden", TerminalOutput.Escape("\u001b[8mhidden", keepLayout: true));
        Assert.Equal("\\x1B]0;title\\x07", TerminalOutput.Escape("\u001b]0;title\u0007", keepLayout: true));
        Assert.Equal("end\\x1B", TerminalOutput.Escape("end\u001b", keepLayout: true));
    }

    [Fact]
    public void A_partial_file_is_created_new()
    {
        var path = Path.Combine(_directory, "file.protondrive-part");
        File.WriteAllText(path, "left over from an interrupted download");

        using (var stream = PartialFile.Create(path))
        {
            stream.Write("new"u8);
        }

        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void A_planted_link_is_replaced_and_its_target_left_alone()
    {
        var target = Path.Combine(_directory, "victim");
        File.WriteAllText(target, "precious");
        var path = Path.Combine(_directory, "file.protondrive-part");
        File.CreateSymbolicLink(path, target);

        using (var stream = PartialFile.Create(path))
        {
            stream.Write("downloaded"u8);
        }

        Assert.Equal("precious", File.ReadAllText(target));
        Assert.Null(new FileInfo(path).LinkTarget);
        Assert.Equal("downloaded", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("Authorization", true)]
    [InlineData("x-pm-uid", true)]
    [InlineData("Cookie", true)]
    [InlineData("pm-storage-token", true)]
    [InlineData("x-pm-appversion", false)]
    [InlineData("Accept", false)]
    public void The_http_trace_hides_credentials_and_tokens(string header, bool secret)
    {
        Assert.Equal(secret, HttpTrace.IsSecret(header));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }
}
