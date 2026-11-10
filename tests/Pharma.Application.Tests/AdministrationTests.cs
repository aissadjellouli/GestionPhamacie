using Pharma.Application.Commun;
using Pharma.Application.Administration;
using Pharma.Application.Alertes;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class SeuilsEnMemoire : ISeuilsAdmin
{
    public ReglagesSeuils Actuels { get; set; } = new(new ParametresAlerte(180, 90, 365, 2, 7, 30, 90), [], []);
    public ReglagesSeuils? Enregistres { get; private set; }
    public Task<ReglagesSeuils> LireAsync(CancellationToken ct = default) => Task.FromResult(Actuels);
    public Task EnregistrerAsync(ReglagesSeuils r, CancellationToken ct = default) { Enregistres = r; return Task.CompletedTask; }
}

internal sealed class MoteurEspion : IMoteurAlertes
{
    public int Appels { get; private set; }
    public Task<int> GenererAsync(CancellationToken ct = default) { Appels++; return Task.FromResult(7); }
}

internal sealed class UtilisateursEnMemoire : IUtilisateursAdmin
{
    public EtatDroits Etat { get; } = new(
        [
            new UtilisateurAdmin(1, "Dr Titulaire", "titulaire", 1, "Pharmacien titulaire", true, true, null),
            new UtilisateurAdmin(3, "Vendeur", "vendeur", 3, "Vendeur", false, true, null)
        ],
        [
            new RoleAdmin(1, "titulaire", "Pharmacien titulaire", true, 100, ["caisse.vendre", "admin.utilisateurs"]),
            new RoleAdmin(3, "vendeur", "Vendeur", false, 0, ["caisse.vendre"])
        ],
        [
            new PermissionAdmin("caisse.vendre", "Caisse", "Encaisser", false),
            new PermissionAdmin("caisse.remise", "Caisse", "Remise", true),
            new PermissionAdmin("admin.utilisateurs", "Administration", "Utilisateurs", true)
        ]);

    public (string Nom, string Identifiant, string Hash, long Role)? Cree { get; private set; }
    public (long Id, long Role, bool Actif, string? Hash)? Modifie { get; private set; }
    public IReadOnlyList<DroitsRole>? Droits { get; private set; }

    public Task<EtatDroits> LireAsync(CancellationToken ct = default) => Task.FromResult(Etat);
    public Task<long> CreerAsync(string n, string i, string h, long r, CancellationToken ct = default) { Cree = (n, i, h, r); return Task.FromResult(10L); }
    public Task ModifierAsync(long id, string n, long r, bool a, string? h, CancellationToken ct = default) { Modifie = (id, r, a, h); return Task.CompletedTask; }
    public Task EnregistrerDroitsAsync(IReadOnlyList<DroitsRole> d, CancellationToken ct = default) { Droits = d; return Task.CompletedTask; }
}

internal sealed class HacheurFactice : IHacheurMotDePasse
{
    public string Hacher(string motDePasse) => "hash:" + motDePasse;
}

internal sealed class PharmacieEnMemoire : IPharmacie
{
    public IdentitePharmacie Identite { get; private set; } = new(null, null, null, null, null, null, "fr");
    public Task<IdentitePharmacie> LireAsync(CancellationToken ct = default) => Task.FromResult(Identite);
    public Task EnregistrerAsync(IdentitePharmacie i, CancellationToken ct = default) { Identite = i; return Task.CompletedTask; }
}

public class AdministrationTests
{
    private static ReglagesSeuils Reglages(int orange, int rouge, params SeuilGrossiste[] grossistes) =>
        new(new ParametresAlerte(orange, rouge, 365, 2, 7, 30, 90), grossistes, []);

    [Fact]
    public async Task Seuils_enregistres_alertes_recalculees_et_journal()
    {
        var seuils = new SeuilsEnMemoire();
        var moteur = new MoteurEspion();
        var journal = new JournalEspion();

        var n = await new EnregistrerSeuils(seuils, moteur, journal).ExecuterAsync(
            Reglages(120, 60, new SeuilGrossiste(1, "Grossiste A", 180, 195, null)), utilisateurId: 1);

        Assert.Equal(7, n);
        Assert.Equal(1, moteur.Appels);
        Assert.Equal(120, seuils.Enregistres!.General.JoursOrange);
        Assert.Equal(["seuils_alerte"], journal.Actions);
    }

    [Fact]
    public async Task Seuil_grossiste_incoherent_refuse_sans_rien_ecrire()
    {
        var seuils = new SeuilsEnMemoire();
        await Assert.ThrowsAsync<RegleMetierException>(() => new EnregistrerSeuils(seuils, new MoteurEspion(), new JournalEspion())
            .ExecuterAsync(Reglages(180, 90, new SeuilGrossiste(1, "Grossiste A", null, null, 200)), 1));
        Assert.Null(seuils.Enregistres);
    }

    [Fact]
    public async Task Exception_produit_sans_seuil_ignoree()
    {
        var seuils = new SeuilsEnMemoire();
        var r = Reglages(180, 90) with { Exceptions = [new ExceptionProduit(9, "Insuline", 270, 120), new ExceptionProduit(5, "Amlodipine", null, null)] };
        await new EnregistrerSeuils(seuils, new MoteurEspion(), new JournalEspion()).ExecuterAsync(r, 1);
        Assert.Equal([9L], seuils.Enregistres!.Exceptions.Select(e => e.ProduitId));
    }

    [Fact]
    public async Task Creation_d_utilisateur_hachee_et_journalisee()
    {
        var admin = new UtilisateursEnMemoire();
        var journal = new JournalEspion();
        var id = await new CreerUtilisateur(admin, new HacheurFactice(), journal, new DroitsFactices("admin.utilisateurs"))
            .ExecuterAsync(" Amina B ", " Amina.B ", "motdepasse", roleId: 3, auteurId: 1);

        Assert.Equal(10L, id);
        Assert.Equal(("Amina B", "amina.b", "hash:motdepasse", 3L), admin.Cree);
        Assert.Equal(["utilisateur_cree"], journal.Actions);
    }

    [Fact]
    public async Task Seul_un_titulaire_cree_un_titulaire()
    {
        // Administrateur des comptes, mais pas titulaire : refusé
        await Assert.ThrowsAsync<DroitRefuseException>(() => new CreerUtilisateur(new UtilisateursEnMemoire(), new HacheurFactice(), new JournalEspion(), new DroitsFactices("admin.utilisateurs"))
            .ExecuterAsync("X", "nouveau", "motdepasse", roleId: 1, auteurId: 2));
        // Titulaire : le vrai pharmacien peut être ajouté
        var admin = new UtilisateursEnMemoire();
        await new CreerUtilisateur(admin, new HacheurFactice(), new JournalEspion(), new DroitsFactices { Titulaire = true })
            .ExecuterAsync("Dr Pharmacien Réel", "pharmacien", "motdepasse", roleId: 1, auteurId: 1);
        Assert.Equal(1L, admin.Cree!.Value.Role);
    }

    [Fact]
    public async Task Compte_titulaire_modifiable_par_un_titulaire_seulement()
    {
        await Assert.ThrowsAsync<DroitRefuseException>(() => new ModifierUtilisateur(new UtilisateursEnMemoire(), new HacheurFactice(), new JournalEspion(), new DroitsFactices("admin.utilisateurs"))
            .ExecuterAsync(1, "Dr", roleId: 1, actif: true, nouveauMotDePasse: null, auteurId: 2));
        var admin = new UtilisateursEnMemoire();
        await new ModifierUtilisateur(admin, new HacheurFactice(), new JournalEspion(), new DroitsFactices { Titulaire = true })
            .ExecuterAsync(1, "Dr Pharmacien Réel", roleId: 1, actif: true, nouveauMotDePasse: "nouveau-mdp", auteurId: 1);
        Assert.Equal((1L, 1L, true, "hash:nouveau-mdp"), admin.Modifie);
    }

    [Fact]
    public async Task Seul_un_titulaire_donne_le_role_titulaire() =>
        await Assert.ThrowsAsync<DroitRefuseException>(() => new ModifierUtilisateur(new UtilisateursEnMemoire(), new HacheurFactice(), new JournalEspion(), new DroitsFactices("admin.utilisateurs"))
            .ExecuterAsync(3, "Vendeur", roleId: 1, actif: true, nouveauMotDePasse: null, auteurId: 2));

    [Fact]
    public async Task Desactivation_sans_changer_le_mot_de_passe()
    {
        var admin = new UtilisateursEnMemoire();
        await new ModifierUtilisateur(admin, new HacheurFactice(), new JournalEspion(), new DroitsFactices("admin.utilisateurs"))
            .ExecuterAsync(3, "Vendeur", roleId: 3, actif: false, nouveauMotDePasse: "", auteurId: 1);
        Assert.Equal((3L, 3L, false, (string?)null), admin.Modifie);
    }

    [Fact]
    public async Task Droits_du_titulaire_jamais_modifies()
    {
        var admin = new UtilisateursEnMemoire();
        await new EnregistrerDroits(admin, new JournalEspion()).ExecuterAsync(
        [
            new DroitsRole(1, 0, []),                                  // titulaire : ignoré
            new DroitsRole(3, 5, ["caisse.vendre", "caisse.remise"])
        ], 1);
        Assert.Equal([3L], admin.Droits!.Select(d => d.RoleId));
    }

    [Theory]
    [InlineData(150)]
    [InlineData(-1)]
    public async Task Remise_maximale_hors_limites_refusee(int remise) =>
        await Assert.ThrowsAsync<RegleMetierException>(() => new EnregistrerDroits(new UtilisateursEnMemoire(), new JournalEspion())
            .ExecuterAsync([new DroitsRole(3, remise, ["caisse.vendre"])], 1));

    [Fact]
    public async Task Permission_inconnue_refusee() =>
        await Assert.ThrowsAsync<RegleMetierException>(() => new EnregistrerDroits(new UtilisateursEnMemoire(), new JournalEspion())
            .ExecuterAsync([new DroitsRole(3, 0, ["n.existe.pas"])], 1));

    [Fact]
    public async Task Pharmacie_enregistree_nettoyee()
    {
        var pharmacie = new PharmacieEnMemoire();
        await new ModifierPharmacie(pharmacie, new JournalEspion())
            .ExecuterAsync(new IdentitePharmacie(" Pharmacie Test ", "", null, null, null, null, "fr"), 1);
        Assert.Equal("Pharmacie Test", pharmacie.Identite.Nom);
        Assert.Null(pharmacie.Identite.Titulaire);
    }

    [Fact]
    public async Task Langue_non_traduite_refusee() =>
        await Assert.ThrowsAsync<RegleMetierException>(() => new ModifierPharmacie(new PharmacieEnMemoire(), new JournalEspion())
            .ExecuterAsync(new IdentitePharmacie("P", null, null, null, null, null, "ar"), 1));
}
