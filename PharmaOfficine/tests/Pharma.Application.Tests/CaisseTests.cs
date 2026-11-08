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

    private readonly Dictionary<long, Produit> produits = new() { [2] = Amoxicilline, [7] = Alprazolam };
    private readonly Dictionary<long, Lot> lots = new()
    {
        [20] = new Lot { Id = 20, ProduitId = 2, NumeroLot = "B1182", QuantiteDisponible = 20, DatePeremption = Aujourdhui.AddDays(140) },
        [70] = new Lot { Id = 70, ProduitId = 7, NumeroLot = "AL25", QuantiteDisponible = 14, DatePeremption = Aujourdhui.AddDays(330) }
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
    public SessionCaisse? Ouverte { get; set; } = new() { Id = 1, UtilisateurId = 5, FondCaisse = 5000, Poste = "Caisse 1" };
    public decimal Especes { get; set; }
    public ParametresCaisse Parametres { get; set; } = new(true, 5000, 500, 0);
    public ResultatCloture? Cloture { get; private set; }

    public Task<SessionCaisse?> SessionOuverteAsync(long u, CancellationToken ct = default) => Task.FromResult(Ouverte);
    public Task<long> OuvrirAsync(long u, string poste, decimal fond, CancellationToken ct = default) => Task.FromResult(2L);
    public Task<decimal> VentesEspecesAsync(long s, CancellationToken ct = default) => Task.FromResult(Especes);
    public Task CloturerAsync(long s, Comptage c, ResultatCloture r, CancellationToken ct = default) { Cloture = r; return Task.CompletedTask; }
    public Task<DetailSession?> DetailAsync(long s, CancellationToken ct = default) => Task.FromResult<DetailSession?>(null);
    public Task<ParametresCaisse> ParametresAsync(CancellationToken ct = default) => Task.FromResult(Parametres);
    public Task ModifierParametresAsync(ParametresCaisse p, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class JournalEspion : IJournal
{
    public List<string> Actions { get; } = [];
    public Task EcrireAsync(long u, string action, string? t, long? id, IReadOnlyDictionary<string, object?> d, string? p, CancellationToken ct = default)
    {
        Actions.Add(action);
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
            Cas(sessions, new DroitsFactices("caisse.vendre"), new VentesEspion()).ExecuterAsync(Commande(2, 20), 5));
        Assert.Contains("Ouvrez la caisse", ex.Message);
    }

    [Fact]
    public async Task Vente_enregistree_avec_la_session()
    {
        var ventes = new VentesEspion();
        var r = await Cas(new SessionsEnMemoire(), new DroitsFactices("caisse.vendre"), ventes).ExecuterAsync(Commande(2, 20, qte: 2), 5);

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
                .ExecuterAsync(Commande(7, 70, patientId: 3, ordonnance: ordonnance), 5));
    }

    [Fact]
    public async Task Psychotrope_avec_droit_inscrit_au_registre()
    {
        var ventes = new VentesEspion();
        var ordonnance = new OrdonnanceSaisie(1, CatalogueEnMemoire.Aujourdhui, "ORD-1");
        await Cas(new SessionsEnMemoire(), new DroitsFactices("caisse.vendre", "psychotropes.delivrer"), ventes)
            .ExecuterAsync(Commande(7, 70, patientId: 3, ordonnance: ordonnance), 5);
        Assert.Contains(7L, ventes.Recu!.ProduitsSousRegistre);
        Assert.Equal(ordonnance, ventes.Recu.Ordonnance);
    }

    [Fact]
    public async Task Remise_sans_le_droit_refusee() =>
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            Cas(new SessionsEnMemoire(), new DroitsFactices("caisse.vendre"), new VentesEspion()).ExecuterAsync(Commande(2, 20, remise: 5), 5));

    [Fact]
    public async Task Credit_sans_patient_refuse() =>
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            Cas(new SessionsEnMemoire(), new DroitsFactices("caisse.vendre", "credit.accorder"), new VentesEspion())
                .ExecuterAsync(Commande(2, 20, mode: ModePaiement.Credit), 5));

    [Fact]
    public async Task Credit_au_dela_du_plafond_selon_le_droit()
    {
        var patient = new Patient { Id = 3, Nom = "Fictif", PlafondCredit = 1000, SoldeCredit = 800 };
        var commande = Commande(2, 20, mode: ModePaiement.Credit, patientId: 3);   // 350 DA : 1 150 > 1 000

        await Assert.ThrowsAsync<RegleMetierException>(() =>
            Cas(new SessionsEnMemoire(), new DroitsFactices("caisse.vendre", "credit.accorder"), new VentesEspion(), patient)
                .ExecuterAsync(commande, 5));

        var ventes = new VentesEspion();
        var patient2 = new Patient { Id = 3, Nom = "Fictif", PlafondCredit = 1000, SoldeCredit = 800 };
        await Cas(new SessionsEnMemoire(), new DroitsFactices("caisse.vendre", "credit.accorder", "credit.depasser_plafond"), ventes, patient2)
            .ExecuterAsync(commande, 5);
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
            .ExecuterAsync(5, new Dictionary<string, decimal> { ["b2000"] = 24, ["b500"] = 1 });

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
            .ExecuterAsync(5, new Dictionary<string, decimal> { ["b2000"] = 3 });
        Assert.Equal(6000m, vue.Attendu);
        Assert.Equal(0m, vue.Ecart);
        Assert.False(vue.AuDelaDuSeuil);
    }

    [Fact]
    public async Task Deuxieme_ouverture_refusee() =>
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            new OuvrirSessionCaisse(new SessionsEnMemoire()).ExecuterAsync(5, "Caisse 1", 5000));
}
