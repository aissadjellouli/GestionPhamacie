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
    public void Un_element_plus_grand_que_la_taille_lisible_part_sans_donnees_avec_sa_version()
    {
        // Revue du 2026-11-23 : le premier élément était gardé tel quel, même trop gros. Il part maintenant SANS données (l'officine l'écarte,
        // acquitte sa version et passe au suivant) et la réponse ne peut plus dépasser la limite de lecture de l'officine
        var gros = "{\"x\":\"" + new string('a', ChangementsDescendantsDto.MaxDonnees) + "\"}";
        var journal = new JournalEssai();
        var r = DepotCentral.Borner([Element(1, gros), Element(2, "{\"a\":1}")], journal);
        Assert.Equal([1L, 2L], r.Select(c => c.Version));
        Assert.Equal("", r[0].DonneesJson);
        Assert.Equal("{\"a\":1}", r[1].DonneesJson);
        Assert.Single(journal.Messages);
        Assert.DoesNotContain("aaaa", journal.Messages[0]);   // jamais les données au journal
        Assert.True(TailleReponse(r) < ChangementsDescendantsDto.MaxReponse);
        // Juste à la taille maximale : gardé tel quel
        var limite = "{\"x\":\"" + new string('a', ChangementsDescendantsDto.MaxDonnees - 8) + "\"}";
        Assert.Equal(ChangementsDescendantsDto.MaxDonnees, limite.Length);
        Assert.Equal(limite, DepotCentral.Borner([Element(1, limite)]).Single().DonneesJson);
    }

    private sealed class JournalEssai : Microsoft.Extensions.Logging.ILogger
    {
        public readonly List<string> Messages = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel niveau) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel niveau, Microsoft.Extensions.Logging.EventId id, TState etat, Exception? exception,
            Func<TState, Exception?, string> formateur) => Messages.Add(formateur(etat, exception));
    }
}
