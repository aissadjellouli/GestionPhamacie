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

    /// <summary>Lot S5.3 : fichier remis par le « serveur central » (ou erreur), nombre d'appels.</summary>
    private sealed class ReferentielsFactices : ITelechargementReferentiel
    {
        public byte[] Contenu { get; set; } = [0x50, 0x4b, 0x03, 0x04, 1, 2, 3];
        public Exception? Erreur { get; set; }
        public int Appels { get; private set; }
        public Task<byte[]> TelechargerAsync(long fichier, long octets, CancellationToken ct = default)
        {
            Appels++;
            return Erreur is null ? Task.FromResult(Contenu) : Task.FromException<byte[]>(Erreur);
        }
    }

    private sealed class Depot : IOrdresCentrauxDepot
    {
        public NomenclatureEnMemoire Nomenclature { get; } = new();
        public ReferentielsFactices Referentiels { get; } = new();
        public AchatsEnMemoire Achats { get; } = new();
        public readonly List<(long Grossiste, IReadOnlyList<Pharma.Domain.Achats.LigneCommandeSaisie> Lignes, string Auteur, Guid Ordre)> Commandes = [];
        public Task<string> CreerCommandeAsync(long fournisseurId, IReadOnlyList<Pharma.Domain.Achats.LigneCommandeSaisie> lignes, string auteurCentral,
            Guid ordre, CancellationToken ct = default)
        {
            Commandes.Add((fournisseurId, lignes, auteurCentral, ordre));
            return Task.FromResult($"C-{Commandes.Count:000000}");
        }
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
        public ProduitsModifEnMemoire Produits = new();
        public readonly List<(long Produit, decimal? Prix, decimal? Tarif, long? Par)> Prix = [];
        public Task<bool> ModifierPrixAsync(long produitId, decimal? prix, decimal? tarif, decimal prixAttendu, decimal? tarifAttendu, long? par,
            string auteurCentral, Guid ordre, CancellationToken ct = default)
        {
            var f = Produits.Fiche;
            if (f.Id != produitId || f.PrixPublic != prixAttendu || f.TarifReference != tarifAttendu) return Task.FromResult(false);
            Produits.Fiche = f with { PrixPublic = prix ?? f.PrixPublic, TarifReference = tarif ?? f.TarifReference };
            Prix.Add((produitId, prix, tarif, par));
            return Task.FromResult(true);
        }
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
            new ModifierUtilisateur(admin, new HacheurFactice(), journal, droits), new EnregistrerDroits(admin, journal), depot.Produits, depot,
            new Pharma.Application.Catalogue.ImporterNomenclature(new LecteurFactice(200), depot.Nomenclature, droits, journal), depot.Referentiels,
            depot.Achats);
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

    // ---- Lot S5.2 : prix d'un produit (fiche d'essai : produit n° 5, prix 100, tarif 90) ----

    private static OrdreRecu Prix(string donnees, string attendu = """{"prix_public":100.00,"tarif_reference":90.00}""") =>
        Ordre(TypesOrdre.PrixProduit, "5", attendu, donnees);

    [Fact]
    public async Task Prix_dans_l_ecart_de_20_pourcent_applique_tout_de_suite_sans_auteur_local()
    {
        var m = Monter();
        var o = Prix("""{"nom":"Produit","prix_public":110.00}""");
        var tarif = Prix("""{"tarif_reference":99}""");
        m.Depot.Ordres.Add(o);
        await m.Auto.ExecuterAsync();
        Assert.Equal(StatutsOrdre.Applique, m.Depot.Statut(o.Id));
        Assert.Equal((5L, (decimal?)110.00m, (decimal?)null, (long?)null), m.Depot.Prix.Single());
        Assert.Equal(110m, m.Depot.Produits.Fiche.PrixPublic);
        Assert.Empty(m.Alertes.Objets);                                    // appliqué : tracé, sans alerte (S5.2-D3)
        // Tarif seul : comparé à la valeur actuelle (le prix a déjà changé : l'ordre de tarif vu à 100 est refusé)
        m.Depot.Ordres.Add(tarif);
        await m.Auto.ExecuterAsync();
        Assert.Equal(StatutsOrdre.Refuse, m.Depot.Statut(tarif.Id));
        Assert.Contains(ExecuterOrdreCentral.ModifieDansOfficine, m.Depot.Ordres.Single(x => x.Id == tarif.Id).Motif);
    }

    [Fact]
    public async Task Prix_au_dela_de_20_pourcent_confirme_par_le_titulaire_a_son_nom()
    {
        var m = Monter();
        var o = Prix("""{"nom":"Produit","prix_public":130}""");
        m.Depot.Ordres.Add(o);
        await m.Auto.ExecuterAsync();
        Assert.Equal(StatutsOrdre.AConfirmer, m.Depot.Statut(o.Id));
        Assert.Contains("20 %", m.Depot.Ordres.Single(x => x.Id == o.Id).Motif);
        Assert.Empty(m.Depot.Prix);
        await m.Confirmer.ExecuterAsync(o.Id, null, 1);
        Assert.Equal((5L, (decimal?)130m, (decimal?)null, (long?)1L), m.Depot.Prix.Single());
        Assert.Equal(StatutsOrdre.Applique, m.Depot.Statut(o.Id));
    }

    [Fact]
    public async Task Prix_modifie_dans_l_officine_ou_produit_inconnu_refuse()
    {
        var m = Monter();
        var change = Prix("""{"prix_public":105}""", """{"prix_public":95.00,"tarif_reference":90.00}""");
        var inconnu = Ordre(TypesOrdre.PrixProduit, "99", """{"prix_public":10}""", """{"prix_public":11}""");
        m.Depot.Ordres.AddRange([change, inconnu]);
        await m.Auto.ExecuterAsync();
        Assert.Equal(StatutsOrdre.Refuse, m.Depot.Statut(change.Id));
        Assert.Equal(StatutsOrdre.Refuse, m.Depot.Statut(inconnu.Id));
        Assert.Empty(m.Depot.Prix);
        Assert.Equal(100m, m.Depot.Produits.Fiche.PrixPublic);
    }

    [Theory]
    [InlineData(100, 120, false)]
    [InlineData(100, 80, false)]
    [InlineData(100, 120.01, true)]
    [InlineData(100, 79.99, true)]
    [InlineData(0, 10, true)]
    public void Ecart_de_prix_au_dela_de_20_pourcent(decimal actuel, decimal nouveau, bool tropGrand) =>
        Assert.Equal(tropGrand, ExecuterOrdreCentral.EcartTropGrand(actuel, nouveau));

    [Fact]
    public void Ecart_sans_valeur_actuelle_ou_sans_changement()
    {
        Assert.True(ExecuterOrdreCentral.EcartTropGrand(null, 10m));       // premier tarif : confirmation
        Assert.False(ExecuterOrdreCentral.EcartTropGrand(100m, null));     // rien ne change
        Assert.Equal("Produit : prix 100,00 DA → 110,00 DA.",
            ResumeOrdre.Texte(Prix("""{"nom":"Produit","prix_public":110.00}"""), new UtilisateursEnMemoire().Etat).Replace('\u202f', ' ').Replace('\u00a0', ' '));
    }

    // ---- Lot S5.3 : nomenclature nationale ----

    private static OrdreRecu Nomenclature(byte[] contenu, string? empreinte = null) =>
        Ordre(TypesOrdre.Nomenclature, null, "{}", System.Text.Json.JsonSerializer.Serialize(new
        {
            fichier = 7, libelle = "Nomenclature Juin 2026", nom_fichier = "nomenclature.xlsx",
            empreinte = empreinte ?? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(contenu)), octets = contenu.Length
        }));

    [Fact]
    public async Task Nomenclature_attend_le_titulaire_puis_s_importe_a_son_nom()
    {
        var m = Monter();
        var o = Nomenclature(m.Depot.Referentiels.Contenu);
        m.Depot.Ordres.Add(o);
        await m.Auto.ExecuterAsync();
        Assert.Equal(StatutsOrdre.AConfirmer, m.Depot.Statut(o.Id));
        Assert.Equal(0, m.Depot.Referentiels.Appels);                        // rien téléchargé avant la confirmation
        await m.Confirmer.ExecuterAsync(o.Id, null, 1);
        Assert.Equal(StatutsOrdre.Applique, m.Depot.Statut(o.Id));
        Assert.Equal(200, m.Depot.Nomenclature.Recue!.Lignes.Count);
        Assert.Contains("nomenclature_importee", m.Journal.Actions);
    }

    [Fact]
    public async Task Nomenclature_deja_chargee_ne_change_rien()
    {
        var m = Monter();
        var premiere = Nomenclature(m.Depot.Referentiels.Contenu);
        var seconde = Nomenclature(m.Depot.Referentiels.Contenu);
        m.Depot.Ordres.AddRange([premiere, seconde]);
        await m.Auto.ExecuterAsync();
        await m.Confirmer.ExecuterAsync(premiere.Id, null, 1);
        await m.Confirmer.ExecuterAsync(seconde.Id, null, 1);
        Assert.Equal(StatutsOrdre.Applique, m.Depot.Statut(seconde.Id));
        Assert.Single(m.Journal.Actions, "nomenclature_importee");
        Assert.Contains("nomenclature_deja_a_jour", m.Journal.Actions);
    }

    [Fact]
    public async Task Nomenclature_empreinte_differente_ou_fichier_inconnu_refuse()
    {
        var m = Monter();
        var faux = Nomenclature(m.Depot.Referentiels.Contenu, new string('0', 64));
        m.Depot.Ordres.Add(faux);
        await m.Auto.ExecuterAsync();
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => m.Confirmer.ExecuterAsync(faux.Id, null, 1));
        Assert.Contains("empreinte", e.Message);
        Assert.Equal(StatutsOrdre.Refuse, m.Depot.Statut(faux.Id));
        Assert.Null(m.Depot.Nomenclature.Recue);

        var inconnu = Nomenclature(m.Depot.Referentiels.Contenu);
        m.Depot.Ordres.Add(inconnu);
        await m.Auto.ExecuterAsync();
        m.Depot.Referentiels.Erreur = new ServeurCentralException("serveur central : 404", 404);
        await Assert.ThrowsAsync<RegleMetierException>(() => m.Confirmer.ExecuterAsync(inconnu.Id, null, 1));
        Assert.Equal(StatutsOrdre.Refuse, m.Depot.Statut(inconnu.Id));
    }

    [Fact]
    public async Task Nomenclature_central_injoignable_reste_a_confirmer()
    {
        var m = Monter();
        var o = Nomenclature(m.Depot.Referentiels.Contenu);
        m.Depot.Ordres.Add(o);
        await m.Auto.ExecuterAsync();
        m.Depot.Referentiels.Erreur = new ServeurCentralException("délai dépassé");
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => m.Confirmer.ExecuterAsync(o.Id, null, 1));
        Assert.Contains("reste à confirmer", e.Message);
        Assert.Equal(StatutsOrdre.AConfirmer, m.Depot.Statut(o.Id));
        m.Depot.Referentiels.Erreur = null;                                   // nouvel essai : importé
        await m.Confirmer.ExecuterAsync(o.Id, null, 1);
        Assert.Equal(StatutsOrdre.Applique, m.Depot.Statut(o.Id));
    }

    [Fact]
    public void Nomenclature_resume_lisible()
    {
        var o = Nomenclature(new byte[3 * 1048576]);
        var texte = ResumeOrdre.Texte(o, new EtatDroits([], [], []));
        Assert.Contains("« Nomenclature Juin 2026 » (3,0 Mo)", texte);
        Assert.Contains("à vérifier", texte);
    }

    // ---- Lot S5.4 : commandes en brouillon ----

    private static OrdreRecu Commande(string grossiste, params object[] lignes) =>
        Ordre(TypesOrdre.Commande, grossiste, "{}", System.Text.Json.JsonSerializer.Serialize(new { grossiste = "Grossiste 1", lignes }));

    [Fact]
    public async Task Commande_creee_en_brouillon_sans_confirmation_et_titulaire_alerte()
    {
        var m = Monter();
        m.Depot.Achats.Dernier = new Pharma.Application.Achats.DernierAchat(80m, 1);
        var o = Commande("1", new { produit = 3, nom = "Produit 3", quantite = 5, prix_achat_ht = 120.5m }, new { produit = 4, nom = "Produit 4", quantite = 2 });
        m.Depot.Ordres.Add(o);
        await m.Auto.ExecuterAsync();
        Assert.Equal(StatutsOrdre.Applique, m.Depot.Statut(o.Id));
        var c = Assert.Single(m.Depot.Commandes);
        Assert.Equal((1L, "admin.essai", o.Id), (c.Grossiste, c.Auteur, c.Ordre));
        Assert.Equal([(3L, 5, 120.5m), (4L, 2, 80m)], c.Lignes.Select(l => (l.ProduitId, l.Quantite, l.PrixAchatHt)));  // sans prix : dernier achat
        Assert.Contains(m.Alertes.Objets, a => a.Contains("commande préparée"));
    }

    [Fact]
    public async Task Commande_une_ligne_refusee_refuse_tout_l_ordre_avec_chaque_motif()
    {
        var m = Monter();
        m.Depot.Achats.BloquesAchat.Add(5);
        var o = Commande("1", new { produit = 99, quantite = 1 }, new { produit = 5, quantite = 1, prix_achat_ht = 10m }, new { produit = 6, quantite = 1 },
            new { produit = 7, quantite = 1, prix_achat_ht = 10m });
        m.Depot.Ordres.Add(o);
        await m.Auto.ExecuterAsync();
        Assert.Equal(StatutsOrdre.Refuse, m.Depot.Statut(o.Id));
        Assert.Empty(m.Depot.Commandes);
        var motif = m.Depot.Ordres.Single().Motif!;
        Assert.Contains("produit n° 99 introuvable", motif);
        Assert.Contains("Produit 5 bloqué à l’achat", motif);
        Assert.Contains("Produit 6 : aucun achat précédent", motif);
        Assert.DoesNotContain("Produit 7", motif);
    }

    [Fact]
    public async Task Commande_grossiste_inconnu_trop_de_lignes_ou_doublon_refuses()
    {
        var m = Monter();
        var inconnu = Commande("8", new { produit = 3, quantite = 1, prix_achat_ht = 1m });
        var trop = Commande("1", Enumerable.Range(1, ExecuterOrdreCentral.LignesCommandeMax + 1).Select(i => (object)new { produit = i, quantite = 1, prix_achat_ht = 1m }).ToArray());
        var doublon = Commande("1", new { produit = 3, quantite = 1, prix_achat_ht = 1m }, new { produit = 3, quantite = 2, prix_achat_ht = 1m });
        var illisible = Ordre(TypesOrdre.Commande, "1", "{}", """{"lignes":[{"produit":"trois","quantite":1}]}""");
        m.Depot.Ordres.AddRange([inconnu, trop, doublon, illisible]);
        await m.Auto.ExecuterAsync();
        Assert.All(m.Depot.Ordres, o => Assert.Equal(StatutsOrdre.Refuse, o.Statut));
        Assert.Contains("Grossiste n° 8 introuvable", m.Depot.Ordres[0].Motif);
        Assert.Contains("de 1 à 200 lignes", m.Depot.Ordres[1].Motif);
        Assert.Contains("deux lignes", m.Depot.Ordres[2].Motif);
        Assert.Contains("mal formée", m.Depot.Ordres[3].Motif);
        Assert.Empty(m.Depot.Commandes);
    }

    [Fact]
    public void Commande_resume_lisible()
    {
        var o = Commande("1", new { produit = 3, nom = "Doliprane", quantite = 5 }, new { produit = 4, nom = "Augmentin", quantite = 2 },
            new { produit = 5, nom = "Smecta", quantite = 1 }, new { produit = 6, nom = "Spasfon", quantite = 3 });
        var texte = ResumeOrdre.Texte(o, new EtatDroits([], [], []));
        Assert.Equal("Bon en brouillon chez Grossiste 1 : 4 ligne(s) (Doliprane × 5, Augmentin × 2, Smecta × 1, …). Envoyé au grossiste depuis l’officine seulement.", texte);
        Assert.False(TypesOrdre.ConfirmationRequise(TypesOrdre.Commande));
    }
}
