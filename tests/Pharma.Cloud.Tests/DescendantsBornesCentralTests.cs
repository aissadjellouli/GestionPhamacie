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

    /// <summary>Forme de la réponse avant le 2026-11-27 (sans DateCentrale), telle que la lit une officine antérieure.</summary>
    private sealed record AncienneReponse(string Contrat, long VersionMax, IReadOnlyList<ChangementDescendantDto> Changements);

    private sealed class TempsFixe(DateTimeOffset t) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => t;
    }

    [Fact]
    public void La_reponse_descendante_porte_la_date_du_central_et_une_officine_anterieure_la_lit_encore()
    {
        // Décision du titulaire du 2026-11-27 (point 4) : date du central, date plancher de l'offre de l'officine ; champ facultatif
        var maintenant = new DateTimeOffset(2026, 11, 27, 10, 15, 0, TimeSpan.Zero);
        var d = DepotCentral.Dater(new ChangementsDescendantsDto(ChangementsDescendantsDto.ContratV1, 3, [Element(3, "{\"a\":1}")]), new TempsFixe(maintenant));
        Assert.Equal(maintenant, d.DateCentrale);
        Assert.Equal(ChangementsDescendantsDto.ContratV1, d.Contrat);   // contrat desc-v1 inchangé
        var json = System.Text.Json.JsonSerializer.Serialize(new AccuseLotDto(1, 1, StatutsLot.Accepte, 1, d), DepotCentral.OptionsReponse);
        Assert.Contains("\"dateCentrale\":\"2026-11-27T10:15:00+00:00\"", json);
        // Officine antérieure : le champ inconnu est ignoré, les éléments se lisent comme avant
        var ancienne = System.Text.Json.JsonSerializer.Deserialize<AncienneReponse>(System.Text.Json.JsonSerializer.Serialize(d, DepotCentral.OptionsReponse),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal((3L, 1), (ancienne.VersionMax, ancienne.Changements.Count));
        // Échange léger et accusé d'un lot : les deux passent par DescendantsAsync, qui date la réponse
        var source = File.ReadAllText(Path.Combine(Racine(), "src", "Pharma.Cloud.Api", "Reception", "DepotCentral.cs"));
        var bloc = source[source.IndexOf("public async Task<Pharma.Shared.ChangementsDescendantsDto> DescendantsAsync", StringComparison.Ordinal)..];
        Assert.Contains("return Dater(", bloc[..bloc.IndexOf("public static async Task LigneAttendueAsync", StringComparison.Ordinal)]);
    }

    private static string Racine()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "PharmaOfficine.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
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
