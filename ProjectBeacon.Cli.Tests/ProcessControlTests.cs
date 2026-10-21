namespace ProjectBeacon.Cli.Tests;

using System.Net;
using System.Net.Sockets;
using ProjectBeacon.Cli.Client;

public sealed class ProcessControlTests
{
    [Fact]
    public void FindListeningPortPid_DetectsLocalListener()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.Equal(Environment.ProcessId, ProcessControl.FindListeningPortPid(port));
        listener.Stop();
        Assert.Equal(0, ProcessControl.FindListeningPortPid(port));
    }
}
