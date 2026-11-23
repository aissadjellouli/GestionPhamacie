using Pharma.Application.Administration;
using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.Sauvegardes;
using Pharma.Application.SecondServeur;
using Pharma.Application.Securite;
using Pharma.Application.Surveillance;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Tests;

/// <summary>Correctifs de la revue de sécurité du 2026-11-23 (sauvegardes, ressaisie du mot de passe, poste lié au jeton, code comptoir, CSV).</summary>
public class RevueSecuriteTests
{
    private static readonly DateOnly Jour = new(2026, 11, 9);

    private sealed class CleEnMemoire : ICleIntegriteStockee
    {
        private readonly byte[] cle = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        public string Emplacement => "memoire";
        public bool Presente => true;
        public byte[] Lire() => (byte[])cle.Clone();
        public void Ecrire(byte[] nouvelle, bool remplacer) => throw new InvalidOperationException();
    }

    // ---- 1. Dossier des sauvegardes : disque local seulement, titulaire seul, journalisé ----

    [Theory]
    [InlineData(@"\\serveur\partage\sauvegardes")]
    [InlineData(@"\\?\UNC\serveur\partage")]
    [InlineData("//serveur/partage/sauvegardes")]
    public async Task Dossier_reseau_refuse_pour_les_sauvegardes(string dossier)
    {
        Assert.True(DossierSauvegarde.EstReseau(dossier));
        var depot = new SauvegardesEnMemoire();
        var modifier = new ModifierReglagesSauvegarde(depot, new JournalEspion(), new DroitsFactices { Titulaire = true });
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() =>
            modifier.ExecuterAsync(true, dossier, new TimeOnly(22, 0), 2, 1));
        Assert.Equal("dossier", ex.Champ);
        Assert.Equal(@"E:\Sauvegardes", depot.Reglages.Dossier);   // rien d'enregistré
    }

    [Fact]
    public void Disque_local_ou_amovible_n_est_pas_un_dossier_reseau()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.False(DossierSauvegarde.EstReseau(@"C:\Sauvegardes"));
    }

    [Fact]
    public async Task Changer_le_dossier_des_sauvegardes_est_reserve_au_titulaire_et_journalise()
    {
        var depot = new SauvegardesEnMemoire();
        var journal = new JournalEspion();
        // Employé avec le droit admin.sauvegardes mais pas titulaire : autre dossier refusé, rien n'est enregistré
        var employe = new ModifierReglagesSauvegarde(depot, journal, new DroitsFactices("admin.sauvegardes"));
        await Assert.ThrowsAsync<DroitRefuseException>(() => employe.ExecuterAsync(true, @"F:\Autre", new TimeOnly(22, 0), 2, 5));
        Assert.Equal(@"E:\Sauvegardes", depot.Reglages.Dossier);
        Assert.Empty(journal.Actions);

        // Même dossier : l'heure et la conservation restent réglables avec le droit seul
        await employe.ExecuterAsync(true, @"E:\Sauvegardes", new TimeOnly(23, 0), 3, 5);
        Assert.Equal(new TimeOnly(23, 0), depot.Reglages.Heure);
        Assert.DoesNotContain("dossier_sauvegarde_change", journal.Actions);

        // Titulaire : dossier changé, ancien et nouveau inscrits au journal
        var titulaire = new ModifierReglagesSauvegarde(depot, journal, new DroitsFactices { Titulaire = true });
        await titulaire.ExecuterAsync(true, @"F:\Autre", new TimeOnly(23, 0), 3, 1);
        Assert.Equal(@"F:\Autre", depot.Reglages.Dossier);
        var i = journal.Actions.IndexOf("dossier_sauvegarde_change");
        Assert.True(i >= 0);
        Assert.Equal(@"E:\Sauvegardes", journal.Details[i]["ancien"]);
        Assert.Equal(@"F:\Autre", journal.Details[i]["nouveau"]);
    }

    // ---- 2. Ressaisie du mot de passe pour les actions très sensibles ----

    [Fact]
    public async Task Export_de_la_cle_exige_le_mot_de_passe_du_titulaire()
    {
        var journal = new JournalEspion();
        var cas = new ExporterCleScellement(new CleEnMemoire(), journal, new HorlogeFixe(Jour),
            MotDePasseTitulaireFactice.Verification(journal));
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("fichier-secret-1", "fichier-secret-1", 1, default, "faux"));
        Assert.Equal(ExporterCleScellement.ChampMotDePasseTitulaire, ex.Champ);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("fichier-secret-1", "fichier-secret-1", 1));   // aucune ressaisie
        Assert.DoesNotContain(ExporterCleScellement.Action, journal.Actions);
        Assert.Equal(2, journal.Actions.Count(a => a == "mot_de_passe_actuel_refuse"));

        var f = await cas.ExecuterAsync("fichier-secret-1", "fichier-secret-1", 1, default, MotDePasseTitulaireFactice.Mot);
        Assert.NotEmpty(f.Contenu);
        Assert.Contains(ExporterCleScellement.Action, journal.Actions);
    }

    [Fact]
    public async Task Mots_de_passe_faux_comptes_dans_le_blocage_apres_cinq_essais()
    {
        var essais = new EssaisConnexion();
        var cas = new ExporterCleScellement(new CleEnMemoire(), new JournalEspion(), new HorlogeFixe(Jour),
            MotDePasseTitulaireFactice.Verification(essais: essais));
        for (var i = 0; i < EssaisConnexion.EssaisMax; i++)
            await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("fichier-secret-1", "fichier-secret-1", 1, default, "faux"));
        // Bloqué : même le bon mot de passe est refusé
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() =>
            cas.ExecuterAsync("fichier-secret-1", "fichier-secret-1", 1, default, MotDePasseTitulaireFactice.Mot));
        Assert.Contains("bloqué", ex.Message);
    }

    [Fact]
    public async Task Reglages_des_notifications_exigent_le_mot_de_passe_du_titulaire()
    {
        var journal = new JournalEspion();
        var cas = new ModifierReglagesNotification(new NotificationsEnMemoire(), new SecretsFactices(), journal, new DroitsFactices { Titulaire = true },
            MotDePasseTitulaireFactice.Verification());
        var m = new ModificationReglagesNotification("smtp.pharmacie.dz", 587, true, "alertes@pharmacie.dz", "alertes", null, "COM3", new TimeOnly(21, 0),
            null, true, true, true);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(m, 1, default, "faux"));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(m, 1));
        Assert.DoesNotContain("reglages_notification", journal.Actions);
        await cas.ExecuterAsync(m, 1, default, MotDePasseTitulaireFactice.Mot);
        Assert.Contains("reglages_notification", journal.Actions);
    }

    [Fact]
    public async Task Anomalie_marquee_traitee_exige_le_mot_de_passe_du_titulaire()
    {
        var depot = new SurveillanceEnMemoire();
        var cas = new TraiterSurveillance(depot, new DroitsFactices { Titulaire = true }, MotDePasseTitulaireFactice.Verification());
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(4, "vu avec l'employé", 1, default, "faux"));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(4, "vu avec l'employé", 1));
        Assert.Empty(depot.Traitees);
        Assert.True(await cas.ExecuterAsync(4, "vu avec l'employé", 1, default, MotDePasseTitulaireFactice.Mot));
        Assert.Equal((4L, "vu avec l'employé"), depot.Traitees.Single());
    }

    [Fact]
    public async Task Matrice_des_droits_exige_le_mot_de_passe_de_l_employe_connecte()
    {
        var admin = new UtilisateursEnMemoire();
        var cas = new EnregistrerDroits(admin, new JournalEspion(), MotDePasseTitulaireFactice.Verification());
        DroitsRole[] droits = [new DroitsRole(3, ["caisse.vendre"])];
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterParEmployeAsync(droits, 1, "faux"));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterParEmployeAsync(droits, 1, null));
        Assert.Null(admin.Droits);
        await cas.ExecuterParEmployeAsync(droits, 1, MotDePasseTitulaireFactice.Mot);
        Assert.Equal([3L], admin.Droits!.Select(d => d.RoleId));
    }

    // ---- 3. Poste lié au jeton ----

    [Fact]
    public async Task Le_poste_de_la_connexion_est_lie_au_jeton()
    {
        var jetons = new JetonsFactices();
        var comptes = new ComptesEnMemoire(new Utilisateur { Id = 5, Identifiant = "vendeur", NomComplet = "Vendeur", MotDePasseHash = "hash:motdepasse" });
        var cas = new SeConnecter(comptes, new VerificateurFactice(), jetons);
        await cas.ExecuterAsync("vendeur", "motdepasse", default, "  Caisse 1 ");
        await cas.ExecuterAsync("vendeur", "motdepasse");
        Assert.Equal(["Caisse 1", null], jetons.Postes);
    }

    [Fact]
    public async Task Connexion_par_code_et_changement_de_mot_de_passe_gardent_le_poste_dans_le_jeton()
    {
        var codes = new CodesEnMemoire();
        codes.Comptes[9] = new CompteComptoir(9, "vendeur.b", "Vendeur B", "hash:2580", 0, null);
        var jetons = new JetonsFactices();
        var par = new SeConnecterParCode(codes, new VerificateurFactice(), jetons, new SessionsEnMemoire(), new DroitsFactices("caisse.vendre"),
            new ComptesEnMemoire(), new HorlogeFixe(Jour), new JournalEspion());
        await par.ExecuterAsync("vendeur.b", "2580", "Caisse 1");
        Assert.Equal(["Caisse 1"], jetons.Postes);

        var comptes = new ComptesEnMemoire(new Utilisateur { Id = 5, Identifiant = "vendeur", NomComplet = "Vendeur", MotDePasseHash = "hash:ancien-mot-de-passe" });
        await new ChangerMonMotDePasse(comptes, new VerificateurFactice(), new HacheurFactice(), jetons, new JournalEspion())
            .ExecuterAsync(5, "ancien-mot-de-passe", "nouveau-mot-de-passe", "nouveau-mot-de-passe", default, "Caisse 1");
        Assert.Equal("Caisse 1", jetons.Postes[^1]);
    }

    // ---- 4. Référence « #numéro » : seulement pour un demandeur connecté ----

    [Fact]
    public async Task Reference_numero_refusee_par_la_connexion_par_code_anonyme()
    {
        var codes = new CodesEnMemoire();
        codes.Comptes[9] = new CompteComptoir(9, "vendeur.b", "Vendeur B", "hash:2580", 0, null);
        var jetons = new JetonsFactices();
        var par = new SeConnecterParCode(codes, new VerificateurFactice(), jetons, new SessionsEnMemoire(), new DroitsFactices("caisse.vendre"),
            new ComptesEnMemoire(), new HorlogeFixe(Jour), new JournalEspion());
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => par.ExecuterAsync("#9", "2580", "Caisse 1"));
        Assert.Equal(SeConnecterParCode.Refus, ex.Message);
        Assert.Empty(jetons.Postes);

        // Demandeur connecté (autorisation sur place) : la référence reste valable
        var verif = new VerificationCodeComptoir(codes, new VerificateurFactice(), new HorlogeFixe(Jour), new JournalEspion(), null, new EssaisCodeDemandeur());
        Assert.Equal(9, (await verif.VerifierAsync("#9", "2580", "Caisse 1", default, demandeurId: 3)).Id);
    }

    // ---- 5. Code comptoir d'un titulaire : effacé par un titulaire seul ----

    private sealed class DroitsParEmploye(long titulaireId, long administrateurId) : IPermissionService
    {
        public Task<bool> APermissionAsync(long u, string p, CancellationToken ct = default) => Task.FromResult(u == administrateurId || u == titulaireId);
        public Task<IReadOnlyList<string>> ListerAsync(long u, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<decimal> RemiseMaxAsync(long u, CancellationToken ct = default) => Task.FromResult(0m);
        public Task<bool> EstTitulaireAsync(long u, CancellationToken ct = default) => Task.FromResult(u == titulaireId);
    }

    [Fact]
    public async Task Seul_un_titulaire_efface_le_code_comptoir_d_un_titulaire()
    {
        var codes = new CodesEnMemoire();
        codes.Comptes[1] = new CompteComptoir(1, "titulaire", "Dr Titulaire", "hash:2580", 0, null);
        codes.Comptes[9] = new CompteComptoir(9, "vendeur.b", "Vendeur B", "hash:2580", 0, null);
        var cas = new EffacerCodeComptoir(codes, new DroitsParEmploye(titulaireId: 1, administrateurId: 7), new JournalEspion());

        // Administrateur non titulaire : refusé pour le code d'un titulaire, permis pour celui d'un employé
        await Assert.ThrowsAsync<DroitRefuseException>(() => cas.ExecuterAsync(1, 7));
        Assert.NotNull(codes.Comptes[1].CodeHash);
        await cas.ExecuterAsync(9, 7);
        Assert.Null(codes.Comptes[9].CodeHash);

        // Un titulaire efface le code d'un titulaire
        await cas.ExecuterAsync(1, 1);
        Assert.Null(codes.Comptes[1].CodeHash);
    }

    // ---- 6. Exports CSV : pas de formule exécutée par le tableur ----

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+33", "'+33")]
    [InlineData("-2", "'-2")]
    [InlineData("@SOMME(A1)", "'@SOMME(A1)")]
    [InlineData("\tcmd", "'\tcmd")]
    [InlineData("\rcmd", "'\rcmd")]
    [InlineData("Dupont", "Dupont")]
    [InlineData("", "")]
    public void Cellule_de_tableur_neutralisee(string brut, string attendu) =>
        Assert.Equal(attendu, ProtectionTableur.Neutraliser(brut));
}
