using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class AnnulationsEnMemoire(VenteAnnulable vente) : IAnnulationsVente
{
    public AnnulationAEcrire? Ecrite { get; private set; }
    public Task<VenteAnnulable?> TrouverAsync(long? id, string? numero, CancellationToken ct = default) =>
        Task.FromResult<VenteAnnulable?>(Ecrite is null ? vente : vente with { Statut = "annulee" });
    public Task<IReadOnlyList<VenteAnnulable>> DernieresAsync(long s, int l, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VenteAnnulable>>([vente]);
    public Task AnnulerAsync(AnnulationAEcrire a, CancellationToken ct = default) { Ecrite = a; return Task.CompletedTask; }
}

public class AnnulationTests
{
    // Crédit : par défaut la dette couvre encore toute la vente (rien n'a été remboursé)
    private static VenteAnnulable Vente(bool caisseOuverte, string mode = "especes", decimal dejaRendu = 0, decimal? solde = null, string? facture = null) =>
        new(12, "T-000012", DateTime.UtcNow, "Vendeur", "Patient fictif", 470, 470, 0, mode, "validee", caisseOuverte, false,
            [new LigneVenteAnnulable("Produit fictif", 2, 470)], dejaRendu, solde ?? (mode == "credit" ? 470 : 0), facture);

    // Le caissier (5) n'a que « vendre » ; l'assistante (8) a « annuler » et un code comptoir 4826
    private static (AnnulerVente Cas, AnnulationsEnMemoire Stock, JournalEspion Journal) Monter(VenteAnnulable v, SessionsEnMemoire? sessions = null)
    {
        var stock = new AnnulationsEnMemoire(v);
        var codes = new CodesEnMemoire();
        codes.Comptes[8] = new CompteComptoir(8, "assistante", "Assistante Fictive", "hash:4826", 0, null);
        codes.Comptes[9] = new CompteComptoir(9, "vendeur.b", "Vendeur B", "hash:2580", 0, null);
        var droits = new DroitsParEmploye(new() { [5] = ["caisse.vendre"], [8] = ["caisse.vendre", "caisse.annuler_vente"], [9] = ["caisse.vendre"] });
        var journal = new JournalEspion();
        var cas = new AnnulerVente(stock, sessions ?? new SessionsEnMemoire(), droits,
            new VerificationCodeComptoir(codes, new VerificateurFactice(), new HorlogeFixe(new DateOnly(2026, 11, 9)), journal), journal);
        return (cas, stock, journal);
    }

    [Fact]
    public async Task Vente_recente_annulee_par_le_caissier_seul_avec_l_argent_rendu()
    {
        var (cas, stock, journal) = Monter(Vente(caisseOuverte: true));
        var r = await cas.ExecuterAsync(12, "Erreur de produit", 5, "Caisse 1", null, null);
        Assert.Equal((470m, "especes", (string?)null), (r.ARendre, r.Mode, r.ValidePar));
        Assert.Equal((false, (long?)null, (long?)1), (stock.Ecrite!.Ancienne, stock.Ecrite.ValidePar, stock.Ecrite.SessionCaisseId));
        Assert.Contains("vente_annulee", journal.Actions);
    }

    [Fact]
    public async Task Vente_ancienne_refusee_sans_validation_puis_acceptee_avec_le_code_de_l_assistante()
    {
        var (cas, stock, _) = Monter(Vente(caisseOuverte: false));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(12, "Retour patient", 5, "Caisse 1", null, null));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(12, "Retour patient", 5, "Caisse 1", "assistante", "1111"));
        // Un collègue sans le droit ne peut pas valider, même avec le bon code
        await Assert.ThrowsAsync<DroitRefuseException>(() => cas.ExecuterAsync(12, "Retour patient", 5, "Caisse 1", "vendeur.b", "2580"));
        var r = await cas.ExecuterAsync(12, "Retour patient", 5, "Caisse 1", "assistante", "4826");
        Assert.Equal("Assistante Fictive", r.ValidePar);
        Assert.Equal((true, (long?)8), (stock.Ecrite!.Ancienne, stock.Ecrite.ValidePar));
    }

    [Fact]
    public async Task Vente_ancienne_annulee_directement_par_l_employe_habilite()
    {
        var (cas, stock, _) = Monter(Vente(caisseOuverte: false));
        await cas.ExecuterAsync(12, "Erreur de saisie", 8, "Caisse 1", null, null);
        Assert.Equal((long?)8, stock.Ecrite!.ValidePar);
    }

    [Fact]
    public async Task Motif_obligatoire_et_pas_de_double_annulation()
    {
        var (cas, _, _) = Monter(Vente(caisseOuverte: true));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(12, " ", 5, "Caisse 1", null, null));
        await cas.ExecuterAsync(12, "Erreur", 5, "Caisse 1", null, null);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(12, "Erreur", 5, "Caisse 1", null, null));
    }

    [Fact]
    public async Task Especes_a_rendre_exigent_une_caisse_ouverte_sur_le_poste_mais_pas_le_credit()
    {
        var fermee = new SessionsEnMemoire { Ouverte = null };
        await Assert.ThrowsAsync<RegleMetierException>(() => Monter(Vente(true), fermee).Cas.ExecuterAsync(12, "Erreur", 8, "Caisse 1", null, null));
        var (cas, stock, _) = Monter(Vente(true, "credit"), new SessionsEnMemoire { Ouverte = null });
        var r = await cas.ExecuterAsync(12, "Erreur", 8, "Caisse 1", null, null);
        Assert.Equal(0m, r.ARendre);
        Assert.Null(stock.Ecrite!.SessionCaisseId);
    }

    [Fact]
    public async Task Annulation_apres_un_retour_ne_rend_que_le_reste()
    {
        var (cas, stock, _) = Monter(Vente(true, dejaRendu: 235));
        var r = await cas.ExecuterAsync(12, "Erreur", 5, "Caisse 1", null, null);
        Assert.Equal(235m, r.ARendre);
        Assert.Equal(235m, stock.Ecrite!.MontantRembourse);
    }

    [Fact]
    public async Task Credit_deja_rembourse_en_partie_rendu_en_especes_et_dette_diminuee_du_reste()
    {
        var (cas, stock, _) = Monter(Vente(true, "credit", solde: 170));
        var r = await cas.ExecuterAsync(12, "Erreur", 5, "Caisse 1", null, null);
        Assert.Equal((300m, "especes"), (r.ARendre, r.Mode));
        Assert.Equal((170m, 300m, "especes"), (stock.Ecrite!.ReductionDette, stock.Ecrite.MontantRembourse, stock.Ecrite.Mode));
        // Rendre de l'argent exige la caisse ouverte du poste
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            Monter(Vente(true, "credit", solde: 170), new SessionsEnMemoire { Ouverte = null }).Cas.ExecuterAsync(12, "Erreur", 5, "Caisse 1", null, null));
    }

    [Fact]
    public async Task Vente_sur_une_facture_en_vigueur_non_annulable()
    {
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => Monter(Vente(true, facture: "F-2026-0003")).Cas.ExecuterAsync(12, "Erreur", 5, "Caisse 1", null, null));
        Assert.Contains("F-2026-0003", e.Message);
    }
}

/// <summary>Droits différents selon l'employé.</summary>
internal sealed class DroitsParEmploye(Dictionary<long, string[]> droits) : IPermissionService
{
    public Task<bool> APermissionAsync(long u, string p, CancellationToken ct = default) => Task.FromResult(droits.GetValueOrDefault(u, []).Contains(p));
    public Task<IReadOnlyList<string>> ListerAsync(long u, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(droits.GetValueOrDefault(u, []));
    public Task<decimal> RemiseMaxAsync(long u, CancellationToken ct = default) => Task.FromResult(0m);
    public Task<bool> EstTitulaireAsync(long u, CancellationToken ct = default) => Task.FromResult(false);
}
