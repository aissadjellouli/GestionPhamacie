using Pharma.Cloud.Api.Reception;
using Pharma.Shared;

namespace Pharma.Cloud.Tests;

/// <summary>
/// Voie descendante bornée par le serveur central (revue du 2026-11-22) : une réponse reste lisible par l'officine (1 Mio au plus),
/// échappements JSON compris ; les éléments suivants partent au passage suivant.
/// </summary>
public class DescendantsBornesCentralTests
{
    private static ChangementDescendantDto Element(long version, string donnees) => new("reglage_essai", "c" + version, version, donnees);

    /// <summary>Taille de la réponse telle qu'écrite (accusé d'un lot portant les éléments, mêmes options que l'API).</summary>
    private static int TailleReponse(List<ChangementDescendantDto> elements) =>
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new AccuseLotDto(1, 1, StatutsLot.Accepte, 1, new ChangementsDescendantsDto(ChangementsDescendantsDto.ContratV1, long.MaxValue, elements)),
            DepotCentral.OptionsReponse).Length;

    [Fact]
    public void Au_plus_MaxElements_dans_l_ordre()
    {
        var tous = Enumerable.Range(1, 3000).Select(i => Element(i, "{\"a\":1}")).ToList();
        var r = DepotCentral.Borner(tous);
        Assert.Equal(ChangementsDescendantsDto.MaxElements, r.Count);
        Assert.Equal(tous.Take(r.Count), r);
    }

    [Fact]
    public void Donnees_pleines_de_guillemets_et_d_accents_la_reponse_reste_sous_la_limite_de_l_officine()
    {
        // Chaque guillemet et chaque « é » s'écrit " / é (6 octets) : la longueur brute ne suffit pas
        var donnees = "{" + string.Join(",", Enumerable.Range(0, 3000).Select(i => $"\"é{i}\":\"éé\"")) + "}";
        Assert.True(donnees.Length < ChangementsDescendantsDto.MaxDonnees);
        var tous = Enumerable.Range(1, 200).Select(i => Element(i, donnees)).ToList();
        var r = DepotCentral.Borner(tous);
        Assert.InRange(r.Count, 1, tous.Count - 1);
        Assert.Equal(tous.Take(r.Count), r);
        Assert.True(TailleReponse(r) < ChangementsDescendantsDto.MaxReponse, $"réponse de {TailleReponse(r)} octets");
    }

    [Fact]
    public void Un_element_seul_trop_gros_part_quand_meme()
    {
        // L'officine l'écarte (et le signale) puis passe aux suivants : il ne bloque jamais la file
        var gros = "{\"x\":\"" + new string('a', ChangementsDescendantsDto.OctetsDonneesParReponse + 1000) + "\"}";
        var r = DepotCentral.Borner([Element(1, gros), Element(2, "{\"a\":1}")]);
        Assert.Equal(1, r.Single().Version);
    }
}
