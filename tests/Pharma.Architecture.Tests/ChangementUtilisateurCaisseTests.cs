namespace Pharma.Architecture.Tests;

/// <summary>
/// « Changer d'utilisateur » avec une vente en cours à la Caisse (2026-11-21, docs/regles-metier.md) : la vente est mise en
/// attente sur le serveur avant la fermeture de la session, réponse attendue ; si elle échoue (serveur injoignable), rien n'est
/// fermé, le panier reste affiché et une fenêtre bloquante propose « Réessayer » ou « Rester sur la vente ».
/// Fiche d'un prêt (Financements) tenue sans défilement de la page : tuiles compactes, parties longues en cadre défilant.
/// </summary>
public class ChangementUtilisateurCaisseTests
{
    private static string Lire(params string[] chemin) => File.ReadAllText(Path.Combine([RacineDepot(), "src", .. chemin]));

    private static string Methode(string source, string signature)
    {
        var i = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, $"Introuvable : {signature}");
        var fin = source.IndexOf("\n    }", i, StringComparison.Ordinal);
        Assert.True(fin > i, $"Fin introuvable : {signature}");
        return source[i..fin];
    }

    [Fact]
    public void La_session_ne_se_ferme_qu_apres_la_mise_en_attente_reussie()
    {
        var page = Lire("Pharma.Desktop", "Composants", "Mise", "MiseEnPage.razor");
        var confirmer = Methode(page, "private async Task ConfirmerChangementAsync()");
        var preparer = confirmer.IndexOf("await Session.PreparerChangementUtilisateurAsync()", StringComparison.Ordinal);
        var arret = confirmer.IndexOf("if (echecChangement is not null) return;", StringComparison.Ordinal);
        var fermer = confirmer.IndexOf("Session.Fermer(", StringComparison.Ordinal);
        Assert.True(preparer > 0 && arret > preparer && fermer > arret, "Ordre : mise en attente attendue, arrêt si échec, puis fermeture");
        // Plus aucune fermeture directe sans la préparation
        Assert.DoesNotContain("private void ConfirmerChangement()", page);
        Assert.Contains("@onclick=\"ConfirmerChangementAsync\">Changer sans relève", page);
    }

    [Fact]
    public void L_echec_ouvre_une_fenetre_bloquante_reessayer_ou_rester()
    {
        var page = Lire("Pharma.Desktop", "Composants", "Mise", "MiseEnPage.razor");
        var debut = page.IndexOf("@if (echecChangement is not null)", StringComparison.Ordinal);
        Assert.True(debut > 0, "Fenêtre d'échec du changement d'utilisateur absente");
        var fenetre = page[debut..page.IndexOf("@* Menu ouvert", debut, StringComparison.Ordinal)];
        Assert.Contains("class=\"voile\" role=\"alertdialog\" aria-modal=\"true\"", fenetre);
        Assert.Contains("@echecChangement", fenetre);
        Assert.Contains("@onclick=\"ConfirmerChangementAsync\"", fenetre);
        Assert.Contains("\"Réessayer\"", fenetre);
        Assert.Contains("@onclick=\"ResterSurLaVente\">Rester sur la vente</button>", fenetre);
        // Charte : pas de style en ligne de taille, de couleur ni de hauteur
        Assert.DoesNotContain("style=", fenetre);
    }

    [Fact]
    public void La_caisse_met_la_vente_en_attente_et_attend_la_reponse_avant_le_changement()
    {
        var caisse = Lire("Pharma.Desktop", "Composants", "Pages", "Caisse.razor");
        Assert.Contains("Session.AvantChangementUtilisateur(LaisserAuSuivantAsync);", caisse);
        Assert.Contains("Session.OublierAvantChangement(LaisserAuSuivantAsync);", caisse);
        var laisser = Methode(caisse, "private async Task<string?> LaisserAuSuivantAsync()");
        Assert.Contains("await MettreDeCoteAsync(DemandeAttente(", laisser);
        // Échec : le message remonte à l'en-tête (la session reste ouverte) ; réussite : vente retenue pour le suivant
        var echec = laisser.IndexOf("return echec;", StringComparison.Ordinal);
        var retenue = laisser.IndexOf("Session.RetenirVenteAReprendre(id, quitte);", StringComparison.Ordinal);
        Assert.True(echec > 0 && retenue > echec, "Échec renvoyé avant que la vente soit retenue");
        Assert.Contains("if (enCours) return", laisser);
    }

    [Fact]
    public void Mise_en_attente_echouee_le_panier_reste_affiche()
    {
        // MettreDeCoteAsync ne vide le panier qu'après la réponse du serveur
        var caisse = Lire("Pharma.Desktop", "Composants", "Pages", "Caisse.razor");
        var mettre = Methode(caisse, "private async Task MettreDeCoteAsync(");
        var erreur = mettre.IndexOf("if (e is not null) { erreurAttente = e; return; }", StringComparison.Ordinal);
        var vider = mettre.IndexOf("ViderPanier();", StringComparison.Ordinal);
        Assert.True(erreur > 0 && vider > erreur, "Le panier n'est vidé qu'après une mise en attente réussie");
    }

    [Fact]
    public void La_session_stoppe_au_premier_echec_des_preparations()
    {
        var session = Lire("Pharma.Desktop", "Services", "ApiClient.cs");
        var preparer = Methode(session, "public async Task<string?> PreparerChangementUtilisateurAsync()");
        Assert.Contains("if (await preparation() is { } echec) return echec;", preparer);
        Assert.Contains("return null;", preparer);
    }

    [Fact]
    public void Fiche_du_pret_sans_defilement_de_la_page()
    {
        var fiche = Lire("Pharma.Desktop", "Composants", "Financements", "FichePret.razor");
        Assert.Contains("<div class=\"tuiles tuiles-compactes tuiles-pret\">", fiche);
        Assert.DoesNotContain("display: none", fiche);
        // Tableaux : 140 px acceptés ; parties longues (formulaire, emploi de l'argent, saisie de l'échéancier) en cadre défilant
        Assert.Equal(2, Compter(fiche, "<div class=\"tableau-cadre\" data-hauteur-min=\"140\">"));
        Assert.Equal(2, Compter(fiche, "<div class=\"cadre-defilant\" data-hauteur-min=\"160\">"));
        var editer = fiche[fiche.IndexOf("case \"editer\":", StringComparison.Ordinal)..fiche.IndexOf("case \"usages\":", StringComparison.Ordinal)];
        Assert.Contains("<div class=\"cadre-defilant cadre-unique\" data-hauteur-min=\"160\">", editer);
        var css = Lire("Pharma.Desktop", "wwwroot", "css", "app.css");
        Assert.Contains(".tuiles-pret { grid-template-columns: repeat(5, minmax(0, 1fr)); }", css);
    }

    private static int Compter(string texte, string motif)
    {
        var n = 0;
        for (var i = texte.IndexOf(motif, StringComparison.Ordinal); i >= 0; i = texte.IndexOf(motif, i + motif.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string RacineDepot()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } racine) return racine;
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }
}
