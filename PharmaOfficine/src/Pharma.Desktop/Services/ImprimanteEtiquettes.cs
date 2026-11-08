using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Pharma.Desktop.Services;

/// <summary>
/// Imprimante thermique d'étiquettes (ZPL, port 9100) du poste, déclarée par la variable
/// d'environnement PHARMA_IMPRIMANTE_ETIQUETTES (adresse IP). Sans elle, seule la planche PDF est proposée.
/// </summary>
public sealed class ImprimanteEtiquettes
{
    public string? Adresse { get; } =
        Environment.GetEnvironmentVariable("PHARMA_IMPRIMANTE_ETIQUETTES") is { Length: > 0 } ip ? ip.Trim() : null;

    public bool EstConfiguree => Adresse is not null;

    /// <summary>Envoie le ZPL ; renvoie un message d'erreur lisible, ou null si l'envoi a réussi.</summary>
    public async Task<string?> EnvoyerAsync(string zpl)
    {
        if (Adresse is null) return "Aucune imprimante d’étiquettes déclarée pour ce poste.";
        try
        {
            using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = new TcpClient();
            await client.ConnectAsync(Adresse, 9100, delai.Token);
            await using var flux = client.GetStream();
            await flux.WriteAsync(Encoding.UTF8.GetBytes(zpl), delai.Token);
            return null;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or IOException)
        {
            return $"Imprimante d’étiquettes {Adresse} injoignable : la planche PDF reste disponible.";
        }
    }
}
