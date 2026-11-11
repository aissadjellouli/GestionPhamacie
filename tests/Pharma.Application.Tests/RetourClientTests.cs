using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

internal sealed class RetoursEnMemoire(VenteRetournable vente) : IRetoursClient
{
    public RetourAEcrire? Ecrit { get; private set; }
    public Task<VenteRetournable?> LireAsync(long id, CancellationToken ct = default) => Task.FromResult<VenteRetournable?>(vente);
    public Task EnregistrerAsync(RetourAEcrire r, CancellationToken ct = default) { Ecrit = r; return Task.CompletedTask; }
}

public class RetourClientTests
{
    private static VenteRetournable Vente(string mode = "especes", decimal partOrganisme = 0) => new(
        new VenteAnnulable(20, "T-000020", DateTime.UtcNow, "Vendeur", null, 590, 590, partOrganisme, mode, "validee", false, false, []),
        [
            new LigneRetournable(1, 10, "Sirop fictif", "S1", 3, 0, 350, []),
            new LigneRetournable(2, 11, "Comprimé fictif", "C1", 2, 0, 240, [new BoiteVendue(100, "SN-A"), new BoiteVendue(101, "SN-B")])
        ]);

    private static (EnregistrerRetourClient Cas, RetoursEnMemoire Stock) Monter(VenteRetournable v, SessionsEnMemoire? sessions = null,
        Pharma.Application.Patients.IPatients? patients = null)
    {
        var stock = new RetoursEnMemoire(v);
        var codes = new CodesEnMemoire();
        codes.Comptes[8] = new CompteComptoir(8, "assistante", "Assistante Fictive", "hash:4826", 0, null);
        var droits = new DroitsParEmploye(new() { [5] = ["caisse.vendre"], [8] = ["caisse.vendre", "caisse.retour_especes"] });
        var journal = new JournalEspion();
        return (new EnregistrerRetourClient(stock, sessions ?? new SessionsEnMemoire(), droits,
            new VerificationCodeComptoir(codes, new VerificateurFactice(), new HorlogeFixe(new DateOnly(2026, 11, 9)), journal), journal, patients), stock);
    }

    [Fact]
    public async Task Le_caissier_doit_faire_valider_le_retour()
    {
        var (cas, stock) = Monter(Vente());
        LigneRetourDemandee[] l = [new(1, 1, "stock", null)];
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(20, "Traitement arrêté", l, 5, "Caisse 1", null, null));
        var r = await cas.ExecuterAsync(20, "Traitement arrêté", l, 5, "Caisse 1", "assistante", "4826");
        Assert.Equal(("Assistante Fictive", 116.67m, 1), (r.ValidePar, r.ARendre, r.RemisEnStock));
        Assert.Equal(8, stock.Ecrit!.ValidePar);
    }

    [Fact]
    public async Task Boites_identifiees_cochees_et_destination_au_choix()
    {
        var (cas, stock) = Monter(Vente());
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            cas.ExecuterAsync(20, "Mauvais dosage", [new(2, 1, "destruction", null)], 8, "Caisse 1", null, null));
        var r = await cas.ExecuterAsync(20, "Mauvais dosage", [new(2, 1, "destruction", [101]), new(1, 2, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal((2, 1, 353.33m), (r.RemisEnStock, r.ADetruire, r.ARendre));
        Assert.Equal(DestinationRetour.Destruction, stock.Ecrit!.Lignes[0].Destination);
        Assert.Equal([101L], stock.Ecrit.Lignes[0].Boites);
    }

    [Fact]
    public async Task Plus_que_vendu_et_tiers_payant_refuses()
    {
        var (cas, _) = Monter(Vente());
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(20, "Erreur", [new(1, 4, "stock", null)], 8, "Caisse 1", null, null));
        var (tp, _) = Monter(Vente(partOrganisme: 280));
        await Assert.ThrowsAsync<RegleMetierException>(() => tp.ExecuterAsync(20, "Erreur", [new(1, 1, "stock", null)], 8, "Caisse 1", null, null));
    }

    [Fact]
    public async Task Credit_sans_argent_rendu_et_especes_sans_caisse_ouverte_refusees()
    {
        var due = Vente("credit");
        var (credit, stock) = Monter(due with { Vente = due.Vente with { SoldeCreditPatient = 590 } }, new SessionsEnMemoire { Ouverte = null });
        var r = await credit.ExecuterAsync(20, "Erreur", [new(1, 3, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal((0m, 350m, 350m), (r.ARendre, stock.Ecrit!.Montant, stock.Ecrit.ReductionDette));
        var (especes, _) = Monter(Vente(), new SessionsEnMemoire { Ouverte = null });
        await Assert.ThrowsAsync<RegleMetierException>(() => especes.ExecuterAsync(20, "Erreur", [new(1, 1, "stock", null)], 8, "Caisse 1", null, null));
    }

    [Fact]
    public async Task Credit_deja_rembourse_la_part_payee_est_rendue_en_especes()
    {
        // Dette restante 100 DA : elle est effacée, les 250 DA déjà remboursés par le patient lui sont rendus
        var vente = Vente("credit");
        var partielle = vente with { Vente = vente.Vente with { SoldeCreditPatient = 100 } };
        var (cas, stock) = Monter(partielle);
        var r = await cas.ExecuterAsync(20, "Erreur", [new(1, 3, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal((250m, "especes"), (r.ARendre, r.Mode));
        Assert.Equal((350m, 100m), (stock.Ecrit!.Montant, stock.Ecrit.ReductionDette));

        // L'argent rendu sort d'une caisse ouverte
        var (sansCaisse, _) = Monter(partielle, new SessionsEnMemoire { Ouverte = null });
        await Assert.ThrowsAsync<RegleMetierException>(() => sansCaisse.ExecuterAsync(20, "Erreur", [new(1, 3, "stock", null)], 8, "Caisse 1", null, null));
    }

    [Fact]
    public async Task Patient_endette_le_caissier_choisit_de_solder_la_dette_ou_de_rendre()
    {
        // Vente payée en espèces à un patient qui doit 200 DA par ailleurs ; retour de 350 DA
        var vente = Vente();
        var endette = vente with { Vente = vente.Vente with { Patient = "Patient Fictif", SoldeCreditPatient = 200 } };
        LigneRetourDemandee[] l = [new(1, 3, "stock", null)];

        // Sans l'accord du client : refusé
        var (sansAccord, _) = Monter(endette);
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => sansAccord.ExecuterAsync(20, "Erreur", l, 8, "Caisse 1", null, null, solderDette: true));
        Assert.Equal("accord_client", e.Champ);

        var (solder, stock) = Monter(endette);
        var r = await solder.ExecuterAsync(20, "Erreur", l, 8, "Caisse 1", null, null, solderDette: true, accordClient: true);
        Assert.True(stock.Ecrit!.AccordClient);
        Assert.Equal((150m, 200m, "especes"), (r.ARendre, r.DetteDiminuee, r.Mode));   // dette soldée, surplus rendu
        Assert.Equal((350m, 200m), (stock.Ecrit!.Montant, stock.Ecrit.ReductionDette));

        var (rendre, stock2) = Monter(endette);
        var r2 = await rendre.ExecuterAsync(20, "Erreur", l, 8, "Caisse 1", null, null, solderDette: false);
        Assert.Equal((350m, 0m, 0m), (r2.ARendre, r2.DetteDiminuee, stock2.Ecrit!.ReductionDette));
    }

    [Fact]
    public async Task Vente_sans_patient_le_patient_se_nomme_et_sa_dette_est_verifiee()
    {
        var patients = new PatientsEnMemoire(new Patient { Id = 3, Nom = "Fictif", SoldeCredit = 1000 });
        LigneRetourDemandee[] l = [new(1, 3, "stock", null)];

        // Dette de 1000 DA : les 350 DA du retour en sont déduits, rien ne sort de la caisse (même fermée)
        var (cas, stock) = Monter(Vente(), new SessionsEnMemoire { Ouverte = null }, patients);
        var r = await cas.ExecuterAsync(20, "Erreur", l, 8, "Caisse 1", null, null, solderDette: true, patientDetteId: 3, accordClient: true);
        Assert.Equal((0m, 350m), (r.ARendre, r.DetteDiminuee));
        Assert.Equal((350m, 3L), (stock.Ecrit!.ReductionDette, stock.Ecrit.PatientDetteId));

        // Patient inconnu, ou vente qui a déjà son patient : refusé
        var (inconnu, _) = Monter(Vente(), null, patients);
        await Assert.ThrowsAsync<RegleMetierException>(() => inconnu.ExecuterAsync(20, "Erreur", l, 8, "Caisse 1", null, null, solderDette: true, patientDetteId: 99, accordClient: true));
        var nommee = Vente();
        var (dejaNommee, _) = Monter(nommee with { Vente = nommee.Vente with { Patient = "Autre Patient" } }, null, patients);
        await Assert.ThrowsAsync<RegleMetierException>(() => dejaNommee.ExecuterAsync(20, "Erreur", l, 8, "Caisse 1", null, null, solderDette: true, patientDetteId: 3, accordClient: true));
    }

    [Fact]
    public async Task Vendeur_sans_le_droit_valide_seul_un_retour_entierement_deduit_de_la_dette_avec_l_accord_du_client()
    {
        var vente = Vente();
        var endette = vente with { Vente = vente.Vente with { Patient = "Patient Fictif", SoldeCreditPatient = 1000 } };
        LigneRetourDemandee[] l = [new(1, 3, "stock", null)];

        // Employé 5 : pas le droit de rembourser. Tout est déduit (dette 1000 DA, retour 350 DA) : aucun code demandé
        var (cas, stock) = Monter(endette);
        var r = await cas.ExecuterAsync(20, "Erreur", l, 5, "Caisse 1", null, null, solderDette: true, accordClient: true);
        Assert.Equal((0m, 350m, (string?)null), (r.ARendre, r.DetteDiminuee, r.ValidePar));
        Assert.Equal(5L, stock.Ecrit!.ValidePar);

        // Rendu en espèces : la validation reste demandée
        var (rendre, _) = Monter(endette);
        await Assert.ThrowsAsync<RegleMetierException>(() => rendre.ExecuterAsync(20, "Erreur", l, 5, "Caisse 1", null, null, solderDette: false));
        // Dette plus petite que le retour : une partie est rendue en espèces, validation demandée
        var petite = vente with { Vente = vente.Vente with { Patient = "Patient Fictif", SoldeCreditPatient = 100 } };
        var (surplus, _) = Monter(petite);
        await Assert.ThrowsAsync<RegleMetierException>(() => surplus.ExecuterAsync(20, "Erreur", l, 5, "Caisse 1", null, null, solderDette: true, accordClient: true));
    }
}
