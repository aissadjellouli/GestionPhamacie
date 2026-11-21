namespace Pharma.Architecture.Tests;

/// <summary>
/// Retour d'une vente régularisée « rendu au patient » (décision du titulaire du 2026-11-21, script 159) : la retenue est annoncée dans la
/// zone des alertes du panneau de retour (au-dessus du cadre défilant), la part non couverte demande « Patient prévenu » avant
/// l'enregistrement, et la confirmation est envoyée au serveur ; la dette ajoutée s'écrit au relevé (mouvement « correction »).
/// </summary>
public class RetourRenduPatientEcranTests
{
    private static string Racine()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } racine) return racine;
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    private static string Lire(params string[] chemin) => File.ReadAllText(Path.Combine([Racine(), .. chemin]));

    private static string Panneau() => Lire("src", "Pharma.Desktop", "Composants", "Caisse", "PanneauAnnulation.razor");

    [Fact]
    public void La_retenue_est_dans_la_zone_des_alertes_au_dessus_du_cadre()
    {
        var panneau = Panneau();
        var champDette = panneau.IndexOf("private RenderFragment ChampDette", StringComparison.Ordinal);
        var finChampDette = panneau.IndexOf("private RenderFragment ChampMotif", StringComparison.Ordinal);
        Assert.True(champDette > 0 && finChampDette > champDette, "Fragment ChampDette introuvable");
        var alertes = panneau[champDette..finChampDette];
        Assert.Contains("data-champ=\"dette_retenue\"", alertes);
        Assert.Contains("data-champ=\"avoir_retenu\"", alertes);
        // Le bloc du retour affiche @ChampDette avant le cadre défilant (disposition gardée, test RetourDettesEcranTests)
        var bloc = panneau[panneau.IndexOf("else if (retour is not null)", StringComparison.Ordinal)..];
        Assert.True(bloc.IndexOf("@ChampDette", StringComparison.Ordinal) < bloc.IndexOf("cadre-defilant retour-cadre", StringComparison.Ordinal));
    }

    [Fact]
    public void L_alerte_de_dette_demande_patient_prevenu_compacte_et_sans_style_en_ligne()
    {
        var panneau = Panneau();
        var debut = panneau.IndexOf("data-champ=\"dette_retenue\"", StringComparison.Ordinal);
        var fin = panneau.IndexOf("</fieldset>", debut, StringComparison.Ordinal);
        Assert.True(debut > 0 && fin > debut, "Alerte de la retenue introuvable");
        var alerte = panneau[debut..fin];
        Assert.Contains("role=\"alert\"", panneau[(debut - 120)..debut]);
        Assert.Contains("Patient prévenu", alerte);
        Assert.Contains("Obligatoire.Classe(RetenueManque)", alerte);
        Assert.Equal(1, alerte.Split("<label").Length - 1);   // compacte : une seule case
        foreach (var interdit in new[] { "font-size", "color:", "height", "style=" })
            Assert.DoesNotContain(interdit, alerte);
        // Info sans dette : un seul bandeau compact
        var info = panneau.IndexOf("data-champ=\"avoir_retenu\"", StringComparison.Ordinal);
        Assert.Contains("info-bandeau compact", panneau[(info - 80)..info]);
    }

    [Fact]
    public void La_confirmation_est_controlee_et_envoyee_au_serveur()
    {
        var panneau = Panneau();
        Assert.Contains(": RetenueManque ?", panneau);
        Assert.Contains("DetteRetenueEstimee > 0 && detteRetenue));", panneau);
        var api = Lire("src", "Pharma.Api", "Endpoints", "CaisseEndpoints.cs");
        Assert.Contains("req.Dettes, req.DetteRetenue)", api);
    }

    [Fact]
    public void La_dette_ajoutee_s_ecrit_au_releve_rattachee_a_la_vente()
    {
        var sql = Lire("src", "Pharma.Infrastructure", "Caisse", "RetoursClientSql.cs");
        var i = sql.IndexOf("if (a.DetteAvoirRetenu > 0)\n", StringComparison.Ordinal) is var n and >= 0 ? n
            : sql.IndexOf("if (a.DetteAvoirRetenu > 0)\r\n", StringComparison.Ordinal);
        Assert.True(i > 0, "Écriture de la dette ajoutée introuvable");
        var bloc = sql[i..sql.IndexOf("await tx.CommitAsync", i, StringComparison.Ordinal)];
        Assert.Contains("ReleveCredit.Correction, a.VenteId", bloc);
        Assert.Contains("solde_credit = solde_credit + $2", bloc);
        Assert.Contains("montant_avoir_retenu", sql);
        Assert.Contains("dette_avoir_retenu", sql);
    }

    [Fact]
    public void Script_159_et_son_test_dans_les_deux_solutions()
    {
        foreach (var solution in new[] { "PharmaOfficine.sln", "PharmaOfficine.slnx" })
        {
            var texte = Lire(solution).Replace('\\', '/');
            Assert.Contains("database/migration/159_retour_regularisation_rendu_patient.sql", texte);
            Assert.Contains("database/tests/159_retour_regularisation_rendu_patient_tests.sql", texte);
        }
    }
}
