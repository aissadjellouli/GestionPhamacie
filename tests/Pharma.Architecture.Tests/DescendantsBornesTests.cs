using Microsoft.Extensions.Logging.Abstractions;
using Pharma.Api.Synchronisation;
using Pharma.Application.Synchronisation;
using Pharma.Shared;

namespace Pharma.Architecture.Tests;

/// <summary>Voie descendante : plafonds du nombre d'éléments et de la taille des données reçues (script 136, audit F2).</summary>
public class DescendantsBornesTests
{
    private sealed class CertificatInutile : ICertificatSync
    {
        public CertificatSyncInfo? Lire() => null;
        public string NouvelleDemande() => "";
        public void Installer(string certificatPem, string autoritePem, string? epinglage) { }
        public System.Security.Cryptography.X509Certificates.X509Certificate2? CertificatClient() => null;
        public System.Security.Cryptography.X509Certificates.X509Certificate2? Autorite() => null;
    }

    private static ClientServeurCentralHttp Client() => new(new CertificatInutile(), NullLogger<ClientServeurCentralHttp>.Instance);

    [Fact]
    public void Au_dela_du_plafond_seuls_les_premiers_elements_par_version_sont_gardes()
    {
        var liste = Enumerable.Range(1, ClientServeurCentralHttp.MaxElements + 5).Reverse()
            .Select(v => new ChangementDescendantDto("reglage_central", "k" + v, v, "{}")).ToList();
        var r = Client().Descendants(new ChangementsDescendantsDto(ChangementsDescendantsDto.ContratV1, 2000, liste));
        Assert.Equal(ClientServeurCentralHttp.MaxElements, r.Changements.Count);
        Assert.Equal(1, r.Changements[0].Version);
    }

    [Fact]
    public void Un_element_trop_gros_ou_mal_forme_est_ignore_sans_arreter_les_autres()
    {
        var gros = "{\"x\":\"" + new string('a', ClientServeurCentralHttp.MaxDonnees) + "\"}";
        var liste = new List<ChangementDescendantDto>
        {
            new("reglage_central", "a", 1, gros), new("reglage_central", "b", 2, "[1]"), new("reglage_central", "c", 3, "pas du json"),
            new("", "d", 4, "{}"), new("reglage_central", "e", 5, "{\"familles\":[]}")
        };
        var r = Client().Descendants(new ChangementsDescendantsDto(ChangementsDescendantsDto.ContratV1, 5, liste));
        Assert.Equal(["e"], r.Changements.Select(c => c.Cle));
        Assert.Equal(4, r.VersionEcarteeMax);   // plus grande version écartée : acquittée avec les autres (revue du 2026-11-23)
        Assert.Equal(4, r.Ecartes);   // comptés et signalés dans la note du passage (revue du 2026-11-22), plus passés sous silence
    }

    [Fact]
    public void La_date_du_central_est_lue_meme_sans_element_et_absente_pour_un_central_anterieur()
    {
        // Décision du titulaire du 2026-11-27 (point 4) : champ facultatif DateCentrale, date plancher de l'offre
        var date = new DateTimeOffset(2027, 1, 10, 13, 0, 0, TimeSpan.FromHours(1));
        Assert.Equal(date.UtcDateTime, Client().Descendants(new ChangementsDescendantsDto(ChangementsDescendantsDto.ContratV1, 0, [], date)).DateCentraleUtc);
        Assert.Equal(DateTimeKind.Utc, Client().Descendants(new ChangementsDescendantsDto(ChangementsDescendantsDto.ContratV1, 0, [], date)).DateCentraleUtc!.Value.Kind);
        var avecElement = Client().Descendants(new ChangementsDescendantsDto(ChangementsDescendantsDto.ContratV1, 1,
            [new("reglage_central", "a", 1, "{}")], date));
        Assert.Equal((1, (DateTime?)date.UtcDateTime), (avecElement.Changements.Count, avecElement.DateCentraleUtc));
        Assert.Equal(date.UtcDateTime, Client().Descendants(new ChangementsDescendantsDto("desc-v9", 1, [new("t", "a", 1, "{}")], date)).DateCentraleUtc);
        Assert.Null(Client().Descendants(new ChangementsDescendantsDto(ChangementsDescendantsDto.ContratV1, 0, [])).DateCentraleUtc);
        Assert.Null(Client().Descendants(null).DateCentraleUtc);
        // Réponse d'un central antérieur (JSON sans le champ) : se lit comme avant
        var ancien = System.Text.Json.JsonSerializer.Deserialize<ChangementsDescendantsDto>("""{"contrat":"desc-v1","versionMax":0,"changements":[]}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Null(ancien.DateCentrale);
    }

    [Fact]
    public void Plafonds_communs_avec_le_serveur_central()
    {
        // Le central borne ses réponses avec les mêmes valeurs que celles lues par l'officine
        Assert.Equal(ChangementsDescendantsDto.MaxReponse, ClientServeurCentralHttp.MaxReponse);
        Assert.Equal(ChangementsDescendantsDto.MaxElements, ClientServeurCentralHttp.MaxElements);
        Assert.Equal(ChangementsDescendantsDto.MaxDonnees, ClientServeurCentralHttp.MaxDonnees);   // taille d'un élément : même mesure des deux côtés
        Assert.True(ChangementsDescendantsDto.OctetsDonneesParReponse < ChangementsDescendantsDto.MaxReponse);
    }
}
