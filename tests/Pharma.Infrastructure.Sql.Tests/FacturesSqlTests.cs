using Pharma.Application.Factures;
using Pharma.Domain.Commun;
using Pharma.Domain.Factures;
using Pharma.Infrastructure.Factures;
using Xunit.Abstractions;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>Lot 4 : correction et sélection des factures clients (FacturesSql) sur une copie PostgreSQL (F1 à F4).</summary>
[Trait("Categorie", "Sql")]
public sealed class FacturesSqlTests(CopieSql copie, ITestOutputHelper sortie) : IClassFixture<CopieSql>
{
    private Donnees D => copie.Donnees;

    /// <summary>Contenu de correction : une ligne libre du TTC voulu, date de facture et échéance au jour donné.</summary>
    private static FactureAEcrire Correction(decimal ttc, long utilisateur, DateOnly date)
        => new("facture", Destinataire.Patient, null, null, null, "Patient Essai", null, date, null, null, new Totaux(ttc, 0m, ttc), 0m,
            [new LigneFacture(null, null, "libre", "Ligne corrigée d'essai", 1m, ttc, 0m, ttc)], null, null, "Correction d'essai", utilisateur, date);

    private async Task<(long Id, string Numero, long Utilisateur)> RejeteeAsync(decimal ttc = 100m, decimal? avoir = null, decimal? reglement = null, DateOnly? date = null)
    {
        sortie.WriteLine($"Copie : {copie.Nom}");
        var u = await D.UtilisateurAsync();
        var (id, numero) = await D.FactureAsync(ttc, u, statut: "rejetee", date: date);
        if (avoir is { } a) await D.AvoirAsync(id, a, u);
        if (reglement is { } r) await D.ReglementAsync(id, r, u);
        return (id, numero, u);
    }

    private async Task AssertInchangeeAsync(long id, decimal ttc)
    {
        Assert.Equal(1, await D.LireAsync<int>("SELECT version FROM facture_client WHERE id = @id", ("id", id)));
        Assert.Equal("rejetee", await D.LireAsync<string>("SELECT statut FROM facture_client WHERE id = @id", ("id", id)));
        Assert.Equal(ttc, await D.LireAsync<decimal>("SELECT montant_ttc FROM facture_client WHERE id = @id", ("id", id)));
        Assert.Equal(1, await D.LireAsync<long>("SELECT count(*) FROM facture_client_ligne WHERE facture_id = @id AND designation = 'Ligne libre d''essai'", ("id", id)));
        Assert.Equal(1, await D.LireAsync<long>("SELECT count(*) FROM facture_client_ligne WHERE facture_id = @id", ("id", id)));
        Assert.Equal(0, await D.LireAsync<long>("SELECT count(*) FROM facture_client_version WHERE facture_id = @id", ("id", id)));
    }

    [FactSql]
    public async Task F1_correction_sous_avoirs_plus_reglements_refusee_et_egale_acceptee()
    {
        var (id, _, u) = await RejeteeAsync(100m, avoir: 30m, reglement: 50m);
        var sql = new FacturesSql(copie.Source);
        var jour = new DateOnly(2026, 6, 20);

        var refus = await Assert.ThrowsAsync<RegleMetierException>(() => sql.CorrigerAsync(id, Correction(79.99m, u, jour), "Motif", CancellationToken.None));
        Assert.Contains("inférieur", refus.Message);
        await AssertInchangeeAsync(id, 100m);

        await sql.CorrigerAsync(id, Correction(80.00m, u, jour), "Motif", CancellationToken.None);

        Assert.Equal(2, await D.LireAsync<int>("SELECT version FROM facture_client WHERE id = @id", ("id", id)));
        Assert.Equal("emise", await D.LireAsync<string>("SELECT statut FROM facture_client WHERE id = @id", ("id", id)));
        Assert.Equal(80.00m, await D.LireAsync<decimal>("SELECT montant_ttc FROM facture_client WHERE id = @id", ("id", id)));
        Assert.Equal(1, await D.LireAsync<long>("SELECT count(*) FROM facture_client_version WHERE facture_id = @id AND version = 1", ("id", id)));
        Assert.Equal("Ligne corrigée d'essai", await D.LireAsync<string>("SELECT designation FROM facture_client_ligne WHERE facture_id = @id", ("id", id)));
    }

    [FactSql]
    public async Task F2_correction_datee_d_une_autre_annee_que_le_numero_refusee()
    {
        var (id, numero, u) = await RejeteeAsync(100m, date: new DateOnly(2026, 6, 15));
        Assert.StartsWith("F-2026-", numero);
        var sql = new FacturesSql(copie.Source);

        var refus = await Assert.ThrowsAsync<RegleMetierException>(
            () => sql.CorrigerAsync(id, Correction(100m, u, new DateOnly(2027, 1, 5)), null, CancellationToken.None));
        Assert.Equal("date_facture", refus.Champ);
        Assert.Contains("2026", refus.Message);
        await AssertInchangeeAsync(id, 100m);

        await sql.CorrigerAsync(id, Correction(100m, u, new DateOnly(2026, 12, 31)), null, CancellationToken.None);

        Assert.Equal(2, await D.LireAsync<int>("SELECT version FROM facture_client WHERE id = @id", ("id", id)));
        Assert.Equal("2026-12-31", await D.LireAsync<string>("SELECT date_facture::text FROM facture_client WHERE id = @id", ("id", id)));
    }

    // bug : FacturesSql.CorrigerAsync relit avoirs et règlements dans la même requête que « FOR UPDATE OF f ». En READ COMMITTED, la requête garde
    // l'instantané pris avant l'attente du verrou : un règlement validé pendant l'attente (l'encaissement de TresorerieSql verrouille la facture
    // sans la modifier) n'est pas vu, et la correction à 60 (< 90 couverts) est acceptée. Correctif : verrouiller dans une requête, relire les sommes dans la suivante.
    [FactSql]
    public async Task F3_reglement_ajoute_pendant_l_attente_du_verrou_fait_refuser_la_correction()
    {
        var (id, _, u) = await RejeteeAsync(100m, reglement: 50m);
        var sql = new FacturesSql(copie.Source);
        await using var bloqueur = await Bloqueur.DemarrerAsync(copie);
        await bloqueur.ExecuterAsync("SELECT id FROM facture_client WHERE id = @id FOR UPDATE", ("id", id));

        var correction = Task.Run(() => sql.CorrigerAsync(id, Correction(60m, u, new DateOnly(2026, 6, 20)), "Motif", CancellationToken.None));
        await bloqueur.AttendreBloquesAsync(1, TimeSpan.FromSeconds(5));
        Assert.False(correction.IsCompleted, "la correction aurait dû attendre le verrou de la facture");
        await bloqueur.ExecuterAsync(Donnees.SqlReglementFacture, ("montant", 40m), ("utilisateur", u), ("facture", id));
        await bloqueur.ValiderAsync();

        var refus = await Assert.ThrowsAsync<RegleMetierException>(() => correction.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("inférieur", refus.Message);
        await AssertInchangeeAsync(id, 100m);
        Assert.Equal(90m, await D.LireAsync<decimal>("SELECT sum(montant) FROM reglement_facture_client WHERE facture_id = @id", ("id", id)));
    }

    [FactSql]
    public async Task F4_AEncaisserParIds_ne_renvoie_que_la_facture_due()
    {
        sortie.WriteLine($"Copie : {copie.Nom}");
        var u = await D.UtilisateurAsync();
        var (due, _) = await D.FactureAsync(100m, u);
        var (soldee, _) = await D.FactureAsync(100m, u);
        await D.ReglementAsync(soldee, 100m, u);
        var (remplacee, _) = await D.FactureAsync(100m, u, statut: "remplacee");
        var (horsSuivi, _) = await D.FactureAsync(100m, u, horsSuivi: true);
        var (origine, _) = await D.FactureAsync(100m, u);
        var avoir = await D.AvoirAsync(origine, 20m, u);
        var inexistant = await D.LireAsync<long>("SELECT max(id) + 99999 FROM facture_client");
        var sql = new FacturesSql(copie.Source);

        var liste = await sql.AEncaisserParIdsAsync([due, soldee, remplacee, horsSuivi, avoir, inexistant], CancellationToken.None);

        Assert.Equal([due], liste.Select(f => f.Id).ToArray());
        Assert.Equal(100m, liste[0].Reste);
        Assert.Empty(await sql.AEncaisserParIdsAsync([], CancellationToken.None));
        Assert.Equal([origine], (await sql.AEncaisserParIdsAsync([origine, avoir], CancellationToken.None)).Select(f => f.Id).ToArray());
    }
}
