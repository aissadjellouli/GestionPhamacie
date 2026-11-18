using Pharma.Application.Administration;
using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.Synchronisation;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

/// <summary>Lots S5.1 et S5.5 : ordres du serveur central (application, confirmation, refus, expiration, résumé).</summary>
public class OrdresCentrauxTests
{
    private static readonly DateTime Maintenant = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);

    private sealed class Horloge(DateTime utc) : IHorloge
    {
        public DateTime MaintenantUtc => utc;
        public DateOnly Aujourdhui => DateOnly.FromDateTime(utc);
    }

    private sealed class Depot : IOrdresCentrauxDepot
    {
        public readonly List<OrdreRecu> Ordres = [];
        public readonly List<(Guid Id, string Statut, string? Motif, long? Par)> Notes = [];
        public Task<IReadOnlyList<OrdreRecu>> EnCoursAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OrdreRecu>>(Ordres.Where(o => o.Statut is StatutsOrdre.AAppliquer or StatutsOrdre.AConfirmer).ToList());
        public Task<OrdreRecu?> LireAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Ordres.FirstOrDefault(o => o.Id == id));
        public Task<PageOrdresRecus> ListerAsync(string? statut, int page, int taille, CancellationToken ct = default) =>
            Task.FromResult(new PageOrdresRecus(Ordres, Ordres.Count, Ordres.Count(o => o.Statut == StatutsOrdre.AConfirmer)));
        public Task NoterAsync(Guid id, string statut, string? motif, long? par, CancellationToken ct = default)
        {
            var i = Ordres.FindIndex(o => o.Id == id);
            Ordres[i] = Ordres[i] with { Statut = statut, Motif = motif, TraitePar = par };
            Notes.Add((id, statut, motif, par));
            return Task.CompletedTask;
        }
        public Task<bool> BaseEnLectureSeuleAsync(CancellationToken ct = default) => Task.FromResult(false);
        public string Statut(Guid id) => Ordres.Single(o => o.Id == id).Statut;
    }

    private sealed class Alertes : IAlertesTitulaire
    {
        public readonly List<string> Objets = [];
        public Task SynchronisationAsync(string objet, string detail, string sms, CancellationToken ct = default) { Objets.Add(objet); return Task.CompletedTask; }
        public Task EcartCaisseAsync(string employe, string poste, decimal ecart, decimal seuilCaisse, CancellationToken ct = default, bool releve = false) => Task.CompletedTask;
        public Task LotBloqueAsync(string produit, string numeroLot, int boitesEnStock, int boitesVendues, CancellationToken ct = default) => Task.CompletedTask;
        public Task SauvegardeEnEchecAsync(bool disqueAbsent, string detail, CancellationToken ct = default) => Task.CompletedTask;
        public Task EcartsInventaireAsync(string rayon, int lignes, int boites, decimal valeur, CancellationToken ct = default) => Task.CompletedTask;
        public Task AnnulationsAsync(string poste, int annulations, int seuil, CancellationToken ct = default) => Task.CompletedTask;
        public Task LotDelaisseAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
        public Task SurveillanceAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static OrdreRecu Ordre(string type, string? cible = null, string attendu = "{}", string donnees = "{}", DateTime? expire = null) =>
        new(Guid.NewGuid(), type, cible, attendu, donnees, "admin.essai", Maintenant.AddHours(-1), expire ?? Maintenant.AddDays(6),
            StatutsOrdre.AAppliquer, null, Maintenant.AddMinutes(-5), null, null);

    private static (Depot Depot, UtilisateursEnMemoire Admin, Alertes Alertes, JournalEspion Journal, AppliquerOrdresCentraux Auto,
        ConfirmerOrdreCentral Confirmer, RefuserOrdreCentral Refuser) Monter(bool titulaire = true)
    {
        var (depot, admin, alertes, journal) = (new Depot(), new UtilisateursEnMemoire(), new Alertes(), new JournalEspion());
        var droits = new DroitsFactices("admin.utilisateurs") { Titulaire = titulaire };
        var executer = new ExecuterOrdreCentral(admin, new CreerUtilisateur(admin, new HacheurFactice(), journal, droits),
            new ModifierUtilisateur(admin, new HacheurFactice(), journal, droits), new EnregistrerDroits(admin, journal));
        var horloge = new Horloge(Maintenant);
        return (depot, admin, alertes, journal, new AppliquerOrdresCentraux(depot, executer, horloge, alertes),
            new ConfirmerOrdreCentral(depot, executer, droits, horloge), new RefuserOrdreCentral(depot, droits));
    }

    [Fact]
    public async Task Essai_et_blocage_appliques_tout_de_suite_le_blocage_previent_le_titulaire()
    {
        var m = Monter();
        var essai = Ordre(TypesOrdre.Essai);
        var bloquer = Ordre(TypesOrdre.EmployeBloquer, "3", """{"role":"vendeur","actif":true}""");
        m.Depot.Ordres.AddRange([essai, bloquer]);

        Assert.Equal(2, await m.Auto.ExecuterAsync());
        Assert.Equal(StatutsOrdre.Applique, m.Depot.Statut(essai.Id));
        Assert.Equal(StatutsOrdre.Applique, m.Depot.Statut(bloquer.Id));
        Assert.Equal((3L, 3L, false, (string?)null), m.Admin.Modifie);
        Assert.Single(m.Alertes.Objets, o => o.Contains("bloqué"));
        Assert.Equal(0, await m.Auto.ExecuterAsync());                 // déjà traités
    }

    [Fact]
    public async Task Blocage_refuse_pour_un_titulaire_ou_si_la_valeur_a_change_dans_l_officine()
    {
        var m = Monter();
        var titulaire = Ordre(TypesOrdre.EmployeBloquer, "1", """{"role":"titulaire","actif":true}""");
        var change = Ordre(TypesOrdre.EmployeBloquer, "3", """{"role":"vendeur","actif":false}""");
        var inconnu = Ordre(TypesOrdre.EmployeBloquer, "99", """{"actif":true}""");
        m.Depot.Ordres.AddRange([titulaire, change, inconnu]);
        await m.Auto.ExecuterAsync();

        Assert.All([titulaire, change, inconnu], o => Assert.Equal(StatutsOrdre.Refuse, m.Depot.Statut(o.Id)));
        Assert.Contains("titulaire", m.Depot.Ordres.Single(o => o.Id == titulaire.Id).Motif);
        Assert.Contains(ExecuterOrdreCentral.ModifieDansOfficine, m.Depot.Ordres.Single(o => o.Id == change.Id).Motif);
        Assert.Null(m.Admin.Modifie);                                   // rien n'a changé
        Assert.Equal(3, m.Alertes.Objets.Count(o => o.Contains("refusé")));
    }

    [Fact]
    public async Task Creation_attend_le_titulaire_puis_s_applique_avec_un_mot_de_passe_provisoire()
    {
        var m = Monter();
        var creer = Ordre(TypesOrdre.EmployeCreer, donnees: """{"nom":"Nouvel Employé","identifiant":"nouvel.employe","role":"vendeur"}""");
        m.Depot.Ordres.Add(creer);

        await m.Auto.ExecuterAsync();
        Assert.Equal(StatutsOrdre.AConfirmer, m.Depot.Statut(creer.Id));
        Assert.Null(m.Admin.Cree);
        Assert.Single(m.Alertes.Objets, o => o.Contains("à confirmer"));

        // Sans mot de passe : erreur de saisie, l'ordre reste à confirmer
        await Assert.ThrowsAsync<RegleMetierException>(() => m.Confirmer.ExecuterAsync(creer.Id, null, 1));
        Assert.Equal(StatutsOrdre.AConfirmer, m.Depot.Statut(creer.Id));

        Assert.Equal(StatutsOrdre.Applique, await m.Confirmer.ExecuterAsync(creer.Id, "Provisoire-2026", 1));
        Assert.Equal(("Nouvel Employé", "nouvel.employe", "hash:Provisoire-2026", 3L), m.Admin.Cree);
        Assert.Equal(1L, m.Depot.Notes.Last().Par);                     // au nom du titulaire qui confirme
        Assert.Contains("utilisateur_cree", m.Journal.Actions);
    }

    [Fact]
    public async Task Creation_avec_le_role_de_titulaire_refusee_a_la_confirmation()
    {
        var m = Monter();
        var creer = Ordre(TypesOrdre.EmployeCreer, donnees: """{"nom":"X","identifiant":"xx","role":"titulaire"}""") with { Statut = StatutsOrdre.AConfirmer };
        m.Depot.Ordres.Add(creer);
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => m.Confirmer.ExecuterAsync(creer.Id, "Provisoire-2026", 1));
        Assert.Contains("titulaire", e.Message);
        Assert.Equal(StatutsOrdre.Refuse, m.Depot.Statut(creer.Id));
        Assert.Null(m.Admin.Cree);
    }

    [Fact]
    public async Task Droits_d_un_role_appliques_seulement_si_les_droits_actuels_sont_ceux_vus_au_central()
    {
        var m = Monter();
        var bon = Ordre(TypesOrdre.DroitsRole, "vendeur", """{"permissions":["caisse.vendre"]}""", """{"permissions":["admin.utilisateurs","caisse.vendre"]}""")
            with { Statut = StatutsOrdre.AConfirmer };
        var perime = Ordre(TypesOrdre.DroitsRole, "vendeur", """{"permissions":[]}""", """{"permissions":["caisse.vendre"]}""")
            with { Statut = StatutsOrdre.AConfirmer };
        m.Depot.Ordres.AddRange([bon, perime]);

        await m.Confirmer.ExecuterAsync(bon.Id, null, 1);
        Assert.Equal(["admin.utilisateurs", "caisse.vendre"], m.Admin.Droits!.Single().Permissions);
        await Assert.ThrowsAsync<RegleMetierException>(() => m.Confirmer.ExecuterAsync(perime.Id, null, 1));
        Assert.Equal(StatutsOrdre.Refuse, m.Depot.Statut(perime.Id));
    }

    [Fact]
    public async Task Modification_et_deblocage_attendent_le_titulaire_et_passent_par_le_cas_d_usage()
    {
        var m = Monter();
        var modifier = Ordre(TypesOrdre.EmployeModifier, "3", """{"role":"vendeur","actif":true}""", """{"nom":"Vendeur Principal"}""");
        m.Depot.Ordres.Add(modifier);
        await m.Auto.ExecuterAsync();
        Assert.Equal(StatutsOrdre.AConfirmer, m.Depot.Statut(modifier.Id));
        Assert.Null(m.Admin.Modifie);
        await m.Confirmer.ExecuterAsync(modifier.Id, null, 1);
        Assert.Equal((3L, 3L, true, (string?)null), m.Admin.Modifie);
        Assert.Contains("utilisateur_modifie", m.Journal.Actions);
    }

    [Fact]
    public async Task Ordre_expire_et_type_inconnu_jamais_appliques()
    {
        var m = Monter();
        var vieux = Ordre(TypesOrdre.Essai, expire: Maintenant.AddMinutes(-1));
        var aConfirmerVieux = Ordre(TypesOrdre.EmployeDebloquer, "3", expire: Maintenant.AddMinutes(-1)) with { Statut = StatutsOrdre.AConfirmer };
        var inconnu = Ordre("prix_produit");
        m.Depot.Ordres.AddRange([vieux, aConfirmerVieux, inconnu]);
        await m.Auto.ExecuterAsync();
        Assert.Equal(StatutsOrdre.Expire, m.Depot.Statut(vieux.Id));
        Assert.Equal(StatutsOrdre.Expire, m.Depot.Statut(aConfirmerVieux.Id));
        Assert.Equal(StatutsOrdre.Refuse, m.Depot.Statut(inconnu.Id));
        Assert.Null(m.Admin.Modifie);
    }

    [Fact]
    public async Task Refus_par_le_titulaire_avec_motif_et_jamais_par_un_autre()
    {
        var m = Monter();
        var o = Ordre(TypesOrdre.DroitsRole, "vendeur") with { Statut = StatutsOrdre.AConfirmer };
        m.Depot.Ordres.Add(o);
        await Assert.ThrowsAsync<RegleMetierException>(() => m.Refuser.ExecuterAsync(o.Id, "x", 1));          // motif trop court
        await m.Refuser.ExecuterAsync(o.Id, "pas pendant les gardes", 1);
        Assert.Equal(StatutsOrdre.Refuse, m.Depot.Statut(o.Id));
        Assert.Equal("refusé par le titulaire : pas pendant les gardes", m.Depot.Notes.Last().Motif);

        var autre = Monter(titulaire: false);
        var o2 = Ordre(TypesOrdre.DroitsRole, "vendeur") with { Statut = StatutsOrdre.AConfirmer };
        autre.Depot.Ordres.Add(o2);
        await Assert.ThrowsAnyAsync<Exception>(() => autre.Refuser.ExecuterAsync(o2.Id, "pas pendant les gardes", 3));
        await Assert.ThrowsAnyAsync<Exception>(() => autre.Confirmer.ExecuterAsync(o2.Id, null, 3));
        Assert.Equal(StatutsOrdre.AConfirmer, autre.Depot.Statut(o2.Id));
    }

    [Fact]
    public void Resume_lisible_d_apres_la_base_de_l_officine()
    {
        var etat = new UtilisateursEnMemoire().Etat;
        Assert.Equal("Vendeur (vendeur) : bloqué (plus aucune connexion).", ResumeOrdre.Texte(Ordre(TypesOrdre.EmployeBloquer, "3"), etat));
        Assert.Equal("Nouveau compte « A B », identifiant ab, rôle Vendeur.",
            ResumeOrdre.Texte(Ordre(TypesOrdre.EmployeCreer, donnees: """{"nom":"A B","identifiant":"ab","role":"vendeur"}"""), etat));
        Assert.Equal("Rôle Vendeur : ajout de admin.utilisateurs.",
            ResumeOrdre.Texte(Ordre(TypesOrdre.DroitsRole, "vendeur", """{"permissions":["caisse.vendre"]}""", """{"permissions":["admin.utilisateurs","caisse.vendre"]}"""), etat));
        Assert.True(TypesOrdre.ConfirmationRequise(TypesOrdre.EmployeCreer));
        Assert.False(TypesOrdre.ConfirmationRequise(TypesOrdre.EmployeBloquer));
    }
}
