using Npgsql;
using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Infrastructure.Caisse;
using Pharma.Infrastructure.Securite;
using Xunit.Abstractions;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>
/// Lot 1 : annulation d'une vente (AnnulationsVenteSql.AnnulerAsync) et journal écrit dans sa transaction, sur une copie jetable de la base.
/// Tests C1 à C6 et J1 du plan. Les contrôles internes (VerifierLectureAsync, EcrireEntreesAsync) sont atteints par le point d'entrée public.
/// </summary>
[Trait("Categorie", "Sql")]
public sealed class CaisseAnnulationSqlTests(CopieSql copie, ITestOutputHelper sortie) : IClassFixture<CopieSql>
{
    private const string Poste = "POSTE-ESSAI-1";
    private const string Permission = "caisse.annuler_vente";

    private Donnees D => copie.Donnees;

    private AnnulationsVenteSql Sut() => new(copie.Source);

    private static AnnulationAEcrire Annulation(long venteId, long utilisateurId, decimal reductionDette = 0m, long? patientDetteId = null,
        LectureDette? lecture = null, IReadOnlyList<JournalAEcrire>? journaux = null) =>
        new(venteId, "Erreur de saisie d'essai", utilisateurId, null, false, null, 0m, null, Poste,
            ReductionDette: reductionDette, PatientDetteId: patientDetteId, Lecture: lecture, Journaux: journaux);

    private static JournalAEcrire Entree(long utilisateurId, string action) =>
        new(utilisateurId, action, "vente", null, ctx => new Dictionary<string, object?> { ["numero"] = ctx.Numero, ["motif"] = "essai" }, Permission);

    /// <summary>Lecture de la dette comme l'écran de caisse : relue en base par l'application avant l'annulation.</summary>
    private async Task<LectureDette> LireDetteAsync(VenteEssai vente, bool avecPaye)
    {
        var lue = await Sut().TrouverAsync(vente.Id, null);
        Assert.NotNull(lue);
        return new LectureDette(lue.PatientId!.Value, lue.SoldeCreditPatient, avecPaye ? lue.PayeSurVente : null);
    }

    private Task<long> Compter(string sql, params (string Nom, object? Valeur)[] p) => D.LireAsync<long>(sql, p);

    private Task<long> Annulations(long venteId) => Compter("SELECT count(*) FROM annulation_vente WHERE vente_id = @v", ("v", venteId));

    private Task<long> LignesJournal(long venteId) =>
        Compter("SELECT count(*) FROM journal_audit WHERE table_cible = 'vente' AND enregistrement_id = @v", ("v", venteId));

    private Task<string> Statut(long venteId) => D.LireAsync<string>("SELECT statut FROM vente WHERE id = @v", ("v", venteId));

    private Task<int> Stock(long lotId) => D.LireAsync<int>("SELECT quantite_disponible FROM lot WHERE id = @l", ("l", lotId));

    /// <summary>Vente à crédit : patient (dette = <paramref name="credit"/>), lot de 5 dont 2 vendus.</summary>
    private async Task<(long Employe, long Patient, long Lot, VenteEssai Vente)> VenteACreditAsync(decimal credit)
    {
        var employe = await D.UtilisateurAsync();
        var patient = await D.PatientAsync();
        var lot = await D.LotAsync(await D.ProduitAsync(), quantite: 5);
        var vente = await D.VenteValideeAsync(employe, [new LigneEssai(lot, 2, credit / 2)], patient, credit);
        Assert.Equal(3, await Stock(lot));
        return (employe, patient, lot, vente);
    }

    private async Task AssertRienEcritAsync(VenteEssai vente, long lot, int stockAttendu)
    {
        Assert.Equal("validee", await Statut(vente.Id));
        Assert.Equal(stockAttendu, await Stock(lot));
        Assert.Equal(0, await Annulations(vente.Id));
        Assert.Equal(0, await LignesJournal(vente.Id));
        Assert.Equal(0, await Compter("SELECT count(*) FROM mouvement_stock WHERE lot_id = @l AND type = 'retour_client'", ("l", lot)));
    }

    [FactSql]
    public async Task C1_dette_modifiee_entre_la_lecture_et_l_annulation_est_refusee_sans_rien_ecrire()
    {
        sortie.WriteLine($"Copie : {copie.Nom}");
        var (employe, patient, lot, vente) = await VenteACreditAsync(100m);
        var lecture = await LireDetteAsync(vente, avecPaye: false);
        Assert.Equal(100m, lecture.Solde);
        await D.ExecuterAsync("UPDATE patient SET solde_credit = 150 WHERE id = @p", ("p", patient));

        var erreur = await Assert.ThrowsAsync<RegleMetierException>(() => Sut().AnnulerAsync(
            Annulation(vente.Id, employe, reductionDette: 100m, patientDetteId: patient, lecture: lecture, journaux: [Entree(employe, "annulation_vente")])));

        Assert.Contains("dette du patient a changé", erreur.Message);
        await AssertRienEcritAsync(vente, lot, stockAttendu: 3);
    }

    [FactSql]
    public async Task C2_dette_modifiee_pendant_l_attente_du_verrou_du_patient_est_refusee()
    {
        var (employe, patient, lot, vente) = await VenteACreditAsync(100m);
        var lecture = await LireDetteAsync(vente, avecPaye: false);
        await using var bloqueur = await Bloqueur.DemarrerAsync(copie);
        await bloqueur.ExecuterAsync("SELECT id FROM patient WHERE id = @p FOR UPDATE", ("p", patient));

        var suite = Task.Run(() => Sut().AnnulerAsync(
            Annulation(vente.Id, employe, reductionDette: 100m, patientDetteId: patient, lecture: lecture, journaux: [Entree(employe, "annulation_vente")])));
        await bloqueur.AttendreBloquesAsync(1, TimeSpan.FromSeconds(5));
        Assert.False(suite.IsCompleted, "l'annulation aurait dû attendre le verrou du patient");
        await bloqueur.ExecuterAsync("UPDATE patient SET solde_credit = 120 WHERE id = @p", ("p", patient));
        await bloqueur.ValiderAsync();

        var erreur = await Assert.ThrowsAsync<RegleMetierException>(() => suite.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("dette du patient a changé", erreur.Message);
        Assert.Equal(120m, await D.LireAsync<decimal>("SELECT solde_credit FROM patient WHERE id = @p", ("p", patient)));
        await AssertRienEcritAsync(vente, lot, stockAttendu: 3);
    }

    [FactSql]
    public async Task C3_part_deja_payee_modifiee_entre_la_lecture_et_l_annulation_est_refusee()
    {
        var (employe, patient, lot, vente) = await VenteACreditAsync(100m);
        await D.ReglementCreditAsync(patient, 30m, employe);
        var lecture = await LireDetteAsync(vente, avecPaye: true);
        Assert.Equal(70m, lecture.Solde);
        Assert.Equal(30m, lecture.PayeSurVente);
        // La dette reste à 70 (remboursement compensé par une correction) : seule la part déjà payée change, de 30 à 40
        await D.ReglementEtCorrectionNeutresAsync(patient, 10m, employe);
        Assert.Equal(70m, await D.LireAsync<decimal>("SELECT solde_credit FROM patient WHERE id = @p", ("p", patient)));

        var erreur = await Assert.ThrowsAsync<RegleMetierException>(() => Sut().AnnulerAsync(
            Annulation(vente.Id, employe, reductionDette: 70m, patientDetteId: patient, lecture: lecture, journaux: [Entree(employe, "annulation_vente")])));

        Assert.Contains("déjà payé", erreur.Message);
        Assert.Contains("a changé", erreur.Message);
        await AssertRienEcritAsync(vente, lot, stockAttendu: 3);
        Assert.Equal(0, await Compter("SELECT count(*) FROM credit_mouvement WHERE vente_id = @v AND type = 'annulation'", ("v", vente.Id)));
    }

    [FactSql]
    public async Task C4_annulation_nominale_remet_les_lots_et_ecrit_le_journal_dans_la_meme_transaction()
    {
        var employe = await D.UtilisateurAsync();
        var produit = await D.ProduitAsync();
        var lotA = await D.LotAsync(produit, quantite: 5);
        var lotB = await D.LotAsync(produit, quantite: 8);
        var vente = await D.VenteValideeAsync(employe, [new LigneEssai(lotA, 2), new LigneEssai(lotB, 3)]);
        Assert.Equal(3, await Stock(lotA));
        Assert.Equal(5, await Stock(lotB));

        await Sut().AnnulerAsync(Annulation(vente.Id, employe,
            journaux: [Entree(employe, "annulation_vente"), Entree(employe, "annulation_vente_validee")]));

        Assert.Equal("annulee", await Statut(vente.Id));
        Assert.Equal(5, await Stock(lotA));
        Assert.Equal(8, await Stock(lotB));
        Assert.Equal(2, await Compter(
            "SELECT count(*) FROM mouvement_stock WHERE type = 'retour_client' AND reference = @r AND lot_id = ANY(@lots)",
            ("r", $"Annulation {vente.Numero}"), ("lots", new[] { lotA, lotB })));
        Assert.Equal(1, await Annulations(vente.Id));
        Assert.Equal(2, await LignesJournal(vente.Id));
        Assert.Equal(2, await Compter(
            "SELECT count(*) FROM journal_audit WHERE enregistrement_id = @v AND utilisateur_id = @u AND details ->> 'numero' = @n " +
            "AND action IN ('annulation_vente', 'annulation_vente_validee') AND permission_code = @perm",
            ("v", vente.Id), ("u", employe), ("n", vente.Numero), ("perm", Permission)));
        // Même transaction : les écritures d'annulation et du journal sont validées ensemble (C5 prouve l'annulation conjointe)
        Assert.Equal(1, await Compter("SELECT count(DISTINCT xmin::text) FROM (" +
            "SELECT xmin FROM annulation_vente WHERE vente_id = @v UNION ALL SELECT xmin FROM journal_audit WHERE enregistrement_id = @v AND table_cible = 'vente') t",
            ("v", vente.Id)));
    }

    [FactSql]
    public async Task C5_echec_a_l_etape_dette_annule_tout_y_compris_le_journal_et_le_stock()
    {
        var (employe, patient, lot, vente) = await VenteACreditAsync(100m);

        var erreur = await Assert.ThrowsAsync<RegleMetierException>(() => Sut().AnnulerAsync(
            Annulation(vente.Id, employe, reductionDette: 150m, patientDetteId: patient,
                journaux: [Entree(employe, "annulation_vente"), Entree(employe, "annulation_vente_validee")])));

        Assert.Contains("dette du patient vient de changer", erreur.Message);
        await AssertRienEcritAsync(vente, lot, stockAttendu: 3);
        Assert.Equal(100m, await D.LireAsync<decimal>("SELECT solde_credit FROM patient WHERE id = @p", ("p", patient)));
        Assert.Equal(0, await Compter("SELECT count(*) FROM credit_mouvement WHERE vente_id = @v AND type = 'annulation'", ("v", vente.Id)));
    }

    [FactSql]
    public async Task C6_deux_annulations_croisees_sur_les_memes_lots_verrouilles_dans_l_ordre_des_id_sans_interblocage()
    {
        var employe = await D.UtilisateurAsync();
        var produit = await D.ProduitAsync();
        var lotA = await D.LotAsync(produit, quantite: 10);
        var lotB = await D.LotAsync(produit, quantite: 10);
        Assert.True(lotA < lotB);
        var v1 = await D.VenteValideeAsync(employe, [new LigneEssai(lotB, 2), new LigneEssai(lotA, 2)]);
        var v2 = await D.VenteValideeAsync(employe, [new LigneEssai(lotA, 3), new LigneEssai(lotB, 3)]);
        Assert.Equal(5, await Stock(lotA));
        Assert.Equal(5, await Stock(lotB));
        await using var bloqueur = await Bloqueur.DemarrerAsync(copie);
        await bloqueur.ExecuterAsync("SELECT id FROM lot WHERE id = @l FOR UPDATE", ("l", lotA));

        var t1 = Task.Run(() => Sut().AnnulerAsync(Annulation(v1.Id, employe)));
        var t2 = Task.Run(() => Sut().AnnulerAsync(Annulation(v2.Id, employe)));
        await bloqueur.AttendreBloquesAsync(2, TimeSpan.FromSeconds(5));
        await bloqueur.ValiderAsync();

        try { await Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (PostgresException e) { throw new Xunit.Sdk.XunitException($"Erreur SQL {e.SqlState} : {e.MessageText}"); }

        Assert.Equal("annulee", await Statut(v1.Id));
        Assert.Equal("annulee", await Statut(v2.Id));
        Assert.Equal(10, await Stock(lotA));
        Assert.Equal(10, await Stock(lotB));
    }

    [FactSql]
    public async Task J1_le_journal_ecrit_par_l_annulation_est_relu_avec_action_et_vente_et_les_chaines_restent_intactes()
    {
        var nom = $"Employe Journal {Donnees.Suivant()}";
        var employe = await D.UtilisateurAsync(nom);
        var lot = await D.LotAsync(await D.ProduitAsync(), quantite: 5);
        var vente = await D.VenteValideeAsync(employe, [new LigneEssai(lot, 2)]);
        await Sut().AnnulerAsync(Annulation(vente.Id, employe,
            journaux: [Entree(employe, "annulation_vente"), Entree(employe, "annulation_vente_validee")]));

        var page = await new JournalSql(copie.Source).PageAsync(new FiltreJournal(null, null, nom), new DemandePage(1, 10));

        Assert.Equal(2, page.Total);
        Assert.Equal(new[] { "annulation_vente", "annulation_vente_validee" }, page.Lignes.Select(l => l.Action).Order().ToArray());
        Assert.All(page.Lignes, l =>
        {
            Assert.Equal(nom, l.Employe);
            Assert.Equal(Permission, l.Permission);
            Assert.Contains(vente.Numero, l.DetailsJson);
        });
        // Contrôle de chaînage du script 111 sur chaque journal inscrit : aucune rupture
        var journaux = new List<string>();
        await using (var cmd = copie.Verif.CreateCommand("SELECT journal FROM chaine_integrite ORDER BY journal"))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) journaux.Add(r.GetString(0));
        foreach (var journal in journaux)
            Assert.Equal(0, await Compter("SELECT count(*) FROM integrite_verifier(@j) WHERE rang_rupture IS NOT NULL", ("j", journal)));
    }
}
