using System.Text.Json;
using Pharma.Application.Chifa;
using Pharma.Application.Factures;
using Pharma.Application.Synchronisation;
using Xunit;

namespace Pharma.Application.Tests;

/// <summary>Référentiels, lot 2 : organismes et centres payeurs envoyés par le serveur central (ajouts et corrections seulement).</summary>
public class OrganismesCentrauxTests
{
    private static (OrganismesCentraux Service, FacturesEnMemoire Factures, CentresEnMemoire Centres, JournalEspion Journal) Monter()
    {
        var (factures, centres, journal) = (new FacturesEnMemoire(), new CentresEnMemoire(), new JournalEspion());
        factures.Organismes.Add(new OrganismeFiche(1, "CNAS", "Caisse nationale", "Alger", null, true, null, null, 30));
        factures.Organismes.Add(new OrganismeFiche(2, "CASNOS", "Caisse des non-salariés", null, null, false, null, null, 30));
        var droits = new DroitsFactices("admin.parametres", "factures.gerer") { Titulaire = true };
        return (new OrganismesCentraux(factures, new Facturation(factures, droits, new HorlogeFixe(new DateOnly(2026, 11, 18)), journal), centres,
            new CentresPayeurs(centres, droits, journal)), factures, centres, journal);
    }

    private static JsonElement Donnees(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Ajoute_et_corrige_sans_jamais_supprimer_ni_reactiver()
    {
        var m = Monter();
        var d = Donnees("""
            {"organismes":[
              {"code":"CNAS","nom":"Caisse nationale des assurances sociales","delai_paiement_jours":45,
               "centres":[{"code":"1600","nom":"Centre d'Alger"},{"code":"0900","nom":"Centre de Blida"}]},
              {"code":"CASNOS","nom":"Caisse des non-salariés"},
              {"code":"MDN","nom":"Ministère de la Défense nationale"}]}
            """);
        var prevu = await m.Service.CalculerAsync(d);
        Assert.Equal(["MDN"], prevu.Creations);
        Assert.Equal(["CNAS"], prevu.Corrections);                                       // CASNOS identique : rien
        Assert.Equal((1, 1), (prevu.CentresCrees, prevu.CentresCorriges));
        Assert.Empty(m.Journal.Actions);                                                 // l'aperçu n'écrit rien

        await m.Service.AppliquerAsync(d, 1);
        var cnas = m.Factures.Organismes.Single(o => o.Code == "CNAS");
        Assert.Equal(("Caisse nationale des assurances sociales", "Alger", 45), (cnas.Nom, cnas.Adresse, cnas.DelaiPaiementJours)); // adresse absente : gardée
        Assert.False(m.Factures.Organismes.Single(o => o.Code == "CASNOS").Actif);       // jamais réactivé
        Assert.Contains(m.Factures.Organismes, o => o.Code == "MDN" && o.Actif && o.DelaiPaiementJours == 30);
        Assert.Contains(m.Centres.Centres, c => c.OrganismeId == 1 && c.Code == "0900" && c.Actif);
        Assert.Contains(m.Centres.Centres, c => c.Id == 3 && c.Nom == "Centre d'Alger");
        Assert.Contains(m.Centres.Centres, c => c.Id == 5 && !c.Actif);                  // centre désactivé non envoyé : inchangé
        Assert.Contains("organisme_cree", m.Journal.Actions);
        Assert.Contains("centre_payeur_cree", m.Journal.Actions);

        Assert.True((await m.Service.CalculerAsync(d)).Vide);                            // deuxième passage : déjà à jour
    }

    [Fact]
    public async Task Une_valeur_invalide_refuse_tout_avant_la_premiere_ecriture()
    {
        var m = Monter();
        var d = Donnees("""{"organismes":[{"code":"MDN","nom":"Défense"},{"code":"CNAS","nom":"Caisse","telephone":"abc"}]}""");
        var e = await Assert.ThrowsAsync<OrdreInapplicableException>(() => m.Service.AppliquerAsync(d, 1));
        Assert.StartsWith("Organismes refusés, rien n’a été changé", e.Message);
        Assert.DoesNotContain(m.Factures.Organismes, o => o.Code == "MDN");
        Assert.Empty(m.Journal.Actions);
        e = await Assert.ThrowsAsync<OrdreInapplicableException>(() =>
            m.Service.CalculerAsync(Donnees("""{"organismes":[{"code":"MDN","nom":"D","centres":[{"code":"1","nom":"X"}]}]}""")));
        Assert.Contains("CNAS ou à la CASNOS", e.Message);
    }
}
