using System.Net.NetworkInformation;
using System.Net.Sockets;
using Matddns.Models;

namespace Matddns.Services;

/// <summary>Prüft die Erreichbarkeit einer Quell-IP für Failover-Validierung (Ping / offener TCP-Port).</summary>
public class ReachabilityChecker
{
    private const int TimeoutMs = 2500;

    public async Task<bool> CheckAsync(RuleValidation mode, string ip, int port, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
        return mode switch
        {
            RuleValidation.Ping => await PingAsync(ip),
            RuleValidation.TcpPort => await TcpAsync(ip, port, ct),
            _ => true
        };
    }

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
