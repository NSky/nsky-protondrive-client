namespace NSkyLabs.ProtonDrive.Cli.Terminal;

/// <summary>Login prompts on the console; secrets are read without echo.</summary>
internal sealed class ConsoleLoginPrompter : ILoginPrompter
{
    public string ReadUsername() => ConsolePrompt.ReadRequiredLine("Username: ");

    public byte[] ReadPassword() => ConsolePrompt.ReadSecret("Password: ");

    public string ReadSecondFactorCode() => ConsolePrompt.ReadRequiredLine("Two-factor code (TOTP or recovery code): ");

    public byte[] ReadMailboxPassword() => ConsolePrompt.ReadSecret("Mailbox password: ");

    public void ShowError(string message) => Console.Error.WriteLine(message);
}
