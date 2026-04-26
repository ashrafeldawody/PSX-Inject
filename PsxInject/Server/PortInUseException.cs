namespace PsxInject.Server;

public class PortInUseException : Exception
{
    public int Port { get; }

    public PortInUseException(int port, Exception? inner = null)
        : base($"Port {port} is already in use by another process.", inner)
    {
        Port = port;
    }
}
