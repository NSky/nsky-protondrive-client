using System.Text;

namespace NSkyLabs.ProtonDrive.Cli.Terminal;

internal static class ConsolePrompt
{
    public static string ReadLine(string prompt)
    {
        Console.Error.Write(prompt);
        var line = Console.ReadLine() ?? throw new OperationCanceledException("Input stream closed.");
        ActivityStatus.InputLineEnded();
        return line.Trim();
    }

    public static string ReadRequiredLine(string prompt)
    {
        while (true)
        {
            var value = ReadLine(prompt);
            if (value.Length > 0)
            {
                return value;
            }
        }
    }

    /// <summary>
    /// Reads a secret without echoing it and returns it as UTF-8 bytes. The caller should clear the array when done.
    /// </summary>
    public static byte[] ReadSecret(string prompt)
    {
        Console.Error.Write(prompt);

        if (Console.IsInputRedirected)
        {
            var line = Console.ReadLine() ?? throw new OperationCanceledException("Input stream closed.");
            ActivityStatus.InputLineEnded();
            return Encoding.UTF8.GetBytes(line);
        }

        var chars = new List<char>(64);
        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Enter)
                {
                    break;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (chars.Count > 0)
                    {
                        chars.RemoveAt(chars.Count - 1);
                    }

                    continue;
                }

                if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    throw new OperationCanceledException();
                }

                if (!char.IsControl(key.KeyChar))
                {
                    chars.Add(key.KeyChar);
                }
            }

            var buffer = chars.ToArray();
            try
            {
                return Encoding.UTF8.GetBytes(buffer);
            }
            finally
            {
                Array.Clear(buffer);
            }
        }
        finally
        {
            Console.Error.WriteLine();
            for (var i = 0; i < chars.Count; i++)
            {
                chars[i] = '\0';
            }
        }
    }
}
