using Npgsql;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>Ligne d'une vente d'essai : lot vendu (le produit est celui du lot), quantité, prix unitaire.</summary>
public sealed record LigneEssai(long LotId, int Quantite, decimal PrixUnitaire = 100m);

/// <summary>Vente d'essai créée par la fabrique.</summary>
public sealed record VenteEssai(long Id, string Numero);

/// <summary>Fabrique de données du lot « caisse » : ventes validées, ventes à crédit, règlements de dette (SQL paramétré, noms fictifs).</summary>
public sealed partial class Donnees
{
    /// <summary>
    /// Vente validée (libre, sans session de caisse) : lignes sur des lots existants, stock du lot diminué de la quantité vendue et
    /// mouvement « vente » écrit. <paramref name="credit"/> &gt; 0 : le montant est mis sur la dette du patient (type « vente_credit »
    /// rattaché à la vente, comme le fait la caisse) ; il faut alors un patient. Tout est écrit dans une seule transaction.
    /// </summary>
    public async Task<VenteEssai> VenteValideeAsync(long utilisateurId, IReadOnlyList<LigneEssai> lignes, long? patientId = null, decimal credit = 0m)
    {
        if (credit > 0 && patientId is null) throw new ArgumentException("Une vente à crédit exige un patient.", nameof(credit));
        var numero = $"V-ESSAI-{Suivant()}";
        var total = lignes.Sum(l => l.Quantite * l.PrixUnitaire);
        await using var cx = await Source.OpenConnectionAsync();
        await using var tx = await cx.BeginTransactionAsync();

        long venteId;
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO vente (numero, utilisateur_id, patient_id, type, montant_total, part_patient, statut) " +
            "VALUES (@numero, @u, @p, 'libre', @total, @total, 'validee') RETURNING id", cx, tx))
        {
            cmd.Parameters.AddWithValue("numero", numero);
            cmd.Parameters.AddWithValue("u", utilisateurId);
            cmd.Parameters.AddWithValue("p", (object?)patientId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("total", total);
            venteId = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        foreach (var l in lignes)
        {
            await using (var cmd = new NpgsqlCommand(
                "INSERT INTO ligne_vente (vente_id, produit_id, lot_id, quantite, prix_unitaire, montant_patient) " +
                "SELECT @v, produit_id, id, @q, @prix, @montant FROM lot WHERE id = @lot", cx, tx))
            {
                cmd.Parameters.AddWithValue("v", venteId);
                cmd.Parameters.AddWithValue("q", l.Quantite);
                cmd.Parameters.AddWithValue("prix", l.PrixUnitaire);
                cmd.Parameters.AddWithValue("montant", l.Quantite * l.PrixUnitaire);
                cmd.Parameters.AddWithValue("lot", l.LotId);
                await cmd.ExecuteNonQueryAsync();
            }
            await using (var cmd = new NpgsqlCommand("UPDATE lot SET quantite_disponible = quantite_disponible - @q WHERE id = @lot", cx, tx))
            {
                cmd.Parameters.AddWithValue("q", l.Quantite);
                cmd.Parameters.AddWithValue("lot", l.LotId);
                await cmd.ExecuteNonQueryAsync();
            }
            await using (var cmd = new NpgsqlCommand(
                "INSERT INTO mouvement_stock (lot_id, type, quantite, reference, utilisateur_id) VALUES (@lot, 'vente', @q, @ref, @u)", cx, tx))
            {
                cmd.Parameters.AddWithValue("lot", l.LotId);
                cmd.Parameters.AddWithValue("q", -l.Quantite);
                cmd.Parameters.AddWithValue("ref", numero);
                cmd.Parameters.AddWithValue("u", utilisateurId);
                await cmd.ExecuteNonQueryAsync();
            }
        }
        if (credit > 0) await MouvementDetteAsync(cx, tx, patientId!.Value, credit, "vente_credit", venteId, utilisateurId, numero);
        await tx.CommitAsync();
        return new VenteEssai(venteId, numero);
    }

    /// <summary>Remboursement de dette versé par le patient (type « remboursement » : la dette diminue de <paramref name="montant"/>).</summary>
    public async Task ReglementCreditAsync(long patientId, decimal montant, long utilisateurId)
    {
        await using var cx = await Source.OpenConnectionAsync();
        await using var tx = await cx.BeginTransactionAsync();
        await MouvementDetteAsync(cx, tx, patientId, -montant, "remboursement", null, utilisateurId, "Règlement d'essai");
        await tx.CommitAsync();
    }

    /// <summary>
    /// Remboursement de <paramref name="montant"/> compensé, dans la même transaction, par une correction de même montant : la dette du
    /// patient ne change pas mais ce qu'il a payé sur ses ventes à crédit augmente.
    /// </summary>
    public async Task ReglementEtCorrectionNeutresAsync(long patientId, decimal montant, long utilisateurId)
    {
        await using var cx = await Source.OpenConnectionAsync();
        await using var tx = await cx.BeginTransactionAsync();
        await MouvementDetteAsync(cx, tx, patientId, -montant, "remboursement", null, utilisateurId, "Règlement d'essai");
        await MouvementDetteAsync(cx, tx, patientId, montant, "correction", null, utilisateurId, "Correction d'essai");
        await tx.CommitAsync();
    }

    /// <summary>Contexte du relevé de crédit (credit_contexte) puis variation de patient.solde_credit : le déclencheur écrit le mouvement.</summary>
    private static async Task MouvementDetteAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long patientId, decimal delta, string type,
        long? venteId, long utilisateurId, string reference)
    {
        await using (var cmd = new NpgsqlCommand("SELECT credit_contexte(@type, @vente, @u, 'POSTE-ESSAI', @ref)", cx, tx))
        {
            cmd.Parameters.AddWithValue("type", type);
            cmd.Parameters.AddWithValue("vente", (object?)venteId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("u", utilisateurId);
            cmd.Parameters.AddWithValue("ref", reference);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = new NpgsqlCommand("UPDATE patient SET solde_credit = solde_credit + @delta WHERE id = @id", cx, tx))
        {
            cmd.Parameters.AddWithValue("delta", delta);
            cmd.Parameters.AddWithValue("id", patientId);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
