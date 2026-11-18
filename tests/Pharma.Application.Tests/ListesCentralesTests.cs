using System.Text.Json;
using Pharma.Application.Caisse;
using Pharma.Application.Catalogue;
using Pharma.Application.Synchronisation;
using Pharma.Domain.Ventes;
using Xunit;

namespace Pharma.Application.Tests;

/// <summary>Référentiels, lots 3 et 4 : prestations (ajouts et corrections) et indications (ajouts) envoyées par le serveur central.</summary>
public class ListesCentralesTests
{
    private static (ListesCentrales Service, TypesPrestationEnMemoire Prestations, IndicationsTests.IndicationsEnMemoire Indications, JournalEspion Journal) Monter()
    {
        var (prestations, indications, journal) = (new TypesPrestationEnMemoire(), new IndicationsTests.IndicationsEnMemoire(), new JournalEspion());
        prestations.Types[1] = new TypePrestation(1, "INJ", "Injection", 100m, 0m, true);
        prestations.Types[2] = new TypePrestation(2, "PANS", "Pansement", 200m, 19m, false);
        var droits = new DroitsFactices("catalogue.creer") { Titulaire = true };
        return (new ListesCentrales(prestations, new TypesPrestation(prestations, droits, journal), indications, new Indications(indications, droits, journal)),
            prestations, indications, journal);
    }

    private static JsonElement Donnees(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Prestations_ajoutees_ou_corrigees_valeurs_absentes_gardees_jamais_reactivees()
    {
        var m = Monter();
        var d = Donnees("""
            {"prestations":[
              {"code":"INJ","libelle":"Injection intramusculaire","remboursable":true,"tarif_reference":80},
              {"code":"PANS","libelle":"Pansement"},
              {"code":"TENSION","libelle":"Prise de tension"}]}
            """);
        var prevu = await m.Service.CalculerAsync(TypesOrdre.Prestations, d);
        Assert.Equal(["TENSION"], prevu.Ajouts);
        Assert.Equal(["INJ"], prevu.Corrections);   // PANS identique : rien
        Assert.Empty(m.Journal.Actions);

        await m.Service.AppliquerAsync(TypesOrdre.Prestations, d, 1);
        var inj = m.Prestations.Types[1];
        Assert.Equal(("Injection intramusculaire", 100m, true, (decimal?)80m), (inj.Libelle, inj.Prix, inj.Remboursable, inj.TarifReference)); // prix gardé
        Assert.False(m.Prestations.Types[2].Actif);                                                   // jamais réactivée
        Assert.Contains(m.Prestations.Types.Values, t => t.Code == "TENSION" && t.Prix == 0m && t.Actif); // sans prix : 0 DA, jamais vendue
        Assert.Contains("type_prestation_cree", m.Journal.Actions);
        Assert.True((await m.Service.CalculerAsync(TypesOrdre.Prestations, d)).Vide);
    }

    [Fact]
    public async Task Prestation_invalide_refuse_tout_avant_la_premiere_ecriture()
    {
        var m = Monter();
        var d = Donnees("""{"prestations":[{"code":"TENSION","libelle":"Tension","prix":150},{"code":"INJ","libelle":"Injection","remboursable":true}]}""");
        var e = await Assert.ThrowsAsync<OrdreInapplicableException>(() => m.Service.AppliquerAsync(TypesOrdre.Prestations, d, 1));
        Assert.StartsWith("Prestations refusées, rien n’a été changé", e.Message);
        Assert.Equal(2, m.Prestations.Types.Count);
        Assert.Empty(m.Journal.Actions);
    }

    [Fact]
    public async Task Indications_ajoutees_seulement_si_elles_manquent_meme_desactivees()
    {
        var m = Monter();
        var d = Donnees("""{"indications":["toux","Ancienne","Mal de gorge","mal de gorge"]}""");
        var prevu = await m.Service.CalculerAsync(TypesOrdre.Indications, d);
        Assert.Equal(["Mal de gorge"], prevu.Ajouts);
        await m.Service.AppliquerAsync(TypesOrdre.Indications, d, 1);
        Assert.Contains(m.Indications.Toutes, i => i.Libelle == "Mal de gorge" && i.Actif);
        Assert.Contains(m.Indications.Toutes, i => i.Libelle == "Ancienne" && !i.Actif);               // désactivée : laissée telle quelle
        Assert.Equal(4, m.Indications.Toutes.Count);
        Assert.Equal("Rien ne change : déjà à jour.", (await m.Service.CalculerAsync(TypesOrdre.Indications, d)).Texte());
    }
}
