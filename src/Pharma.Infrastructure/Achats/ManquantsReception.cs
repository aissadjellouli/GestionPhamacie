using Npgsql;

namespace Pharma.Infrastructure.Achats;

/// <summary>
/// Réception à l'aveugle, écart validé : boîtes facturées absentes du carton sorties du stock dans la transaction de la réception
/// (boîte « manquante », mouvement manquant_reception, à réclamer au grossiste). Valeur au prix d'achat.
/// </summary>
internal static class ManquantsReception
{
    public static async Task<decimal> SortirAsync(NpgsqlConnection cnx, NpgsqlTransaction tx, long receptionId, string numeroBl,
        IEnumerable<(long LotId, int Boites)> manquants, long utilisateurId, CancellationToken ct)
    {
        var total = 0m;
        var reference = $"BL {numeroBl} : manquant à la réception, à réclamer au grossiste";
        foreach (var (lotId, boites) in manquants)
        {
            // Boîtes de cette livraison sans Data Matrix (non vues au comptage), les plus chères d'abord (facturées avant les UG)
            int n;
            decimal valeur;
            await using (var cmd = new NpgsqlCommand("""
                WITH choisies AS (
                    SELECT id, prix_achat_ht FROM boite
                    WHERE lot_id = $1 AND reception_id = $2 AND statut = 'en_stock' AND numero_serie IS NULL
                    ORDER BY prix_achat_ht DESC NULLS LAST, id DESC LIMIT $3 FOR UPDATE),
                maj AS (UPDATE boite SET statut = 'manquante' WHERE id IN (SELECT id FROM choisies) RETURNING id, prix_achat_ht),
                ev AS (INSERT INTO boite_evenement (boite_id, type, utilisateur_id, commentaire) SELECT id, 'manquant_reception', $4, $5 FROM maj)
                SELECT count(*)::int, COALESCE(sum(prix_achat_ht), 0) FROM maj
                """, cnx, tx))
            {
                cmd.Parameters.AddWithValue(lotId);
                cmd.Parameters.AddWithValue(receptionId);
                cmd.Parameters.AddWithValue(boites);
                cmd.Parameters.AddWithValue(utilisateurId);
                cmd.Parameters.AddWithValue(reference);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                (n, valeur) = (r.GetInt32(0), r.GetDecimal(1));
            }
            // Toutes les boîtes manquantes doivent sortir : sinon la réception entière est annulée
            if (n != boites)
                throw new Pharma.Domain.Commun.RegleMetierException(
                    $"BL {numeroBl} : {boites} boîte(s) manquante(s) à sortir, seulement {n} boîte(s) sans Data Matrix dans la livraison. Réception annulée.");
            await using (var cmd = new NpgsqlCommand("UPDATE lot SET quantite_disponible = quantite_disponible - $2 WHERE id = $1", cnx, tx))
            {
                cmd.Parameters.AddWithValue(lotId);
                cmd.Parameters.AddWithValue(n);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using (var cmd = new NpgsqlCommand("""
                INSERT INTO mouvement_stock (lot_id, type, quantite, reference, utilisateur_id, reception_id, prix_achat_ht)
                VALUES ($1, 'manquant_reception', $2, $3, $4, $5, $6)
                """, cnx, tx))
            {
                cmd.Parameters.AddWithValue(lotId);
                cmd.Parameters.AddWithValue(-n);
                cmd.Parameters.AddWithValue(reference);
                cmd.Parameters.AddWithValue(utilisateurId);
                cmd.Parameters.AddWithValue(receptionId);
                cmd.Parameters.AddWithValue(Math.Round(valeur / n, 2, MidpointRounding.AwayFromZero));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            total += valeur;
        }
        return total;
    }
}
