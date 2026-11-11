using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Patients;
using Pharma.Application.Securite;
using Pharma.Domain.Caisse;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;
using Pharma.Domain.Stock;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

internal sealed class DroitsFactices(params string[] droits) : IPermissionService
{
    public decimal RemiseMax { get; init; } = 10;
    public Task<bool> APermissionAsync(long u, string p, CancellationToken ct = default) => Task.FromResult(droits.Contains(p));
    public Task<IReadOnlyList<string>> ListerAsync(long u, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(droits);
    public Task<decimal> RemiseMaxAsync(long u, CancellationToken ct = default) => Task.FromResult(RemiseMax);
    public bool Titulaire { get; init; }
    public Task<bool> EstTitulaireAsync(long u, CancellationToken ct = default) => Task.FromResult(Titulaire);
}

internal sealed class CatalogueEnMemoire : IPreparationVente
{
    public static readonly DateOnly Aujourdhui = new(2026, 11, 8);

    public static readonly Produit Amoxicilline = new() { Id = 2, NomCommercial = "Amoxicilline", PrixPublic = 350, Remboursable = true, TarifReference = 350 };
    public static readonly Produit Alprazolam = new() { Id = 7, NomCommercial = "Alprazolam", PrixPublic = 190, Classe = ClasseReglementaire.Psychotrope };
    public static readonly Produit Sintrom = new() { Id = 9, NomCommercial = "Sintrom", PrixPublic = 280, Classe = ClasseReglementaire.ListeI };

    private readonly Dictionary<long, Produit> produits = new() { [2] = Amoxicilline, [7] = Alprazolam, [9] = Sintrom };
    private readonly Dictionary<long, Lot> lots = new()
    {
        [20] = new Lot { Id = 20, ProduitId = 2, NumeroLot = "B1182", QuantiteDisponible = 20, DatePeremption = Aujourdhui.AddDays(140) },
        [70] = new Lot { Id = 70, ProduitId = 7, NumeroLot = "AL25", QuantiteDisponible = 14, DatePeremption = Aujourdhui.AddDays(330) },
        [90] = new Lot { Id = 90, ProduitId = 9, NumeroLot = "SI40", QuantiteDisponible = 30, DatePeremption = Aujourdhui.AddDays(400) }
    };

    public Task<IReadOnlyDictionary<long, Produit>> ProduitsAsync(IEnumerable<long> ids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<long, Produit>>(produits.Where(p => ids.Contains(p.Key)).ToDictionary());
    public Task<IReadOnlyDictionary<long, Lot>> LotsAsync(IEnumerable<long> ids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<long, Lot>>(lots.Where(l => ids.Contains(l.Key)).ToDictionary());
}

internal sealed class PatientsEnMemoire(params Patient[] liste) : IPatients
{
    public Task<IReadOnlyList<PatientResume>> RechercherAsync(string t, int l, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PatientResume>>([]);
    public Task<Patient?> TrouverAsync(long id, CancellationToken ct = default) => Task.FromResult(liste.FirstOrDefault(p => p.Id == id));
    public Task<long> CreerAsync(NouveauPatient p, CancellationToken ct = default) => Task.FromResult(99L);
    public FichePatient? Fiche { get; set; }
    public FichePatient? Modifiee { get; private set; }
    public Task<FichePatient?> FicheAsync(long id, CancellationToken ct = default) => Task.FromResult(Fiche?.Id == id ? Fiche : null);
    public Task<bool> ModifierAsync(FichePatient f, CancellationToken ct = default) { Modifiee = f; return Task.FromResult(Fiche?.Id == f.Id); }
}

public class ModifierPatientTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 11, 10);
    private static FichePatient Fiche() => new(7, "Benali", "Karim", null, "0550 12 34 56", null, "123", 1, false, 5000, 1200);

    [Fact]
    public async Task Modification_enregistree_et_journalisee_champ_par_champ_sans_toucher_a_la_dette()
    {
        var patients = new PatientsEnMemoire { Fiche = Fiche() };
        var journal = new JournalEspion();
        await new ModifierPatient(patients, journal, new HorlogeFixe(Aujourdhui))
            .ExecuterAsync(Fiche() with { Nom = " Benali-Saïd ", PlafondCredit = 8000, SoldeCredit = 0, OrganismeId = 0 }, 3);
        var m = patients.Modifiee!;
        Assert.Equal(("Benali-Saïd", 8000m, 1200m, (long?)null), (m.Nom, m.PlafondCredit, m.SoldeCredit, m.OrganismeId));
        Assert.Equal("0550 12 34 56", m.Telephone);   // téléphone inchangé : gardé tel quel
        Assert.Equal(["patient_modifie"], journal.Actions);
        Assert.Equal(["nom", "organisme_id", "plafond_credit"], journal.Details[0].Keys.Order());
    }

    [Theory]
    [InlineData(" ", 0, 0, "nom_patient")]
    [InlineData("X", -1, 0, "plafond")]
    [InlineData("X", 0, 1, "date_naissance")]
    public async Task Saisie_refusee(string nom, decimal plafond, int joursNaissance, string champ)
    {
        var patients = new PatientsEnMemoire { Fiche = Fiche() };
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => new ModifierPatient(patients, new JournalEspion(), new HorlogeFixe(Aujourdhui))
            .ExecuterAsync(Fiche() with { Nom = nom, PlafondCredit = plafond, DateNaissance = joursNaissance > 0 ? Aujourdhui.AddDays(joursNaissance) : null }, 3));
        Assert.Equal(champ, ex.Champ);
        Assert.Null(patients.Modifiee);
    }

    [Fact]
    public async Task Patient_inconnu_refuse() =>
        await Assert.ThrowsAsync<RegleMetierException>(() => new ModifierPatient(new PatientsEnMemoire(), new JournalEspion(), new HorlogeFixe(Aujourdhui))
            .ExecuterAsync(Fiche(), 3));
}

internal sealed class VentesEspion : IVentesEcriture
{
    public VenteAEnregistrer? Recu { get; private set; }
    public Task<(long Id, string Numero)> EnregistrerAsync(VenteAEnregistrer vente, CancellationToken ct = default)
    {
        Recu = vente;
        return Task.FromResult((1L, "T-000001"));
    }
}

internal sealed class SessionsEnMemoire : ISessionsCaisse
{
    public SessionCaisse? Ouverte { get; set; } = new() { Id = 1, UtilisateurId = 5, FondCaisse = 5000, Poste = "Caisse 1", OuvertePar = "Vendeur A" };
    public decimal Especes { get; set; }
    public ParametresCaisse Parametres { get; set; } = new(true, 5000, 500, 0);
    public ResultatCloture? Cloture { get; private set; }
    public List<(long Utilisateur, string Poste, decimal Fond, long? ReleveDe)> Ouvertures { get; } = [];
    public List<Vendeur> Vendeurs { get; set; } = [];

    /// <summary>Une caisse par poste : seule celle de « Ouverte.Poste » existe.</summary>
    public Task<SessionCaisse?> SessionDuPosteAsync(string poste, CancellationToken ct = default) =>
        Task.FromResult(Ouverte?.Poste == poste && Cloture is null ? Ouverte : null);
    public Task<long> OuvrirAsync(long u, string poste, decimal fond, long? releveDe = null, CancellationToken ct = default)
    {
        Ouvertures.Add((u, poste, fond, releveDe));
        return Task.FromResult(2L);
    }
    public Task<IReadOnlyList<Vendeur>> VendeursAsync(long s, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Vendeur>>(Vendeurs);
    public Task<decimal> VentesEspecesAsync(long s, CancellationToken ct = default) => Task.FromResult(Especes);
    public Task CloturerAsync(long s, Comptage c, ResultatCloture r, CancellationToken ct = default) { Cloture = r; return Task.CompletedTask; }
    public Task<DetailSession?> DetailAsync(long s, CancellationToken ct = default) => Task.FromResult<DetailSession?>(null);
    public Task<ParametresCaisse> ParametresAsync(CancellationToken ct = default) => Task.FromResult(Parametres);
    public Task ModifierParametresAsync(ParametresCaisse p, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class JournalEspion : IJournal
{
    public List<string> Actions { get; } = [];
    public List<IReadOnlyDictionary<string, object?>> Details { get; } = [];
    public Task EcrireAsync(long u, string action, string? t, long? id, IReadOnlyDictionary<string, object?> d, string? p, CancellationToken ct = default)
    {
        Actions.Add(action);
        Details.Add(d);
        return Task.CompletedTask;
    }
}

public class CaisseApplicationTests
{
    private static Encaisser Cas(SessionsEnMemoire sessions, DroitsFactices droits, VentesEspion ventes, params Patient[] patients) =>
        new(new CatalogueEnMemoire(), new PatientsEnMemoire(patients), ventes, sessions, droits, new HorlogeFixe(CatalogueEnMemoire.Aujourdhui), new ScansEnMemoire());

    private static CommandeVente Commande(long produitId, long lotId, int qte = 1, ModePaiement mode = ModePaiement.Especes,
        long? patientId = null, OrdonnanceSaisie? ordonnance = null, decimal remise = 0) =>
        new([new LigneCommande(produitId, lotId, qte)], false, null, null, patientId, ordonnance, remise, mode, null);

    [Fact]
    public async Task Pas_d_encaissement_sans_caisse_ouverte()
    {
        var sessions = new SessionsEnMemoire { Ouverte = null };
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() =>
            Cas(sessions, new DroitsFactices("caisse.vendre"), new VentesEspion()).ExecuterAsync(Commande(2, 20), 5, "Caisse 1"));
        Assert.Contains("Ouvrez la caisse", ex.Message);
    }

    [Fact]
    public async Task Vente_enregistree_avec_la_session()
    {
        var ventes = new VentesEspion();
        var r = await Cas(new SessionsEnMemoire(), new DroitsFactices("caisse.vendre"), ventes).ExecuterAsync(Commande(2, 20, qte: 2), 5, "Caisse 1");

        Assert.Equal("T-000001", r.Numero);
        Assert.Equal(1L, ventes.Recu!.Vente.SessionCaisseId);
        Assert.Equal(StatutVente.Validee, ventes.Recu.Vente.Statut);
        Assert.Empty(ventes.Recu.ProduitsSousRegistre);
    }

    [Fact]
    public async Task Psychotrope_sans_le_droit_refuse()
    {
        var ordonnance = new OrdonnanceSaisie(1, CatalogueEnMemoire.Aujourdhui, null);
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            Cas(new SessionsEnMemoire(), new DroitsFactices("caisse.vendre"), new VentesEspion())
                .ExecuterAsync(Commande(7, 70, patientId: 3, ordonnance: ordonnance), 5, "Caisse 1"));
    }

    [Fact]
    public async Task Psychotrope_sans_le_droit_nomme_les_personnes_qui_peuvent_delivrer()
    {
        var ordonnance = new OrdonnanceSaisie(1, CatalogueEnMemoire.Aujourdhui, null);
        var cas = new Encaisser(new CatalogueEnMemoire(), new PatientsEnMemoire(), new VentesEspion(), new SessionsEnMemoire(),
            new DroitsFactices("caisse.vendre"), new HorlogeFixe(CatalogueEnMemoire.Aujourdhui), new ScansEnMemoire(),
            new DetenteursFactices(new DetenteurDroit("Dr Assistant Fictif", true), new DetenteurDroit("Dr Titulaire Fictif", false)));
        var e = await Assert.ThrowsAsync<DroitRefuseException>(() => cas.ExecuterAsync(Commande(7, 70, patientId: 3, ordonnance: ordonnance), 5, "Caisse 1"));
        Assert.Contains("Dr Assistant Fictif (code comptoir), Dr Titulaire Fictif", e.Message);
    }

    private sealed class DetenteursFactices(params DetenteurDroit[] d) : IDetenteursDroit
    {
        public Task<IReadOnlyList<DetenteurDroit>> ListerAsync(string permission, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DetenteurDroit>>(d);
    }

    [Fact]
    public async Task Psychotrope_avec_droit_inscrit_au_registre()
    {
        var ventes = new VentesEspion();
        var ordonnance = new OrdonnanceSaisie(1, CatalogueEnMemoire.Aujourdhui, "ORD-1");
        await Cas(new SessionsEnMemoire(), new DroitsFactices("caisse.vendre", "psychotropes.delivrer"), ventes)
            .ExecuterAsync(Commande(7, 70, patientId: 3, ordonnance: ordonnance), 5, "Caisse 1");
        Assert.Contains(7L, ventes.Recu!.ProduitsSousRegistre);
        Assert.Equal(ordonnance, ventes.Recu.Ordonnance);
    }

    [Fact]
    public async Task Liste_I_sans_ordonnance_refusee_sans_le_droit_de_vendre_sans_ordonnance()
    {
        var e = await Assert.ThrowsAsync<DroitRefuseException>(() =>
            Cas(new SessionsEnMemoire(), new DroitsFactices("caisse.vendre"), new VentesEspion()).ExecuterAsync(Commande(9, 90), 5, "Caisse 1"));
        Assert.Equal("caisse.vendre_sans_ordonnance", e.Permission);
    }

    [Fact]
    public async Task Liste_I_avec_ordonnance_vendue_sans_le_droit()
    {
        var ventes = new VentesEspion();
        var ordonnance = new OrdonnanceSaisie(1, CatalogueEnMemoire.Aujourdhui, null);
        await Cas(new SessionsEnMemoire(), new DroitsFactices("caisse.vendre"), ventes)
            .ExecuterAsync(Commande(9, 90, patientId: 3, ordonnance: ordonnance), 5, "Caisse 1");
        Assert.Equal(ordonnance, ventes.Recu!.Ordonnance);
    }

    [Fact]
    public async Task Liste_I_sans_ordonnance_vendue_avec_le_droit_et_inscrite_au_journal()
    {
        var journal = new JournalEspion();
        var cas = new Encaisser(new CatalogueEnMemoire(), new PatientsEnMemoire(), new VentesEspion(), new SessionsEnMemoire(),
            new DroitsFactices("caisse.vendre", "caisse.vendre_sans_ordonnance"), new HorlogeFixe(CatalogueEnMemoire.Aujourdhui), new ScansEnMemoire(),
            journal: journal);
        await cas.ExecuterAsync(Commande(9, 90), 5, "Caisse 1");
        Assert.Contains("vente_sans_ordonnance", journal.Actions);
    }

    [Fact]
    public async Task Psychotrope_jamais_sans_ordonnance_meme_avec_le_droit_de_vendre_sans_ordonnance() =>
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            Cas(new SessionsEnMemoire(), new DroitsFactices("caisse.vendre", "psychotropes.delivrer", "caisse.vendre_sans_ordonnance"), new VentesEspion())
                .ExecuterAsync(Commande(7, 70, patientId: 3), 5, "Caisse 1"));

    [Fact]
    public async Task Apercu_signale_un_medicament_de_liste()
    {
        var a = await Cas(new SessionsEnMemoire(), new DroitsFactices("caisse.vendre"), new VentesEspion()).CalculerAsync(Commande(9, 90), 5);
        Assert.Equal((true, false), (a.SurOrdonnance, a.OrdonnanceRequise));
    }

    [Fact]
    public async Task Boites_identifiees_scannees_rattachees_a_la_vente()
    {
        var ventes = new VentesEspion();
        var cas = Cas(new SessionsEnMemoire(), new DroitsFactices("caisse.vendre"), ventes);
        CommandeVente Avec(int qte, params long[] boites) =>
            new([new LigneCommande(2, 20, qte, boites)], false, null, null, null, null, 0, ModePaiement.Especes, null);

        await cas.ExecuterAsync(Avec(2, 100, 101), 5, "Caisse 1");
        Assert.Equal([100L, 101L], ventes.Recu!.BoitesParLot![20]);
        // Même boîte deux fois, ou plus de boîtes que de boîtes vendues : refusé
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(Avec(2, 100, 100), 5, "Caisse 1"));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(Avec(1, 100, 101), 5, "Caisse 1"));
    }

    [Fact]
    public async Task Remise_sans_le_droit_refusee() =>
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            Cas(new SessionsEnMemoire(), new DroitsFactices("caisse.vendre"), new VentesEspion()).ExecuterAsync(Commande(2, 20, remise: 5), 5, "Caisse 1"));

    [Fact]
    public async Task Credit_sans_patient_refuse() =>
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            Cas(new SessionsEnMemoire(), new DroitsFactices("caisse.vendre", "credit.accorder"), new VentesEspion())
                .ExecuterAsync(Commande(2, 20, mode: ModePaiement.Credit), 5, "Caisse 1"));

    [Fact]
    public async Task Credit_au_dela_du_plafond_selon_le_droit()
    {
        var patient = new Patient { Id = 3, Nom = "Fictif", PlafondCredit = 1000, SoldeCredit = 800 };
        var commande = Commande(2, 20, mode: ModePaiement.Credit, patientId: 3);   // 350 DA : 1 150 > 1 000

        // Refus de droit (« dépasser le plafond ») : un employé habilité peut l'autoriser sur place avec son code
        var refus = await Assert.ThrowsAsync<DroitRefuseException>(() =>
            Cas(new SessionsEnMemoire(), new DroitsFactices("caisse.vendre", "credit.accorder"), new VentesEspion(), patient)
                .ExecuterAsync(commande, 5, "Caisse 1"));
        Assert.Equal("credit.depasser_plafond", refus.Permission);
        Assert.StartsWith("Plafond de crédit dépassé", refus.Message);

        var ventes = new VentesEspion();
        var patient2 = new Patient { Id = 3, Nom = "Fictif", PlafondCredit = 1000, SoldeCredit = 800 };
        await Cas(new SessionsEnMemoire(), new DroitsFactices("caisse.vendre", "credit.accorder", "credit.depasser_plafond"), ventes, patient2)
            .ExecuterAsync(commande, 5, "Caisse 1");
        Assert.True(ventes.Recu!.CreditAccorde);
        Assert.Equal(ModePaiement.Credit, ventes.Recu.Reglement.Mode);
    }

    [Fact]
    public async Task Apercu_signale_l_ordonnance_requise()
    {
        var a = await Cas(new SessionsEnMemoire(), new DroitsFactices(), new VentesEspion()).CalculerAsync(Commande(7, 70), 5);
        Assert.True(a.OrdonnanceRequise);
        Assert.Equal(190m, a.PartPatient);
    }

    [Fact]
    public async Task Cloture_sans_droit_ne_montre_que_le_compte_mais_journalise_l_ecart()
    {
        var sessions = new SessionsEnMemoire { Especes = 45000 };
        var journal = new JournalEspion();
        var vue = await new CloturerSessionCaisse(sessions, new DroitsFactices("caisse.vendre"), journal)
            .ExecuterAsync(5, "Caisse 1", new Dictionary<string, decimal> { ["b2000"] = 24, ["b500"] = 1 });

        Assert.Equal(48500m, vue.Compte);
        Assert.Null(vue.Attendu);
        Assert.Null(vue.Ecart);
        Assert.Equal(-1500m, sessions.Cloture!.Ecart);
        Assert.Equal(["ecart_caisse"], journal.Actions);
    }

    [Fact]
    public async Task Cloture_avec_droit_montre_l_ecart()
    {
        var vue = await new CloturerSessionCaisse(new SessionsEnMemoire { Especes = 1000 }, new DroitsFactices("caisse.voir_ecart"), new JournalEspion())
            .ExecuterAsync(5, "Caisse 1", new Dictionary<string, decimal> { ["b2000"] = 3 });
        Assert.Equal(6000m, vue.Attendu);
        Assert.Equal(0m, vue.Ecart);
        Assert.False(vue.AuDelaDuSeuil);
    }

    [Fact]
    public async Task Deuxieme_ouverture_refusee() =>
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            new OuvrirSessionCaisse(new SessionsEnMemoire()).ExecuterAsync(5, "Caisse 1", 5000));

    // ---- Caisse partagée par poste ----

    [Fact]
    public async Task Un_collegue_ne_peut_pas_ouvrir_une_seconde_caisse_sur_le_meme_poste()
    {
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() =>
            new OuvrirSessionCaisse(new SessionsEnMemoire()).ExecuterAsync(9, "Caisse 1", 5000));
        Assert.Contains("Vendeur A", ex.Message);
    }

    [Fact]
    public async Task Un_autre_poste_ouvre_sa_propre_caisse()
    {
        var sessions = new SessionsEnMemoire();
        await new OuvrirSessionCaisse(sessions).ExecuterAsync(9, "Caisse 2", 3000);
        Assert.Equal((9L, "Caisse 2", 3000m, (long?)null), sessions.Ouvertures.Single());
    }

    [Fact]
    public async Task Sans_nom_de_poste_pas_de_caisse() =>
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            Cas(new SessionsEnMemoire(), new DroitsFactices("caisse.vendre"), new VentesEspion()).ExecuterAsync(Commande(2, 20), 5, " "));

    [Fact]
    public async Task Un_collegue_vend_sur_la_caisse_du_poste_a_son_nom()
    {
        var ventes = new VentesEspion();
        // Caisse ouverte par l'employé 5 ; l'employé 9 vend dessus
        await Cas(new SessionsEnMemoire(), new DroitsFactices("caisse.vendre"), ventes).ExecuterAsync(Commande(2, 20), 9, "Caisse 1");
        Assert.Equal(9, ventes.Recu!.Vente.UtilisateurId);
        Assert.Equal(1, ventes.Recu.Vente.SessionCaisseId);
    }

    [Fact]
    public async Task Pas_de_vente_sur_un_poste_dont_la_caisse_est_fermee() =>
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            Cas(new SessionsEnMemoire(), new DroitsFactices("caisse.vendre"), new VentesEspion()).ExecuterAsync(Commande(2, 20), 5, "Caisse 2"));

    [Fact]
    public async Task Releve_cloture_avec_le_comptage_et_rouvre_avec_l_argent_compte()
    {
        var sessions = new SessionsEnMemoire { Especes = 1000 };
        var journal = new JournalEspion();
        var cloturer = new CloturerSessionCaisse(sessions, new DroitsFactices("caisse.vendre"), journal);
        var r = await new ReleverCaisse(sessions, cloturer, journal)
            .ExecuterAsync(9, "Caisse 1", new Dictionary<string, decimal> { ["b2000"] = 3 });
        Assert.Equal(6000m, r.Cloture.Compte);
        Assert.NotNull(sessions.Cloture);
        Assert.Equal((9L, "Caisse 1", 6000m, (long?)1), sessions.Ouvertures.Single());
        Assert.Contains("releve_caisse", journal.Actions);
    }
}
