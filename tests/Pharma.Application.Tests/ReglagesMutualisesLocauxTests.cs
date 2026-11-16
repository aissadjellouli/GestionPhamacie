using Pharma.Application.Reglages;
using Pharma.Domain.Commun;
using Pharma.Domain.Reglages;
using Pharma.Shared;

namespace Pharma.Application.Tests;

// ---- Mutualisation des réglages, lot M5 : lecture de l'état, écarts, réglage fiscal et traçabilité (docs/conception-synchronisation.md § 16 bis.5) ----

internal sealed class LectureLocaleFactice(Dictionary<string, string> valeurs) : ILectureValeurReglage
{
    public Task<string> ValeurLocaleAsync(string table, string valeurModeleJson, CancellationToken ct = default) =>
        Task.FromResult(valeurs.GetValueOrDefault(table, "{}"));
}

internal sealed class ParametresFiscalTracabiliteEnMemoire : IParametresFiscalTracabilite
{
    public ParametreFiscal Fiscal { get; set; } = new(6, "csv-v1", ";", "Africa/Algiers");
    public ParametreTracabilite Tracabilite { get; set; } = new(false, "ordonnance");
    public int Ecritures { get; private set; }
    public Task<ParametreFiscal> FiscalAsync(CancellationToken ct = default) => Task.FromResult(Fiscal);
    public Task EnregistrerFiscalAsync(ParametreFiscal p, CancellationToken ct = default) { Fiscal = p; Ecritures++; return Task.CompletedTask; }
    public Task<ParametreTracabilite> TracabiliteAsync(CancellationToken ct = default) => Task.FromResult(Tracabilite);
    public Task EnregistrerTracabiliteAsync(ParametreTracabilite p, CancellationToken ct = default) { Tracabilite = p; Ecritures++; return Task.CompletedTask; }
    public Task<bool> FuseauConnuAsync(string fuseau, CancellationToken ct = default) => Task.FromResult(fuseau is "Africa/Algiers" or "Europe/Paris");
}

public class ReglagesMutualisesLocauxTests
{
    private const string Inventaire = TablesMutualisables.Inventaire;

    private static string Modele(string valeurJson) =>
        "{\"auteur\":\"admin-central-1\",\"format\":\"mutu-v1\",\"mode\":\"propose\",\"valeur\":" + valeurJson + ",\"version_modele\":3}";

    private static DepotReglagesEnMemoire DepotPropose(string table, string valeurModele, string locale, string etat = "non_adopte", string mode = "propose")
    {
        var d = new DepotReglagesEnMemoire();
        d.Etats[table] = d.Etats[table] with
        {
            Mode = mode, ModeRecu = "propose", VersionRecue = 3, ModeleJson = Modele(valeurModele), Etat = etat, Origine = "locale"
        };
        d.Locales[table] = locale;
        return d;
    }

    // ---- Lecture de l'état ----

    [Fact]
    public async Task L_etat_des_11_tables_est_renvoye_dans_l_ordre_de_la_liste_fermee()
    {
        var d = new DepotReglagesEnMemoire();
        var vues = await new ConsulterReglagesMutualises(d, new LectureLocaleFactice([])).ListerAsync();
        Assert.Equal(TablesMutualisables.Toutes, vues.Select(v => v.Etat.Table));
        Assert.All(vues, v => { Assert.Equal("local", v.ModeEnVigueur); Assert.Empty(v.Ecarts); });
    }

    [Fact]
    public async Task Une_valeur_proposee_non_adoptee_montre_la_valeur_du_central_a_cote_de_la_valeur_locale()
    {
        var d = DepotPropose(Inventaire, "{\"seuil_boites\":5,\"seuil_montant\":2000}", "{\"seuil_boites\":2,\"seuil_montant\":2000}");
        var v = await new ConsulterReglagesMutualises(d, new LectureLocaleFactice(d.Locales)).LireAsync(Inventaire);
        var e = Assert.Single(v.Ecarts);                                      // le montant est identique : pas d'écart
        Assert.Equal(("Seuil en boîtes", "2", "5"), (e.Libelle, e.Local, e.Central));
        Assert.Equal("admin-central-1", v.Auteur);
        Assert.Equal(("propose", "non_adopte", 3L), (v.ModeEnVigueur, v.Etat.Etat, v.Etat.VersionRecue));
    }

    [Fact]
    public async Task Sans_synchronisation_le_mode_est_local_et_aucun_ecart_n_est_calcule()
    {
        var d = DepotPropose(Inventaire, "{\"seuil_boites\":5,\"seuil_montant\":2000}", "{\"seuil_boites\":2,\"seuil_montant\":2000}");
        d.Possible = false;                                                    // synchronisation désactivée : mode effectif « local »
        var v = await new ConsulterReglagesMutualises(d, new LectureLocaleFactice(d.Locales)).LireAsync(Inventaire);
        Assert.Equal("local", v.ModeEnVigueur);
        Assert.Empty(v.Ecarts);
    }

    [Fact]
    public async Task Une_table_rejetee_garde_son_motif_et_ne_calcule_pas_d_ecart()
    {
        var d = new DepotReglagesEnMemoire();
        d.Etats[Inventaire] = d.Etats[Inventaire] with { Etat = "rejete", MotifRejet = "Seuil en boîtes : valeur négative.", VersionRejetee = 4 };
        var v = await new ConsulterReglagesMutualises(d, new LectureLocaleFactice([])).LireAsync(Inventaire);
        Assert.Equal(("rejete", "Seuil en boîtes : valeur négative."), (v.Etat.Etat, v.Etat.MotifRejet));
        Assert.Empty(v.Ecarts);
    }

    [Fact]
    public async Task Une_table_inconnue_est_refusee()
    {
        var cas = new ConsulterReglagesMutualises(new DepotReglagesEnMemoire(), new LectureLocaleFactice([]));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.LireAsync("utilisateur"));
    }

    // ---- Comparaison ----

    [Fact]
    public void Les_motifs_et_les_remises_sont_compares_element_par_element()
    {
        var local = "{\"motifs\":[{\"liste\":\"tiroir\",\"code\":\"a\",\"libelle\":\"Monnaie\",\"actif\":true,\"ordre\":1,\"effet\":\"aucun\",\"detail\":\"aucun\"}]}";
        var modele = "{\"motifs\":[{\"liste\":\"tiroir\",\"code\":\"a\",\"libelle\":\"Monnaie\",\"actif\":false,\"ordre\":1,\"effet\":\"aucun\",\"detail\":\"aucun\"},"
                     + "{\"liste\":\"tiroir\",\"code\":\"c_x\",\"libelle\":\"Nouveau\",\"actif\":true,\"ordre\":2,\"effet\":\"aucun\",\"detail\":\"aucun\"}]}";
        var ecarts = ComparaisonReglages.Comparer(TablesMutualisables.Motifs, local, modele);
        Assert.Equal(2, ecarts.Count);
        Assert.Contains(ecarts, e => e.Local.Contains("actif") && e.Central.Contains("inactif"));
        Assert.Contains(ecarts, e => e.Libelle.Contains("Nouveau") && e.Local == ComparaisonReglages.Absent);

        var ecartsRemises = ComparaisonReglages.Comparer(TablesMutualisables.Remises, "{\"remises\":[]}",
            "{\"remises\":[{\"cle\":\"c_1\",\"categorie\":\"dermo\",\"pourcentage\":10,\"date_debut\":\"2026-12-01\",\"date_fin\":\"2026-12-31\",\"motif\":null}]}");
        var r = Assert.Single(ecartsRemises);
        Assert.Equal("10 % du 2026-12-01 au 2026-12-31", r.Central);
    }

    [Fact]
    public void Les_regles_des_psychotropes_sont_comparees_par_code_et_par_champ()
    {
        static string Regle(int seuil) => "{\"regles\":[{\"code\":\"patient_repete\",\"active\":true,\"seuil\":" + seuil
                                          + ",\"periode_jours\":30,\"exiger_valideur\":false,\"unites_par_jour\":null}]}";
        var e = Assert.Single(ComparaisonReglages.Comparer(TablesMutualisables.AlertesPsychotropes, Regle(3), Regle(5)));
        Assert.EndsWith("Seuil", e.Libelle);
        Assert.Equal(("3", "5"), (e.Local, e.Central));
    }

    [Fact]
    public void L_entete_d_etat_fait_l_aller_retour_et_ignore_un_texte_illisible()
    {
        var e = new EtatReglageEntete("parametre_alerte", "impose", "applique", 4, false);
        var lu = EtatReglageEntete.Lire(e.Formater());
        Assert.Equal(e, lu);
        Assert.True(lu!.Impose);
        Assert.True(EtatReglageEntete.Lire("table=x; mode=propose; etat=non_adopte; version=2; ajouts=1")!.ANonAdopte);
        Assert.Null(EtatReglageEntete.Lire(null));
        Assert.Null(EtatReglageEntete.Lire("texte sans forme"));
    }

    // ---- Réglage fiscal et traçabilité (D8) ----

    private static (ModifierParametreFiscal Fiscal, ModifierParametreTracabilite Tracabilite, ParametresFiscalTracabiliteEnMemoire Depot, JournalEspion Journal, DepotReglagesEnMemoire Reglages)
        Fabrique(bool titulaire = true)
    {
        var (depot, journal, reglages) = (new ParametresFiscalTracabiliteEnMemoire(), new JournalEspion(), new DepotReglagesEnMemoire());
        var droits = new DroitsFactices { Titulaire = titulaire };
        var garde = new GardeReglagesCentraux(reglages);
        return (new ModifierParametreFiscal(depot, droits, journal, garde), new ModifierParametreTracabilite(depot, droits, journal, garde), depot, journal, reglages);
    }

    [Fact]
    public async Task Le_reglage_fiscal_est_enregistre_et_journalise_avant_apres()
    {
        var (fiscal, _, depot, journal, _) = Fabrique();
        await fiscal.ExecuterAsync(new ParametreFiscal(10, "csv-v1", ",", " Europe/Paris "), 1);
        Assert.Equal(new ParametreFiscal(10, "csv-v1", ",", "Europe/Paris"), depot.Fiscal);
        Assert.Equal("parametre_fiscal", Assert.Single(journal.Actions));
        var d = journal.Details[0];
        Assert.Contains("conservation_annees = 6", d["avant"]!.ToString());
        Assert.Contains("conservation_annees = 10", d["apres"]!.ToString());
    }

    [Theory]
    [InlineData(5, "csv-v1", ";", "Africa/Algiers", "conservation_annees")]
    [InlineData(31, "csv-v1", ";", "Africa/Algiers", "conservation_annees")]
    [InlineData(6, "xml", ";", "Africa/Algiers", "format_export_dgi")]
    [InlineData(6, "csv-v1", "|", "Africa/Algiers", "separateur_csv")]
    [InlineData(6, "csv-v1", ";", "", "fuseau")]
    [InlineData(6, "csv-v1", ";", "Mars/Olympus", "fuseau")]
    public async Task Le_reglage_fiscal_refuse_une_valeur_invalide_en_designant_le_champ(int annees, string format, string separateur, string fuseau, string champ)
    {
        var (fiscal, _, depot, _, _) = Fabrique();
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => fiscal.ExecuterAsync(new ParametreFiscal(annees, format, separateur, fuseau), 1));
        Assert.Equal(champ, e.Champ);
        Assert.Equal(0, depot.Ecritures);
    }

    [Fact]
    public async Task Le_reglage_fiscal_est_reserve_au_titulaire()
    {
        var (fiscal, tracabilite, depot, _, _) = Fabrique(titulaire: false);
        await Assert.ThrowsAsync<Pharma.Application.Commun.DroitRefuseException>(() => fiscal.ExecuterAsync(new ParametreFiscal(10, "csv-v1", ";", "Africa/Algiers"), 1));
        await Assert.ThrowsAsync<Pharma.Application.Commun.DroitRefuseException>(() => tracabilite.ExecuterAsync(new ParametreTracabilite(true, "toujours"), 1));
        Assert.Equal(0, depot.Ecritures);
    }

    [Fact]
    public async Task Une_table_imposee_refuse_la_saisie_fiscale_et_de_tracabilite_en_409()
    {
        var (fiscal, tracabilite, depot, journal, reglages) = Fabrique();
        foreach (var t in new[] { TablesMutualisables.Fiscal, TablesMutualisables.Tracabilite })
            reglages.Etats[t] = reglages.Etats[t] with { Mode = "impose" };
        var e = await Assert.ThrowsAsync<ReglageImposeException>(() => fiscal.ExecuterAsync(new ParametreFiscal(10, "csv-v1", ";", "Africa/Algiers"), 1));
        Assert.Equal(TablesMutualisables.Fiscal, e.Table);
        await Assert.ThrowsAsync<ReglageImposeException>(() => tracabilite.ExecuterAsync(new ParametreTracabilite(true, "toujours"), 1));
        Assert.Equal(0, depot.Ecritures);
        Assert.Empty(journal.Actions);
    }

    [Fact]
    public async Task La_tracabilite_est_enregistree_et_refuse_une_valeur_inconnue()
    {
        var (_, tracabilite, depot, journal, _) = Fabrique();
        await tracabilite.ExecuterAsync(new ParametreTracabilite(true, "toujours"), 1);
        Assert.Equal(new ParametreTracabilite(true, "toujours"), depot.Tracabilite);
        Assert.Equal("parametre_tracabilite", Assert.Single(journal.Actions));
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => tracabilite.ExecuterAsync(new ParametreTracabilite(true, "souvent"), 1));
        Assert.Equal("patient_obligatoire", e.Champ);
    }
}
