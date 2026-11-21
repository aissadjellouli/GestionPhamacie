using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Barre d'état (maquette validée le 2026-11-21) : éléments fixes toujours présents, pastilles au centre aux classes de la charte, aucun
/// style en ligne interdit (taille, couleur, hauteur), liste « À traiter » accessible ; compteurs lus par une route ouverte à tout employé
/// connecté, filtrés par les droits, sans donnée sensible.
/// </summary>
public class BarreEtatPastillesTests
{
    private static string Lire(params string[] chemin) => File.ReadAllText(Path.Combine([BaseJetableM4.RacineDepot(), "src", .. chemin]));
    private static string Barre() => Lire("Pharma.Desktop", "Composants", "Mise", "BarreEtat.razor");

    /// <summary>Balisage du pied de la barre (&lt;footer class="barre-etat"&gt; … &lt;/footer&gt;).</summary>
    private static string Pied(string barre)
    {
        var debut = barre.IndexOf("<footer class=\"barre-etat\"", StringComparison.Ordinal);
        Assert.True(debut > 0, "pied de la barre introuvable");
        return barre[debut..(barre.IndexOf("</footer>", debut, StringComparison.Ordinal) + 9)];
    }

    [Fact]
    public void Aucun_style_en_ligne_interdit_dans_la_barre()
    {
        var pied = Pied(Barre());
        Assert.DoesNotContain("style=", pied);
        Assert.DoesNotMatch(new Regex("font-size|color:|height:"), pied);
        Assert.DoesNotContain("style=", Barre()[Barre().IndexOf("@code {", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void Elements_fixes_toujours_presents_hors_de_toute_condition()
    {
        var pied = Pied(Barre());
        // Liaison au serveur et date : au niveau du pied (4 espaces de retrait), jamais dans un @if
        Assert.Matches(new Regex("\n    <button type=\"button\" class=\"element-etat [^\n]*data-zone=\"serveur\""), pied);
        Assert.Matches(new Regex("\n    <span class=\"element-etat droite\" data-zone=\"date\" title=\"Tracodex Officine v@\\(Version\\)\">"), pied);
        Assert.Contains("\"Connecté\"", pied);
        Assert.Contains("\"Serveur injoignable\"", pied);
        // Poste et vendeur : affiché dès qu'il y a un nom (poste déclaré ou vendeur connecté)
        Assert.Contains("@if (Identite is { Length: > 0 } identite)", pied);
        Assert.Contains("data-zone=\"identite\"", pied);
        Assert.Contains("PastillesBarre.Identite(Configuration.NomCourt, Session.EstConnecte ? Session.NomComplet : null)", Barre());
        // Version : dans l'infobulle seulement
        Assert.DoesNotMatch(new Regex(">v@\\(Version\\)"), pied);
    }

    [Fact]
    public void Pastilles_aux_classes_de_la_charte_lien_seulement_avec_un_ecran()
    {
        var barre = Barre();
        var rendu = barre[barre.IndexOf("private RenderFragment Rendu(Pastille p)", StringComparison.Ordinal)..];
        rendu = rendu[..rendu.IndexOf("</text>;", StringComparison.Ordinal)];
        Assert.Contains("@if (p.Lien is { } lien)", rendu);
        Assert.Contains("<button type=\"button\" class=\"pastille @p.Niveau\"", rendu);
        Assert.Contains("<span class=\"pastille @p.Niveau\"", rendu);
        Assert.Single(Regex.Matches(rendu, "@onclick"));
        // Niveaux permis : null (gris), attention, anormal
        var regles = Lire("Pharma.Desktop", "Services", "PastillesBarre.cs");
        Assert.Contains("public const string Attention = \"attention\";", regles);
        Assert.Contains("public const string Anormal = \"anormal\";", regles);
        Assert.Contains("a.Niveau == Anormal ? Anormal : Attention", regles);

        var css = Lire("Pharma.Desktop", "wwwroot", "css", "app.css");
        var bloc = css[css.IndexOf(".barre-etat .pastilles-etat", StringComparison.Ordinal)..];
        bloc = bloc[..bloc.IndexOf(".voyant {", StringComparison.Ordinal)];
        // Jetons de la charte (clair et sombre) : aucune couleur écrite en dur sur les pastilles, hors l'ombre de la liste
        Assert.Contains("background: var(--attention-fond); color: var(--attention);", bloc);
        Assert.Contains("background: var(--urgent-fond); color: var(--urgent);", bloc);
        Assert.DoesNotMatch(new Regex("#[0-9A-Fa-f]{3,6}"), bloc);
    }

    [Fact]
    public void Liste_a_traiter_accessible_fermee_par_echap_et_clic_dehors()
    {
        var pied = Pied(Barre());
        Assert.Contains("@if (affichage.Regroupe)", pied);
        Assert.Contains("aria-haspopup=\"dialog\"", pied);
        Assert.Contains("aria-controls=\"liste-a-traiter\"", pied);
        Assert.Contains("aria-expanded=\"@(listeOuverte ? \"true\" : \"false\")\"", pied);
        Assert.Contains("role=\"dialog\" aria-label=\"À traiter\"", pied);
        Assert.Contains("À traiter (@affichage.ATraiter.Count) ▾", pied);
        Assert.Contains("class=\"fond-liste-etat\" aria-hidden=\"true\" @onclick=\"FermerListe\"", pied);
        Assert.Contains("@onkeydown=\"ToucheGroupeAsync\"", pied);
        Assert.Contains("if (k.Key != \"Escape\" || !listeOuverte) return;", Barre());
    }

    [Fact]
    public void Route_des_compteurs_ouverte_a_tout_employe_connecte_et_filtree_par_les_droits()
    {
        var api = Lire("Pharma.Api", "Endpoints", "EtatPosteEndpoints.cs");
        var debut = api.IndexOf("api.MapGet(\"/etat-poste/compteurs\"", StringComparison.Ordinal);
        Assert.True(debut > 0, "route des compteurs introuvable");
        var route = api[debut..];
        route = route[..(route.IndexOf(".ExigerConnexion();", StringComparison.Ordinal) + 19)];
        Assert.EndsWith(".ExigerConnexion();", route);
        Assert.DoesNotContain("ExigerPermission", route);
        Assert.DoesNotContain("ExigerTitulaire", route);
        Assert.Contains("consulter.ExecuterAsync(user.UtilisateurId(), requete.Poste(), ct)", route);
        // Filtre par droits côté serveur (règle testée dans Pharma.Application.Tests.CompteursBarreTests)
        var cas = Lire("Pharma.Application", "Administration", "CompteursBarre.cs");
        Assert.Contains("return RegleCompteursBarre.Filtrer(", cas);
        Assert.Contains("public static readonly TimeSpan Duree = TimeSpan.FromSeconds(30);", cas);
    }

    [Fact]
    public void Reponse_des_compteurs_sans_donnee_sensible()
    {
        var props = typeof(Pharma.Shared.CompteursBarreDto).GetProperties();
        Assert.Equal(["VentesEnAttente", "ProduitsDisponibles", "VoletsChifa", "ChifaADeposer", "TraitesAPayer", "JournauxEnAnomalie"],
            props.Select(p => p.Name).ToArray());
        Assert.All(props, p => Assert.True(p.PropertyType == typeof(int) || p.PropertyType == typeof(bool), p.Name));
    }

    [Fact]
    public void Imprimante_de_tickets_etat_garde_sans_le_ticket_d_essai()
    {
        var imprimante = Lire("Pharma.Desktop", "Services", "ImprimanteTickets.cs");
        Assert.Contains("public string? DerniereErreur { get; private set; }", imprimante);
        Assert.Contains("public Task<string?> ImprimerEssaiAsync(string? pharmacie) => ImprimerFileAsync(", imprimante);
        Assert.Contains("Tickets.DerniereErreur", Barre());
        Assert.Contains("Tickets.EtatChange += SurImprimante;", Barre());
        Assert.Contains("Tickets.EtatChange -= SurImprimante;", Barre());
    }
}
