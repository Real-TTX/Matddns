using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Matddns.Services;

/// <summary>Checks reachability of a source IP for failover validation (ping / open TCP port).</summary>
public class ReachabilityChecker
{
    private const int TimeoutMs = 2500;

    public async Task<bool> PingAsync(string ip)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, TimeoutMs);
            return reply.Status == IPStatus.Success;
        }
        catch { return false; }
    }

    public async Task<bool> TcpAsync(string ip, int port, CancellationToken ct)
    {
        if (port is < 1 or > 65535) return false;
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeoutMs);
            await client.ConnectAsync(ip, port, cts.Token);
            return client.Connected;
        }
        catch { return false; }
    }
}
