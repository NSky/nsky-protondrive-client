using System.ComponentModel;
using System.Diagnostics;

namespace NSkyLabs.ProtonDrive.Core.Auth;

/// <summary>The session could not be encrypted or decrypted.</summary>
internal sealed class SessionProtectionException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>Encrypts the saved session at rest (see <see cref="SessionStore"/>).</summary>
internal interface ISessionProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>
/// Encrypts with <c>systemd-creds --user</c> (systemd 256 or newer): the key is held by the system (and the TPM,
/// when there is one) and the credential is bound to this user, so a copy of the file is useless anywhere else.
/// </summary>
/// <remarks>
/// It works without a desktop session or keyring, so the background sync keeps running after logout and after a
/// reboot. Other programs running as the same user can still ask systemd to decrypt it: this protects copies of the
/// file (backups, a stolen disk, a pasted file), not the running account. Decrypting takes about 1.5 seconds,
/// which is why <see cref="SessionStore"/> decrypts at most once per change of the file.
/// </remarks>
internal sealed class SystemdCredentialProtector : ISessionProtector
{
    /// <summary>Embedded in the credential: one encrypted for another purpose is refused.</summary>
    private const string CredentialName = "protondrive-session";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public static SystemdCredentialProtector Instance { get; } = new();

    public byte[] Protect(byte[] plaintext) => Run("encrypt", plaintext);

    public byte[] Unprotect(byte[] ciphertext) => Run("decrypt", ciphertext);

    private static byte[] Run(string verb, byte[] input)
    {
        var startInfo = new ProcessStartInfo("systemd-creds")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in new[] { "--user", verb, $"--name={CredentialName}", "-", "-" })
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new SessionProtectionException("Could not start systemd-creds.");
        }
        catch (Win32Exception ex)
        {
            throw new SessionProtectionException("systemd-creds was not found; encrypting the session needs systemd 256 or newer.", ex);
        }

        using (process)
        {
            var output = new MemoryStream();
            var copyOutput = process.StandardOutput.BaseStream.CopyToAsync(output);
            var error = process.StandardError.ReadToEndAsync();

            try
            {
                process.StandardInput.BaseStream.Write(input);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // It exited early; the exit code and its message say why.
            }

            if (!process.WaitForExit(Timeout))
            {
                process.Kill();
                throw new SessionProtectionException($"systemd-creds {verb} did not finish within {Timeout.TotalSeconds:0} seconds.");
            }

            copyOutput.Wait();
            if (process.ExitCode != 0)
            {
                var message = error.Result.Trim();
                throw new SessionProtectionException(
                    $"systemd-creds could not {verb} the session{(message.Length > 0 ? $": {message}" : $" (exit code {process.ExitCode})")}.");
            }

            return output.ToArray();
        }
    }
}
