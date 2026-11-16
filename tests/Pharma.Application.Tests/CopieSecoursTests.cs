using Pharma.Application.Sauvegardes;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

/// <summary>PC de secours (2026-11-16) : seconde copie vérifiée, alerte de retard, réglage, test de restauration.</summary>
public class CopieSecoursTests
{
    private static readonly DateTime Maintenant = new(2026, 11, 16, 21, 0, 0, DateTimeKind.Utc);

    private sealed class DepotMemoire : ICopieSecoursDepot
    {
        public ReglagesCopieSecours R { get; set; } = new();
        public EtatCopieSecours E { get; set; } = new();
        public ReglagesCopieSecours Reglages() => R;
        public void EnregistrerReglages(ReglagesCopieSecours reglages) => R = reglages;
        public EtatCopieSecours Etat() => E;
        public void EnregistrerEtat(EtatCopieSecours etat) => E = etat;
    }

    private sealed class FichiersMemoire : IFichiersCopieSecours
    {
        public Dictionary<string, string> Contenus { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Empreintes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool DossierAccessible { get; set; } = true;
        public bool CopieAbimee { get; set; }
        public bool Accessible(string dossier) => DossierAccessible;
        public void Copier(string source, string destination) => Contenus[destination] = CopieAbimee ? Contenus[source] + "x" : Contenus[source];
        public long Taille(string fichier) => Contenus[fichier].Length;
        public string Sha256(string fichier) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Contenus[fichier])));
        public string? EmpreinteNotee(string copie) => Empreintes.GetValueOrDefault(copie);
        public void NoterEmpreinte(string copie, string sha256) => Empreintes[copie] = sha256;
        public IReadOnlyList<string> Copies(string dossier) =>
            Contenus.Keys.Where(k => k.StartsWith(dossier, StringComparison.OrdinalIgnoreCase)).OrderByDescending(k => k, StringComparer.Ordinal).ToList();
        public bool Existe(string fichier) => Contenus.ContainsKey(fichier);
        public void Supprimer(string copie) { Contenus.Remove(copie); Empreintes.Remove(copie); }
    }

    private sealed class TestFactice(string? erreur = null) : ITestRestauration
    {
        public int Appels { get; private set; }
        public Task<string> TesterAsync(string fichier, CancellationToken ct = default)
        {
            Appels++;
            return erreur is null ? Task.FromResult("12 tables, 3 employé(s).") : throw new RegleMetierException(erreur);
        }
    }

    private static (DepotMemoire, FichiersMemoire, AlertesTitulaireEspion, CopierSauvegardeSecours) Preparer(string? dossier = @"F:\Secours")
    {
        var depot = new DepotMemoire { R = new ReglagesCopieSecours(dossier, 26, Maintenant.AddDays(-1)) };
        var fichiers = new FichiersMemoire();
        fichiers.Contenus[@"E:\Sauv\pharmacie_20261116_220000.dump"] = "contenu de la sauvegarde";
        var alertes = new AlertesTitulaireEspion();
        return (depot, fichiers, alertes, new CopierSauvegardeSecours(depot, fichiers, alertes, new HorlogeReglable(Maintenant)));
    }

    [Fact]
    public async Task Copie_verifiee_empreinte_notee_et_etat_mis_a_jour()
    {
        var (depot, fichiers, alertes, copier) = Preparer();
        var message = await copier.ExecuterAsync(@"E:\Sauv\pharmacie_20261116_220000.dump", 14);
        var cible = @"F:\Secours\pharmacie_20261116_220000.dump";
        Assert.Contains("vérifiée", message);
        Assert.Equal(fichiers.Contenus[@"E:\Sauv\pharmacie_20261116_220000.dump"], fichiers.Contenus[cible]);
        Assert.Equal(fichiers.Sha256(cible), fichiers.Empreintes[cible]);
        Assert.Equal(Maintenant, depot.E.DerniereCopieUtc);
        Assert.Equal(cible, depot.E.DerniereCopie);
        Assert.Empty(alertes.Sauvegardes);
    }

    [Fact]
    public async Task Sans_second_emplacement_rien_n_est_copie()
    {
        var (_, fichiers, _, copier) = Preparer(dossier: null);
        Assert.Null(await copier.ExecuterAsync(@"E:\Sauv\pharmacie_20261116_220000.dump", 14));
        Assert.Single(fichiers.Contenus);
    }

    [Fact]
    public async Task Emplacement_absent_titulaire_prevenu_et_echec_note()
    {
        var (depot, fichiers, alertes, copier) = Preparer();
        fichiers.DossierAccessible = false;
        var message = await copier.ExecuterAsync(@"E:\Sauv\pharmacie_20261116_220000.dump", 14);
        Assert.Contains("Copie de secours impossible", message);
        Assert.Single(alertes.Sauvegardes);
        Assert.Equal(Maintenant, depot.E.DernierEchecUtc);
        Assert.Null(depot.E.DerniereCopieUtc);
    }

    [Fact]
    public async Task Copie_differente_supprimee_et_signalee()
    {
        var (depot, fichiers, alertes, copier) = Preparer();
        fichiers.CopieAbimee = true;
        await copier.ExecuterAsync(@"E:\Sauv\pharmacie_20261116_220000.dump", 14);
        Assert.False(fichiers.Existe(@"F:\Secours\pharmacie_20261116_220000.dump"));
        Assert.Single(alertes.Sauvegardes);
        Assert.Contains("SHA-256", depot.E.DernierEchec);
    }

    [Fact]
    public async Task Copies_au_dela_de_la_conservation_supprimees()
    {
        var (_, fichiers, _, copier) = Preparer();
        fichiers.Contenus[@"F:\Secours\pharmacie_20261114_220000.dump"] = "a";
        fichiers.Contenus[@"F:\Secours\pharmacie_20261115_220000.dump"] = "b";
        await copier.ExecuterAsync(@"E:\Sauv\pharmacie_20261116_220000.dump", 2);
        Assert.Equal([@"F:\Secours\pharmacie_20261116_220000.dump", @"F:\Secours\pharmacie_20261115_220000.dump"], fichiers.Copies(@"F:\Secours"));
    }

    [Fact]
    public async Task Retard_alerte_une_seule_fois_par_periode()
    {
        var depot = new DepotMemoire { R = new ReglagesCopieSecours(@"F:\Secours", 26), E = new EtatCopieSecours(DerniereCopieUtc: Maintenant.AddHours(-30)) };
        var alertes = new AlertesTitulaireEspion();
        var horloge = new HorlogeReglable(Maintenant);
        var surveiller = new SurveillerCopieSecours(depot, alertes, horloge);
        Assert.True(await surveiller.ExecuterAsync());
        Assert.False(await surveiller.ExecuterAsync());
        horloge.MaintenantUtc = Maintenant.AddHours(27);
        Assert.True(await surveiller.ExecuterAsync());
        Assert.Equal(2, alertes.Sauvegardes.Count);
    }

    [Fact]
    public async Task Copie_recente_aucune_alerte()
    {
        var depot = new DepotMemoire { R = new ReglagesCopieSecours(@"F:\Secours", 26), E = new EtatCopieSecours(DerniereCopieUtc: Maintenant.AddHours(-2)) };
        var alertes = new AlertesTitulaireEspion();
        Assert.False(await new SurveillerCopieSecours(depot, alertes, new HorlogeReglable(Maintenant)).ExecuterAsync());
        Assert.Empty(alertes.Sauvegardes);
    }

    [Fact]
    public async Task Reglage_chemin_relatif_refuse_et_reglage_journalise()
    {
        var depot = new DepotMemoire();
        var journal = new JournalEspion();
        var modifier = new ModifierReglagesCopieSecours(depot, journal, new HorlogeReglable(Maintenant));
        await Assert.ThrowsAsync<RegleMetierException>(() => modifier.ExecuterAsync("Secours", 26, 1));
        await Assert.ThrowsAsync<RegleMetierException>(() => modifier.ExecuterAsync(@"F:\Secours", 0, 1));
        await modifier.ExecuterAsync(@"\\PC-SECOURS\Sauvegardes", 12, 1);
        Assert.Equal(new ReglagesCopieSecours(@"\\PC-SECOURS\Sauvegardes", 12, Maintenant), depot.R);
        Assert.Equal(["reglages_copie_secours"], journal.Actions);
    }

    [Fact]
    public async Task Test_de_restauration_reussi_journalise()
    {
        var (depot, fichiers, _, copier) = Preparer();
        await copier.ExecuterAsync(@"E:\Sauv\pharmacie_20261116_220000.dump", 14);
        var journal = new JournalEspion();
        var test = new TestFactice();
        var r = await new TesterRestaurationCopie(depot, fichiers, test, journal, new HorlogeReglable(Maintenant)).ExecuterAsync(1);
        Assert.True(r.Reussi);
        Assert.Contains("empreinte SHA-256 vérifiée", r.Message);
        Assert.True(depot.E.DernierTestReussi);
        Assert.Equal(["test_restauration_copie_secours"], journal.Actions);
    }

    [Fact]
    public async Task Copie_abimee_apres_coup_test_echoue_sans_restaurer()
    {
        var (depot, fichiers, _, copier) = Preparer();
        await copier.ExecuterAsync(@"E:\Sauv\pharmacie_20261116_220000.dump", 14);
        fichiers.Contenus[@"F:\Secours\pharmacie_20261116_220000.dump"] = "abîmé";
        var test = new TestFactice();
        var r = await new TesterRestaurationCopie(depot, fichiers, test, new JournalEspion(), new HorlogeReglable(Maintenant)).ExecuterAsync(1);
        Assert.False(r.Reussi);
        Assert.Equal(0, test.Appels);
        Assert.False(depot.E.DernierTestReussi);
    }

    [Fact]
    public async Task Sans_copie_le_test_est_refuse()
    {
        var depot = new DepotMemoire { R = new ReglagesCopieSecours(@"F:\Secours") };
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            new TesterRestaurationCopie(depot, new FichiersMemoire(), new TestFactice(), new JournalEspion(), new HorlogeReglable(Maintenant)).ExecuterAsync(1));
    }
}
