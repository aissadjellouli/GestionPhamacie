using System.Net.Sockets;
using System.Text;

namespace Pharma.Infrastructure.Impression;

/// <summary>Envoi direct à une imprimante réseau (port 9100).</summary>
public static class ImprimanteReseau
{
    public static async Task EnvoyerAsync(string ip, string contenu, CancellationToken ct = default)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(ip, 9100, ct);
        await using var flux = client.GetStream();
        await flux.WriteAsync(Encoding.UTF8.GetBytes(contenu), ct);
    }
}
