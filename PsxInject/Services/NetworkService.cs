using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PsxInject.Services;

public static class NetworkService
{
    public static IReadOnlyList<string> GetLocalIPv4Addresses()
    {
        var addresses = new List<string>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                var props = nic.GetIPProperties();
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;

                    addresses.Add(ua.Address.ToString());
                }
            }
        }
        catch
        {
            // best-effort; fall back to empty list
        }

        return addresses;
    }

    /// <summary>
    /// Conservative port-availability check. Treats the port as taken if ANY
    /// of the following is true:
    ///   • IPv4 loopback bind fails
    ///   • IPv6 loopback bind fails (when IPv6 is supported on this machine)
    ///   • IPv4 wildcard bind fails
    ///   • A TCP connect to localhost:port succeeds (anything is listening)
    /// </summary>
    public static bool IsPortAvailable(int port)
    {
        if (CanConnectToLocalhost(port)) return false;
        if (!TryProbeBind(AddressFamily.InterNetwork, IPAddress.Loopback, port)) return false;
        if (!TryProbeBindV6(port)) return false;
        if (!TryProbeBind(AddressFamily.InterNetwork, IPAddress.Any, port)) return false;
        return true;
    }

    private static bool TryProbeBind(AddressFamily family, IPAddress address, int port)
    {
        Socket? socket = null;
        try
        {
            socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp)
            {
                ExclusiveAddressUse = true
            };
            socket.Bind(new IPEndPoint(address, port));
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return false;
        }
        catch
        {
            // Other errors (e.g. permission denied) — be conservative and treat
            // as available so we get a clear error from the real bind later.
            return true;
        }
        finally
        {
            try { socket?.Close(); } catch { }
        }
    }

    private static bool TryProbeBindV6(int port)
    {
        if (!Socket.OSSupportsIPv6) return true;

        Socket? socket = null;
        try
        {
            socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
            {
                ExclusiveAddressUse = true,
                DualMode = false
            };
            socket.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return false;
        }
        catch
        {
            // IPv6 unsupported in some configs — don't fail-close on this branch.
            return true;
        }
        finally
        {
            try { socket?.Close(); } catch { }
        }
    }

    /// <summary>
    /// Active probe: try to TCP-connect to localhost:port. If anything answers,
    /// the port is in use regardless of how it's bound.
    /// </summary>
    private static bool CanConnectToLocalhost(int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync("localhost", port);
            if (!connect.Wait(300)) return false;     // timeout — assume free
            return client.Connected;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            // Connection actively refused → nothing listening → port is free.
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the first available port starting from <paramref name="preferred"/>,
    /// scanning up to <paramref name="range"/> ports. Falls back to an OS-assigned
    /// ephemeral port if nothing in the range is free.
    /// </summary>
    public static int FindAvailablePort(int preferred, int range = 25)
    {
        if (preferred is > 0 and < 65535 && IsPortAvailable(preferred)) return preferred;

        for (int p = Math.Max(1, preferred + 1); p <= preferred + range && p < 65535; p++)
        {
            if (IsPortAvailable(p)) return p;
        }

        try
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
        catch
        {
            return preferred;
        }
    }
}
