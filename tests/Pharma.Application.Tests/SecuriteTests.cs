using Pharma.Application.Securite;
using Pharma.Domain.Securite;

namespace Pharma.Application.Tests;

public class SecuriteTests
{
    [Fact]
    public async Task La_connexion_est_datee()
    {
        var comptes = new ComptesEnMemoire(
            new Utilisateur { Id = 7, Identifiant = "vendeur", NomComplet = "Vendeur Test", MotDePasseHash = "hash:secret" });
        await new SeConnecter(comptes, new VerificateurFactice(), new JetonsFactices()).ExecuterAsync("vendeur", "secret");
        Assert.Equal([7L], comptes.Connexions);
    }

    [Fact]
    public async Task Cinq_mots_de_passe_faux_bloquent_la_connexion()
    {
        var comptes = new ComptesEnMemoire(
            new Utilisateur { Id = 7, Identifiant = "vendeur", NomComplet = "Vendeur Test", MotDePasseHash = "hash:secret" });
        var journal = new JournalEspion();
        var essais = new EssaisConnexion();
        var cas = new SeConnecter(comptes, new VerificateurFactice(), new JetonsFactices(), essais, new HorlogeFixe(new DateOnly(2026, 11, 10)), journal);

        for (var i = 1; i < EssaisConnexion.EssaisMax; i++)
            Assert.Null(await cas.ExecuterAsync("vendeur", "faux"));
        // Le cinquième essai bloque ; le bon mot de passe est ensuite refusé pendant le blocage
        await Assert.ThrowsAsync<Pharma.Domain.Commun.RegleMetierException>(() => cas.ExecuterAsync("vendeur", "faux"));
        await Assert.ThrowsAsync<Pharma.Domain.Commun.RegleMetierException>(() => cas.ExecuterAsync("vendeur", "secret"));
        Assert.Empty(comptes.Connexions);

        // Blocage terminé : la connexion repasse, le compteur repart de zéro
        var apres = new HorlogeFixe(new DateOnly(2026, 11, 11));
        Assert.NotNull(await new SeConnecter(comptes, new VerificateurFactice(), new JetonsFactices(), essais, apres, journal).ExecuterAsync("vendeur", "secret"));
        Assert.Null(essais.BloqueJusqu("vendeur", apres.MaintenantUtc));
    }

    [Fact]
    public void Une_rafale_d_identifiants_inventes_ne_leve_pas_un_blocage_en_cours()
    {
        var essais = new EssaisConnexion();
        var t = new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < EssaisConnexion.EssaisMax; i++) essais.NoterEchec("titulaire", t);
        Assert.NotNull(essais.BloqueJusqu("titulaire", t));
        for (var i = 0; i < 25_000; i++) essais.NoterEchec("invente" + i, t.AddMilliseconds(i));
        Assert.NotNull(essais.BloqueJusqu("titulaire", t.AddMinutes(1)));
        Assert.True(essais.Nombre <= 10_000);
    }

    [Fact]
    public void Memoire_pleine_de_blocages_refuse_un_nouvel_identifiant_sans_grossir_revue_M6()
    {
        var essais = new EssaisConnexion();
        var t = new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 10_000; i++)
            for (var k = 0; k < EssaisConnexion.EssaisMax; k++) essais.NoterEchec("bloque" + i, t);
        Assert.Equal(10_000, essais.Nombre);
        Assert.True(essais.NoterEchec("nouveau", t));          // refusé, non ajouté
        Assert.NotNull(essais.BloqueJusqu("nouveau", t));
        Assert.Equal(10_000, essais.Nombre);
        // Blocages expirés : supprimés d'abord, le nouvel identifiant est de nouveau suivi normalement
        var apres = t + EssaisConnexion.Blocage + TimeSpan.FromSeconds(1);
        Assert.Null(essais.BloqueJusqu("nouveau", apres));
        Assert.False(essais.NoterEchec("nouveau", apres));
        Assert.Equal(1, essais.Nombre);
    }

    [Fact]
    public void Codes_faux_comptes_au_demandeur_bloque_apres_cinq_en_quinze_minutes_revue_I2()
    {
        var e = new EssaisCodeDemandeur();
        var t = new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i < EssaisCodeDemandeur.EssaisMax; i++) Assert.Equal(i, e.Reserver(4, t.AddMinutes(i)).Essai);
        var cinquieme = e.Reserver(4, t.AddMinutes(5));
        Assert.True(cinquieme.VientDeBloquer);
        Assert.Null(e.Reserver(4, t.AddMinutes(6)).Essai);                       // demandeur bloqué
        Assert.Equal(1, e.Reserver(9, t.AddMinutes(6)).Essai);                   // un autre demandeur n'est pas touché
        Assert.Equal(1, e.Reserver(4, t.AddMinutes(5) + EssaisCodeDemandeur.Blocage).Essai);   // fin du blocage
        // Essais anciens (plus de 15 minutes) oubliés
        var f = new EssaisCodeDemandeur();
        for (var i = 0; i < 4; i++) f.Reserver(1, t);
        Assert.False(f.Reserver(1, t.AddMinutes(16)).VientDeBloquer);
        // Un seul demandeur sur un valideur : 1 ; un second demandeur : 2 (blocage progressif du valideur)
        Assert.Equal(1, e.NoterEchecValideur(1, 4, t));
        Assert.Equal(1, e.NoterEchecValideur(1, 4, t.AddMinutes(1)));
        Assert.Equal(2, e.NoterEchecValideur(1, 9, t.AddMinutes(2)));
        Assert.Equal(1, e.NoterEchecValideur(1, 9, t.AddMinutes(30)));
    }

    private sealed class VerificateurCompteur : IVerificateurMotDePasse
    {
        public int Factices { get; private set; }
        public bool Verifier(string motDePasse, string hash) => hash == "hash:" + motDePasse;
        public void VerifierFactice(string motDePasse) => Factices++;
    }

    [Fact]
    public async Task Identifiant_inconnu_verifie_un_hachage_factice()
    {
        var verif = new VerificateurCompteur();
        var comptes = new ComptesEnMemoire(new Utilisateur { Id = 7, Identifiant = "vendeur", NomComplet = "Vendeur Test", MotDePasseHash = "hash:secret" });
        Assert.Null(await new SeConnecter(comptes, verif, new JetonsFactices()).ExecuterAsync("inconnu", "secret"));
        Assert.Equal(1, verif.Factices);
        Assert.Null(await new SeConnecter(comptes, verif, new JetonsFactices()).ExecuterAsync("vendeur", "faux"));
        Assert.Equal(1, verif.Factices);
    }

    [Fact]
    public async Task Changer_son_mot_de_passe_compte_les_essais_avec_la_connexion_et_journalise()
    {
        var comptes = new ComptesEnMemoire(new Utilisateur { Id = 7, Identifiant = "vendeur", NomComplet = "Vendeur Test", MotDePasseHash = "hash:secret" });
        var essais = new EssaisConnexion();
        var journal = new JournalEspion();
        var horloge = new HorlogeFixe(new DateOnly(2026, 11, 15));
        var cas = new ChangerMonMotDePasse(comptes, new VerificateurFactice(), new HacheurFactice(), new JetonsFactices(), journal, essais, horloge);
        for (var i = 0; i < EssaisConnexion.EssaisMax; i++)
            await Assert.ThrowsAsync<Pharma.Domain.Commun.RegleMetierException>(() => cas.ExecuterAsync(7, "faux", "Nouveau-2026", "Nouveau-2026"));
        Assert.Contains("mot_de_passe_actuel_refuse", journal.Actions);
        Assert.Contains("connexion_bloquee", journal.Actions);
        // Bloqué : même le bon mot de passe actuel est refusé, et la connexion aussi (même compteur)
        await Assert.ThrowsAsync<Pharma.Domain.Commun.RegleMetierException>(() => cas.ExecuterAsync(7, "secret", "Nouveau-2026", "Nouveau-2026"));
        Assert.NotNull(essais.BloqueJusqu("vendeur", horloge.MaintenantUtc));
    }

    [Fact]
    public async Task Une_connexion_reussie_remet_les_essais_a_zero()
    {
        var comptes = new ComptesEnMemoire(
            new Utilisateur { Id = 7, Identifiant = "vendeur", NomComplet = "Vendeur Test", MotDePasseHash = "hash:secret" });
        var cas = new SeConnecter(comptes, new VerificateurFactice(), new JetonsFactices(), new EssaisConnexion(), new HorlogeFixe(new DateOnly(2026, 11, 10)));
        for (var tour = 0; tour < 3; tour++)
        {
            for (var i = 1; i < EssaisConnexion.EssaisMax; i++)
                Assert.Null(await cas.ExecuterAsync("vendeur", "faux"));
            Assert.NotNull(await cas.ExecuterAsync("vendeur", "secret"));
        }
    }

    private static SeConnecter CasDUsage() => new(
        new ComptesEnMemoire(
            new Utilisateur { Id = 7, Identifiant = "vendeur", NomComplet = "Vendeur Test", MotDePasseHash = "hash:secret" },
            new Utilisateur { Id = 8, Identifiant = "ancien", NomComplet = "Ancien", MotDePasseHash = "hash:secret", Actif = false }),
        new VerificateurFactice(),
        new JetonsFactices());

    [Fact]
    public async Task Connexion_reussie_renvoie_une_session()
    {
        var session = await CasDUsage().ExecuterAsync(" vendeur ", "secret");

        Assert.NotNull(session);
        Assert.Equal("jeton-7", session.Jeton);
        Assert.Equal("Vendeur Test", session.NomComplet);
    }

    [Theory]
    [InlineData("vendeur", "faux")]
    [InlineData("inconnu", "secret")]
    [InlineData("ancien", "secret")]
    [InlineData("", "secret")]
    [InlineData("vendeur", "")]
    public async Task Connexion_refusee(string identifiant, string motDePasse) =>
        Assert.Null(await CasDUsage().ExecuterAsync(identifiant, motDePasse));
}
