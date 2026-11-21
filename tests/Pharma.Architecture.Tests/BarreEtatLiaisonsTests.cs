using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Barre d'état (2026-11-21) : liaison avec le serveur central et avec le 2e serveur. Un élément seulement en anomalie (rien si la
/// liaison n'est pas en place ou à jour), lien vers l'écran seulement pour qui y a droit, aucun style en ligne ; route de lecture
/// minimale ouverte à tout employé connecté, sans donnée sensible.
/// </summary>
public class BarreEtatLiaisonsTests
{
    private static string Lire(params string[] chemin) => File.ReadAllText(Path.Combine([BaseJetableM4.RacineDepot(), "src", .. chemin]));

    private static string Barre() => Lire("Pharma.Desktop", "Composants", "Mise", "BarreEtat.razor");

    /// <summary>Bloc @if (liaisons?.X is { } y) { … } de la barre (jusqu'à l'accolade fermante au même retrait).</summary>
    private static string Bloc(string barre, string liaison)
    {
        var m = Regex.Match(barre, $@"\n(?<retrait> *)@if \(liaisons\?\.{liaison} is \{{ \}} \w+\)\s*\n\k<retrait>\{{(?<corps>.*?)\n\k<retrait>\}}", RegexOptions.Singleline);
        Assert.True(m.Success, $"bloc de la liaison {liaison} introuvable");
        return m.Groups["corps"].Value;
    }

    [Theory]
    [InlineData("Central", "admin.parametres", "admin/synchronisation", "liaison-central")]
    [InlineData("Second", "role.titulaire", "admin/second-serveur", "liaison-second")]
    public void Element_seulement_en_anomalie_lien_reserve_a_qui_a_le_droit_sans_style_en_ligne(string liaison, string droit, string ecran, string zone)
    {
        var barre = Barre();
        var bloc = Bloc(barre, liaison);

        // Seul point d'affichage : l'anomalie reçue (null quand la liaison n'est pas en place ou à jour)
        Assert.Single(Regex.Matches(barre, $@"liaisons\?\.{liaison}\b"));
        Assert.DoesNotContain("CentralActive", barre);
        Assert.DoesNotContain("SecondConfigure", barre);

        // Lien vers l'écran seulement avec le droit de l'ouvrir ; sinon texte simple sans clic
        var avecDroit = $"@if (Session.A(\"{droit}\"))";
        Assert.Contains(avecDroit, bloc);
        var (lien, texte) = (bloc[..bloc.IndexOf("else", StringComparison.Ordinal)], bloc[bloc.IndexOf("else", StringComparison.Ordinal)..]);
        Assert.Contains("<button", lien);
        Assert.Contains($"Nav.NavigateTo(\"{ecran}\")", lien);
        Assert.Contains("<span", texte);
        Assert.DoesNotContain("@onclick", texte);
        Assert.DoesNotContain("<button", texte);
        Assert.Equal(2, Regex.Matches(bloc, $"data-zone=\"{zone}\"").Count);
        Assert.Equal(2, Regex.Matches(bloc, "class=\"element-etat @Classe\\(").Count);
        Assert.DoesNotContain("style=", bloc);

        // L'écran visé existe bien à cette adresse
        var page = liaison == "Central" ? "AdminSynchronisation.razor" : "AdminSecondServeur.razor";
        Assert.StartsWith($"@page \"/{ecran}\"", Lire("Pharma.Desktop", "Composants", "Pages", page));
    }

    [Fact]
    public void Classes_de_la_charte_seulement_et_date_raccourcie_avec_un_element()
    {
        var barre = Barre();
        Assert.Contains("private static string Classe(AnomalieLiaisonDto a) => a.Niveau == \"anormal\" ? \"anormal\" : \"attention\";", barre);
        Assert.Contains("{ Central: not null, Second: not null } => \"HH:mm\"", barre);
        Assert.Contains("{ Central: not null } or { Second: not null } => \"dd/MM · HH:mm\"", barre);
        Assert.Contains("@maintenant.ToString(FormatDate, Fr)", barre);
        var css = Lire("Pharma.Desktop", "wwwroot", "css", "app.css");
        Assert.Contains(".element-etat.attention {", css);
        Assert.Contains(".element-etat.anormal {", css);
        Assert.Contains("white-space: nowrap", css[css.IndexOf(".barre-etat {", StringComparison.Ordinal)..][..400]);
    }

    [Fact]
    public void Lue_au_meme_rythme_que_la_caisse_et_la_sauvegarde()
    {
        var barre = Barre();
        var complet = barre[barre.IndexOf("if (complet && serveurJoignable == true && Session.EstConnecte)", StringComparison.Ordinal)..];
        complet = complet[..complet.IndexOf("\n        }", StringComparison.Ordinal)];
        Assert.Contains("Api.EtatLiaisonsPosteAsync()", complet);
        Assert.Single(Regex.Matches(barre, @"Api\.EtatLiaisonsPosteAsync\(\)"));
        Assert.Contains("(etat, attenteSync, liaisons) = (null, null, null)", barre);
        Assert.Contains("\"api/synchronisation/etat-poste\"", Lire("Pharma.Desktop", "Services", "ApiClient.cs"));
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
