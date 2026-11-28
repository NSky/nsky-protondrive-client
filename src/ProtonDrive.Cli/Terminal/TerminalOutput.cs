using System.Globalization;
using System.Text;

namespace NSkyLabs.ProtonDrive.Cli.Terminal;

/// <summary>
/// Keeps text that came from elsewhere (file and folder names from Proton Drive or the disk, error messages) from
/// controlling the terminal. A name can contain escape sequences that change the window title, move the cursor and
/// rewrite earlier lines, or write to the clipboard; on a terminal they are shown as visible escapes instead
/// (<c>\x1B</c>, <c>\u202E</c>), like <c>ls</c> does.
/// </summary>
/// <remarks>
/// <see cref="Install"/> filters everything written to stdout and stderr when they are a terminal. Only the color
/// codes this program uses (<see cref="Ansi"/>) get through. The live progress displays, which move the cursor, write
/// through <see cref="WriteTrusted"/> and escape the names they show themselves. Output to pipes, files and the
/// journal is not changed.
/// </remarks>
internal static class TerminalOutput
{
    // The SGR parameters Ansi and the help text use; anything else (cursor movement, titles, hidden text) is escaped.
    private static readonly HashSet<string> AllowedSgr = ["0", "1", "2", "31", "32", "33", "36", "1;31", "1;33"];

    [ThreadStatic]
    private static bool t_trusted;

    public static bool IsErrorTerminal { get; } = !Console.IsErrorRedirected;

    public static bool IsOutputTerminal { get; } = !Console.IsOutputRedirected;

    public static void Install()
    {
        if (!Console.IsOutputRedirected)
        {
            Console.SetOut(new FilteringWriter(Console.Out));
        }

        if (!Console.IsErrorRedirected)
        {
            Console.SetError(new FilteringWriter(Console.Error));
        }
    }

    /// <summary>Writes text that is meant to control the terminal. Any names in it must already be escaped.</summary>
    public static void WriteTrusted(TextWriter writer, string text)
    {
        t_trusted = true;
        try
        {
            writer.Write(text);
        }
        finally
        {
            t_trusted = false;
        }
    }

    /// <summary>
    /// A name or path shown on one line: every control character is escaped, line breaks and tabs included, so it
    /// cannot look like more than one line. Unchanged when stderr is not a terminal.
    /// </summary>
    public static string Name(string text) => IsErrorTerminal ? Escape(text, keepLayout: false) : text;

    /// <summary>Like <see cref="Name"/>, for stdout.</summary>
    public static string OutputName(string text) => IsOutputTerminal ? Escape(text, keepLayout: false) : text;

    /// <summary>A message for stderr: like the filter, keeps line breaks, tabs and this program's colors.</summary>
    public static string Message(string text) => IsErrorTerminal ? Escape(text, keepLayout: true) : text;

    /// <summary>
    /// Escapes C0 and C1 control characters, DEL, and the invisible characters that reorder or break text (bidi
    /// controls, line and paragraph separators). With <paramref name="keepLayout"/>, line feeds, tabs and the allowed
    /// color codes are kept.
    /// </summary>
    internal static string Escape(string text, bool keepLayout)
    {
        var i = 0;
        while (i < text.Length && !NeedsEscape(text[i], keepLayout))
        {
            i++;
        }

        if (i == text.Length)
        {
            return text;
        }

        var result = new StringBuilder(text.Length + 16);
        result.Append(text, 0, i);
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (keepLayout && c == '\u001b' && TryMatchAllowedSgr(text, i, out var length))
            {
                result.Append(text, i, length);
                i += length - 1;
            }
            else if (NeedsEscape(c, keepLayout))
            {
                result.Append(c <= '\u00ff'
                    ? string.Create(CultureInfo.InvariantCulture, $"\\x{(int)c:X2}")
                    : string.Create(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}"));
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    private static bool NeedsEscape(char c, bool keepLayout)
    {
        if (keepLayout && c is '\n' or '\t')
        {
            return false;
        }

        return c < ' '
            || c is >= '\u007f' and <= '\u009f'
            || c is '\u061c' or '\u200e' or '\u200f' or '\u2028' or '\u2029'
            || c is >= '\u202a' and <= '\u202e'
            || c is >= '\u2066' and <= '\u2069';
    }

    /// <summary>ESC [ params m, with params one of <see cref="AllowedSgr"/>.</summary>
    private static bool TryMatchAllowedSgr(string text, int start, out int length)
    {
        length = 0;
        if (start + 2 >= text.Length || text[start + 1] != '[')
        {
            return false;
        }

        var end = start + 2;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == ';'))
        {
            end++;
        }

        if (end >= text.Length || text[end] != 'm' || !AllowedSgr.Contains(text[(start + 2)..end]))
        {
            return false;
        }

        length = end - start + 1;
        return true;
    }

    /// <summary>Escapes what is written, unless it is written through <see cref="WriteTrusted"/>.</summary>
    private sealed class FilteringWriter(TextWriter inner) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override IFormatProvider FormatProvider => inner.FormatProvider;

        public override string NewLine
        {
#pragma warning disable CS8765 // Matches TextWriter.NewLine, whose setter accepts null.
            get => inner.NewLine;
            set => inner.NewLine = value;
#pragma warning restore CS8765
        }

        public override void Write(char value) => Write(value.ToString());

        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

        public override void Write(string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                inner.Write(t_trusted ? value : Escape(value, keepLayout: true));
            }
        }

        public override void WriteLine(string? value) => Write(value + inner.NewLine);

        public override void WriteLine() => Write(inner.NewLine);

        public override void Flush() => inner.Flush();
    }
}
