using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Barre d'état (2026-11-21) : liaison avec le serveur central et avec le 2e serveur. Une pastille seulement en anomalie (rien si la
/// liaison n'est pas en place ou à jour), lien vers l'écran seulement pour qui y a droit ; route de lecture minimale ouverte à tout
/// employé connecté, sans donnée sensible.
/// </summary>
public class BarreEtatLiaisonsTests
{
    private static string Lire(params string[] chemin) => File.ReadAllText(Path.Combine([BaseJetableM4.RacineDepot(), "src", .. chemin]));

    private static string Barre() => Lire("Pharma.Desktop", "Composants", "Mise", "BarreEtat.razor");
    private static string Regles() => Lire("Pharma.Desktop", "Services", "PastillesBarre.cs");

    [Theory]
    [InlineData("Central", "admin.parametres", "admin/synchronisation", "liaison-central")]
    [InlineData("Second", "role.titulaire", "admin/second-serveur", "liaison-second")]
    public void Pastille_seulement_en_anomalie_lien_reserve_a_qui_a_le_droit(string liaison, string droit, string ecran, string cle)
    {
        var regles = Regles();
        // Seul point d'affichage : l'anomalie reçue (null quand la liaison n'est pas en place ou à jour)
        Assert.Single(Regex.Matches(regles, $@"e\.Liaisons\?\.{liaison}\b"));
        Assert.DoesNotContain("CentralActive", regles + Barre());
        Assert.DoesNotContain("SecondConfigure", regles + Barre());
        // Lien vers l'écran seulement avec le droit de l'ouvrir ; sinon texte simple (Lien null)
        Assert.Contains($"new(\"{cle}\",", regles);
        Assert.Contains($"e.Droit(\"{droit}\") ? \"{ecran}\" : null", regles);
        // L'écran visé existe bien à cette adresse
        var page = liaison == "Central" ? "AdminSynchronisation.razor" : "AdminSecondServeur.razor";
        Assert.StartsWith($"@page \"/{ecran}\"", Lire("Pharma.Desktop", "Composants", "Pages", page));
    }

    [Fact]
    public void Classes_de_la_charte_seulement_et_date_raccourcie_quand_les_pastilles_sont_regroupees()
    {
        var regles = Regles();
        Assert.Contains("private static string NiveauLiaison(AnomalieLiaisonDto a) => a.Niveau == Anormal ? Anormal : Attention;", regles);
        Assert.Contains("maintenant.ToString(courte ? \"dd/MM · HH:mm\" : \"ddd dd/MM · HH:mm\", Fr)", regles);
        Assert.Contains("@PastillesBarre.Date(maintenant, affichage.Regroupe)", Barre());
        var css = Lire("Pharma.Desktop", "wwwroot", "css", "app.css");
        Assert.Contains(".barre-etat .pastille.attention {", css);
        Assert.Contains(".barre-etat .pastille.anormal {", css);
        Assert.Contains("white-space: nowrap", css[css.IndexOf(".barre-etat {", StringComparison.Ordinal)..][..400]);
    }

    [Fact]
    public void Lue_au_meme_rythme_que_la_caisse_et_la_sauvegarde()
    {
        var barre = Barre();
        var complet = barre[barre.IndexOf("if (complet && serveurJoignable == true && Session.EstConnecte)", StringComparison.Ordinal)..];
        complet = complet[..complet.IndexOf("\n        }", StringComparison.Ordinal)];
        Assert.Contains("Api.EtatLiaisonsPosteAsync()", complet);
        Assert.Contains("Api.CompteursBarreAsync()", complet);
        Assert.Single(Regex.Matches(barre, @"Api\.EtatLiaisonsPosteAsync\(\)"));
        Assert.Single(Regex.Matches(barre, @"Api\.CompteursBarreAsync\(\)"));
        Assert.Contains("(etat, attenteSync, liaisons, compteurs) = (null, null, null, null)", barre);
        Assert.Contains("\"api/synchronisation/etat-poste\"", Lire("Pharma.Desktop", "Services", "ApiClient.cs"));
        Assert.Contains("\"api/etat-poste/compteurs\"", Lire("Pharma.Desktop", "Services", "ApiClient.cs"));
    }

    [Fact]
    public void Route_ouverte_a_tout_employe_connecte_sans_droit_d_administration()
    {
        var api = Lire("Pharma.Api", "Endpoints", "SynchronisationEndpoints.cs");
        var debut = api.IndexOf("api.MapGet(\"/synchronisation/etat-poste\"", StringComparison.Ordinal);
        Assert.True(debut > 0, "route etat-poste introuvable (doit être sur le groupe api, hors /admin)");
        var route = api[debut..];
        route = route[..(route.IndexOf("}).", StringComparison.Ordinal) + 40)];
        Assert.Contains(".ExigerConnexion();", route);
        Assert.DoesNotContain("ExigerPermission", route);
        Assert.DoesNotContain("ExigerTitulaire", route);
        Assert.Contains("ConsulterLiaisonsPoste", route);
        // Avant le groupe /admin/synchronisation (qui exige admin.parametres)
        Assert.True(debut < api.IndexOf("api.MapGroup(\"/admin/synchronisation\")", StringComparison.Ordinal));
    }

    [Fact]
    public void Reponse_sans_donnee_sensible()
    {
        var champs = typeof(Pharma.Shared.EtatLiaisonsPosteDto).GetProperties().Select(p => p.Name)
            .Concat(typeof(Pharma.Shared.AnomalieLiaisonDto).GetProperties().Select(p => p.Name)).ToList();
        Assert.Equal(["CentralActive", "Central", "LotsEnAttente", "DernierEnvoiLe", "SecondConfigure", "Second", "Libelle", "Niveau", "Detail"], champs);
        // Le détail est composé par la règle : jamais l'adresse, le code d'officine, l'empreinte ni le message d'erreur technique
        var regle = Lire("Pharma.Application", "Synchronisation", "EtatLiaisonsPoste.cs");
        var central = regle[regle.IndexOf("public static AnomalieLiaison? Central(", StringComparison.Ordinal)..regle.IndexOf("public static AnomalieLiaison? Second(", StringComparison.Ordinal)];
        foreach (var interdit in new[] { "p.Adresse}", "OfficineId", "Empreinte", "DerniereErreur", "{s.Derniere" })
            Assert.DoesNotContain(interdit, central);
    }
}
