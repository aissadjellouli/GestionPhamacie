using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Achats;
using Pharma.Domain.Achats;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Achats;

/// <summary>Règlements des grossistes (029_reglements.sql).</summary>
public sealed class ReglementsFournisseursSql(NpgsqlDataSource db) : IReglementsFournisseurs
{
    public async Task<IReadOnlyList<GrossisteAReglement>> GrossistesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT f.id, f.raison_sociale, f.solde,
                   COALESCE((SELECT SUM(reste) FROM v_facture_fournisseur v WHERE v.fournisseur_id = f.id AND v.reste > 0), 0),
                   (SELECT count(*) FROM v_facture_fournisseur v WHERE v.fournisseur_id = f.id AND v.reste > 0)::int
            FROM fournisseur f
            ORDER BY f.solde DESC, f.raison_sociale
            """);
        var liste = new List<GrossisteAReglement>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new GrossisteAReglement(r.GetInt64(0), r.GetString(1), r.GetDecimal(2), r.GetDecimal(3), r.GetInt32(4)));
        return liste;
    }

    public async Task<SituationGrossiste?> SituationAsync(long fournisseurId, CancellationToken ct = default)
    {
        string nom;
        decimal solde;
        await using (var cmd = db.CreateCommand("SELECT raison_sociale, solde FROM fournisseur WHERE id = $1"))
        {
            cmd.Parameters.AddWithValue(fournisseurId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            (nom, solde) = (r.GetString(0), r.GetDecimal(1));
        }

        var factures = new List<FactureFournisseur>();
        await using (var cmd = db.CreateCommand("""
            SELECT reception_id, numero_facture, numero_bl, date_reception, montant, regle, reste
            FROM v_facture_fournisseur WHERE fournisseur_id = $1 AND reste > 0 ORDER BY date_reception, reception_id
            """))
        {
            cmd.Parameters.AddWithValue(fournisseurId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                factures.Add(new FactureFournisseur(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                    r.GetDateTime(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6)));
        }

        var reglements = new List<ReglementPasse>();
        await using (var cmd = db.CreateCommand("""
            SELECT g.id, g.date_reglement, g.montant, g.moyen, g.reference, g.echeance, g.part_tiroir, u.nom_complet
            FROM reglement_fournisseur g JOIN utilisateur u ON u.id = g.utilisateur_id
            WHERE g.fournisseur_id = $1 ORDER BY g.date_reglement DESC, g.id DESC LIMIT 20
            """))
        {
            cmd.Parameters.AddWithValue(fournisseurId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                reglements.Add(new ReglementPasse(r.GetInt64(0), DateOnly.FromDateTime(r.GetDateTime(1)), r.GetDecimal(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : DateOnly.FromDateTime(r.GetDateTime(5)), r.GetDecimal(6), r.GetString(7)));
        }
        return new SituationGrossiste(fournisseurId, nom, solde, factures, reglements);
    }

    public async Task<long> EnregistrerAsync(ReglementAEcrire g, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);

        // Grossiste verrouillé : deux règlements simultanés ne paient pas deux fois la même facture
        await using (var cmd = new NpgsqlCommand("SELECT solde FROM fournisseur WHERE id = $1 FOR UPDATE", cx, tx))
        {
            cmd.Parameters.AddWithValue(g.FournisseurId);
            if (await cmd.ExecuteScalarAsync(ct) is not decimal solde || g.Montant > Math.Max(solde, 0) + 0.005m)
                throw new RegleMetierException("La dette envers ce grossiste vient de changer : rechargez sa situation.");
        }
        foreach (var (receptionId, montant) in g.Imputations)
        {
            await using var cmd = new NpgsqlCommand("SELECT reste FROM v_facture_fournisseur WHERE reception_id = $1 AND fournisseur_id = $2", cx, tx);
            cmd.Parameters.AddWithValue(receptionId);
            cmd.Parameters.AddWithValue(g.FournisseurId);
            if (await cmd.ExecuteScalarAsync(ct) is not decimal reste || reste < montant)
                throw new RegleMetierException("Une facture vient d’être réglée entre-temps : rechargez la situation du grossiste.");
        }

        long id;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO reglement_fournisseur (fournisseur_id, date_reglement, montant, moyen, reference, banque, echeance, part_tiroir,
                                               session_caisse_id, utilisateur_id, commentaire, compte_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12) RETURNING id
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(g.FournisseurId);
            cmd.Parameters.AddWithValue(g.Date);
            cmd.Parameters.AddWithValue(g.Montant);
            cmd.Parameters.AddWithValue(g.Moyen.Code());
            cmd.Parameters.Add(Texte(g.Reference));
            cmd.Parameters.Add(Texte(g.Banque));
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)g.Echeance ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
            cmd.Parameters.AddWithValue(g.PartTiroir);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)g.SessionCaisseId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            cmd.Parameters.AddWithValue(g.UtilisateurId);
            cmd.Parameters.Add(Texte(g.Commentaire));
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)g.CompteId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        foreach (var (receptionId, montant) in g.Imputations)
        {
            await using var cmd = new NpgsqlCommand("INSERT INTO reglement_facture (reglement_id, reception_id, montant) VALUES ($1, $2, $3)", cx, tx);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(receptionId);
            cmd.Parameters.AddWithValue(montant);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand("UPDATE fournisseur SET solde = solde - $2 WHERE id = $1", cx, tx))
        {
            cmd.Parameters.AddWithValue(g.FournisseurId);
            cmd.Parameters.AddWithValue(g.Montant);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return id;
    }

    private static NpgsqlParameter Texte(string? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };
}
