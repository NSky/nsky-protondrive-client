using System.Net.Sockets;
using System.Text;

namespace NSkyLabs.ProtonDrive.Cli.Notifications;

/// <summary>
/// Sets the one-line status shown by <c>systemctl --user status</c> (the sd_notify <c>STATUS=</c> message).
/// Only active when started by systemd with <c>NotifyAccess=main</c>; otherwise it does nothing.
/// </summary>
internal static class SystemdStatus
{
    private static readonly string? SocketPath = GetSocketPath();

    public static bool IsAvailable => SocketPath is not null;

    public static void Set(string status)
    {
        if (SocketPath is null)
        {
            return;
        }

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
            socket.Send(Encoding.UTF8.GetBytes("STATUS=" + status.Replace('\n', ' ')));
        }
        catch (SocketException)
        {
            // Best effort.
        }
    }

    private static string? GetSocketPath()
    {
        var path = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        // A leading '@' means a socket in the abstract namespace.
        return path[0] == '@' ? "\0" + path[1..] : path;
    }
}
