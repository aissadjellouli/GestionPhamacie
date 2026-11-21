namespace Pharma.Architecture.Tests;

/// <summary>
/// Retour et dettes du patient (décision du titulaire du 2026-11-21, script 158) : alerte bloquante dans le panneau de retour (nom du
/// patient cliquable, total de ses dettes, « Déduire de ses dettes » recommandé, « Rendre quand même »), choix transmis au serveur ;
/// l'imputation sur ses dettes s'écrit comme « Encaisser une dette » (relevé « remboursement », credit_imputation), sans tiroir.
/// </summary>
public class RetourDettesEcranTests
{
    private static string Lire(params string[] chemin) => File.ReadAllText(Path.Combine([RacineDepot(), "src", .. chemin]));

    private static string RacineDepot()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } racine) return racine;
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    /// <summary>Bloc de l'alerte des dettes du patient au retour (du commentaire de décision au fieldset fermé).</summary>
    private static string Alerte(string panneau)
    {
        var debut = panneau.IndexOf("data-champ=\"dettes_patient\"", StringComparison.Ordinal);
        Assert.True(debut > 0, "Alerte des dettes du patient introuvable dans le panneau de retour");
        var fin = panneau.IndexOf("</fieldset>", debut, StringComparison.Ordinal);
        Assert.True(fin > debut, "Fin de l'alerte introuvable");
        return panneau[debut..fin];
    }

    [Fact]
    public void L_alerte_des_dettes_nomme_le_patient_et_propose_les_deux_choix()
    {
        var alerte = Alerte(Lire("Pharma.Desktop", "Composants", "Caisse", "PanneauAnnulation.razor"));
        Assert.Contains("<LienContact Type=\"patient\"", alerte);
        Assert.Contains("Déduire de ses dettes (recommandé)", alerte);
        Assert.Contains("Rendre quand même", alerte);
        Assert.Contains("data-champ=\"accord_client\"", alerte);
        // Charte : aucune taille, couleur ou hauteur en ligne
        foreach (var interdit in new[] { "font-size", "color:", "height" })
            Assert.DoesNotContain(interdit, alerte);
    }

    [Fact]
    public void Retour_ecran_fixe_dettes_en_haut_produits_dans_un_cadre_bouton_dessous()
    {
        var panneau = Lire("Pharma.Desktop", "Composants", "Caisse", "PanneauAnnulation.razor");
        var debut = panneau.IndexOf("else if (retour is not null)", StringComparison.Ordinal);
        Assert.True(debut > 0, "Bloc du retour introuvable");
        var bloc = panneau[debut..panneau.IndexOf("EnregistrerRetourAsync\">", debut, StringComparison.Ordinal)];
        var dette = bloc.IndexOf("@ChampDette", StringComparison.Ordinal);
        var validation = bloc.IndexOf("@ChampValidation", StringComparison.Ordinal);
        var cadre = bloc.IndexOf("<div class=\"cadre-defilant retour-cadre\"", StringComparison.Ordinal);
        var lignes = bloc.IndexOf("@foreach (var s in saisies)", StringComparison.Ordinal);
        var motif = bloc.IndexOf("@ChampMotif", StringComparison.Ordinal);
        Assert.True(dette > 0 && validation > dette && cadre > validation, "Dettes et validation au-dessus du cadre défilant");
        Assert.True(lignes > cadre && motif > lignes, "Produits puis motif dans le cadre défilant");
        Assert.Equal(1, bloc.Split("cadre-defilant").Length - 1);   // un seul cadre : pas de défilement imbriqué
        // Le bouton suit le cadre fermé (hors du cadre, toujours visible)
        Assert.EndsWith("</div>\n                    <button type=\"button\" class=\"btn btn-danger\" disabled=\"@(enCours || ARendre is null || DetteInconnue)\" @onclick=\"",
            bloc.Replace("\r\n", "\n"));
        foreach (var interdit in new[] { "font-size", "color:", "height:" })
            Assert.DoesNotContain(interdit, bloc);
    }

    [Fact]
    public void Deduire_est_choisi_par_defaut_et_le_choix_est_envoye_au_serveur()
    {
        var panneau = Lire("Pharma.Desktop", "Composants", "Caisse", "PanneauAnnulation.razor");
        Assert.Contains("RetourPatientConnu ? solderDette != false", panneau);
        Assert.Contains("(DeduireChoisi ? \"deduire\" : \"rendre\")", panneau);
    }

    [Fact]
    public void L_imputation_s_ecrit_comme_un_encaissement_de_dette_sans_tiroir()
    {
        var sql = Lire("Pharma.Infrastructure", "Caisse", "RetoursClientSql.cs");
        var i = sql.IndexOf("private static async Task ImputerSurDettesAsync", StringComparison.Ordinal);
        Assert.True(i > 0, "ImputerSurDettesAsync introuvable");
        var imputer = sql[i..];
        Assert.Contains("ReleveCredit.Remboursement", imputer);
        Assert.Contains("ReleveCredit.ImputerAsync", imputer);
        Assert.Contains("RegleReglementCredit.Imputer", imputer);
        Assert.Contains("FOR UPDATE", imputer);
        Assert.DoesNotContain("Tiroir", sql);
        Assert.DoesNotContain("INSERT INTO paiement", sql);
    }
}
