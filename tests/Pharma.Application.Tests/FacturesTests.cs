using Pharma.Application.Commun;
using Pharma.Application.Factures;
using Pharma.Domain.Commun;
using Pharma.Domain.Factures;

namespace Pharma.Application.Tests;

internal sealed class FacturesEnMemoire : IFactures
{
    public List<LigneVenteSource> Lignes { get; } =
    [
        // Vente 1 : patient 3, CNAS (organisme 1) ; vente 2 : patient 4, sans organisme
        new(new LigneVendue(1, "T-1", 10, "Produit A", 0, 2, 100, 400, 0), DateTime.UtcNow, 3, "Patient fictif", 1, 50, "ORD-1", false),
        new(new LigneVendue(2, "T-2", 20, "Produit B", 0, 1, 250, 0, 0), DateTime.UtcNow, 4, "Autre patient", null, null, null, false)
    ];
    public List<FactureAEcrire> Emises { get; } = [];
    public long? Remplacee { get; private set; }
    public FactureDetail? Detail { get; set; }
    public (long Id, bool Hors, string? Motif)? Suivi { get; private set; }

    public Task<IReadOnlyList<LigneVenteSource>> CandidatesAsync(string part, long? p, long? o, DateOnly du, DateOnly au, string? q, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LigneVenteSource>>(Lignes);
    public Task<IReadOnlyList<LigneVenteSource>> LignesDesVentesAsync(IReadOnlyList<long> ventes, string part, long? sauf, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LigneVenteSource>>(Lignes.Where(l => ventes.Contains(l.Ligne.VenteId)).ToList());
    public Task<decimal> PayeAuComptoirAsync(IReadOnlyList<long> ventes, CancellationToken ct = default) => Task.FromResult(100m);
    public Task<(string Nom, string? Infos)?> DestinataireAsync(Destinataire d, long id, CancellationToken ct = default) =>
        Task.FromResult<(string, string?)?>(("Destinataire fictif", null));
    public Task<(long Id, string Numero)> EmettreAsync(FactureAEcrire f, long? remplaceId, CancellationToken ct = default)
    {
        Emises.Add(f);
        Remplacee = remplaceId;
        return Task.FromResult((100L + Emises.Count, $"F-2026-{Emises.Count:0000}"));
    }
    public Task<IReadOnlyList<FactureResume>> ListerAsync(FiltreFactures f, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FactureResume>>([]);
    public Task<FactureDetail?> DetailAsync(long id, int? v, CancellationToken ct = default) => Task.FromResult(Detail);
    public Task RejeterAsync(long id, string motif, CancellationToken ct = default) => Task.CompletedTask;
    public Task CorrigerAsync(long id, FactureAEcrire f, string? motif, CancellationToken ct = default) { Emises.Add(f); return Task.CompletedTask; }
    public Task HorsSuiviAsync(long id, bool h, string? m, CancellationToken ct = default) { Suivi = (id, h, m); return Task.CompletedTask; }
    public Task<IReadOnlyList<ClientFacture>> ClientsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ClientFacture>>([]);
    public Task<long> EnregistrerClientAsync(ClientFacture c, CancellationToken ct = default) => Task.FromResult(1L);
    public Task<IReadOnlyList<OrganismeFiche>> OrganismesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<OrganismeFiche>>([]);
    public Task<long> EnregistrerOrganismeAsync(OrganismeFiche o, CancellationToken ct = default) => Task.FromResult(1L);
}

public class FacturesTests
{
    private static Facturation Cas(FacturesEnMemoire f, string droit = "factures.gerer") =>
        new(f, new DroitsFactices(droit), new HorlogeFixe(new DateOnly(2026, 11, 9)), new JournalEspion());

    private static DemandeFacture Demande(string dest, long? patient = null, long? organisme = null, params long[] ventes) =>
        new(dest, patient, null, organisme, null, null, null, ventes, [], null);

    private static FactureDetail Detail(string statut) => new(
        new FactureResume(101, "facture", "F-2026-0001", new DateOnly(2026, 11, 9), "organisme", "CNAS", 560, 0, 0, 0, 560, statut, 1, false, null,
            null, null, null, statut == "rejetee" ? "Taux erroné" : null, 1),
        null, null, null, null, null, 560, 0, null, null, "Employé", [], []);

    [Fact]
    public async Task Facture_organisme_part_prise_en_charge_sans_deja_paye()
    {
        var f = new FacturesEnMemoire();
        await Cas(f).EmettreAsync(Demande("organisme", organisme: 1, ventes: 1), 5);
        Assert.Equal((400m, 0m), (f.Emises[0].Totaux.Ttc, f.Emises[0].DejaPaye));
        Assert.Equal(1L, f.Emises[0].OrganismeId);
    }

    [Fact]
    public async Task Facture_patient_refuse_la_vente_d_un_autre_patient_et_compte_le_deja_paye()
    {
        var f = new FacturesEnMemoire();
        await Cas(f).EmettreAsync(Demande("patient", patient: 3, ventes: 1), 5);
        Assert.Equal((100m, 100m), (f.Emises[0].Totaux.Ttc, f.Emises[0].DejaPaye));
        await Assert.ThrowsAsync<RegleMetierException>(() => Cas(f).EmettreAsync(Demande("patient", patient: 3, ventes: [1, 2]), 5));
    }

    [Fact]
    public async Task Vente_deja_facturee_ou_hors_organisme_refusee()
    {
        var f = new FacturesEnMemoire();
        await Assert.ThrowsAsync<RegleMetierException>(() => Cas(f).EmettreAsync(Demande("organisme", organisme: 1, ventes: 2), 5));
        f.Lignes[0] = f.Lignes[0] with { DejaFacturee = true };
        await Assert.ThrowsAsync<RegleMetierException>(() => Cas(f).EmettreAsync(Demande("organisme", organisme: 1, ventes: 1), 5));
        await Assert.ThrowsAsync<DroitRefuseException>(() => Cas(new FacturesEnMemoire(), "caisse.vendre").EmettreAsync(Demande("organisme", organisme: 1, ventes: 1), 5));
    }

    [Fact]
    public async Task Remplacement_seulement_apres_rejet_et_garde_le_destinataire()
    {
        var f = new FacturesEnMemoire { Detail = Detail("emise") };
        await Assert.ThrowsAsync<RegleMetierException>(() => Cas(f).RemplacerAsync(101, Demande("organisme", organisme: 1, ventes: 1), 5));
        f.Detail = Detail("rejetee");
        // Le destinataire demandé est ignoré : celui de la facture rejetée est repris
        await Cas(f).RemplacerAsync(101, Demande("patient", patient: 3, ventes: 1), 5);
        Assert.Equal((101L, Destinataire.Organisme), (f.Remplacee!.Value, f.Emises[0].Destinataire));
    }

    [Fact]
    public async Task Retrait_du_suivi_demande_un_motif()
    {
        var f = new FacturesEnMemoire { Detail = Detail("emise") };
        await Assert.ThrowsAsync<RegleMetierException>(() => Cas(f).SuiviAsync(101, true, " ", 5));
        await Cas(f).SuiviAsync(101, true, "Créance abandonnée", 5);
        Assert.Equal((101L, true, "Créance abandonnée"), f.Suivi);
    }

    [Fact]
    public async Task Avoir_limite_au_montant_de_la_facture()
    {
        var f = new FacturesEnMemoire { Detail = Detail("emise") };
        await Cas(f).AvoirAsync(101, 60, "Boîte retournée", 5);
        Assert.Equal(("avoir", 60m, 101L), (f.Emises[0].Type, f.Emises[0].Totaux.Ttc, f.Emises[0].FactureOrigineId!.Value));
        await Assert.ThrowsAsync<RegleMetierException>(() => Cas(f).AvoirAsync(101, 600, "Trop", 5));
    }
}
