using Npgsql;
using Pharma.Application.Caisse;

namespace Pharma.Infrastructure.Caisse;

/// <summary>
/// Reçu d'un règlement de crédit (paiement sans vente, numero_recu). Le reste dû est la dette inscrite au relevé par ce
/// règlement (mouvement « remboursement » écrit dans la même transaction, donc à la même heure).
/// </summary>
public sealed class RecusCreditSql(NpgsqlDataSource db) : IRecusCredit
{
    public async Task<RecuCreditEnregistre?> LireAsync(string numero, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT x.numero_recu, x.cree_le, x.montant, x.mode,
                   COALESCE((SELECT m.solde_apres FROM credit_mouvement m
                             WHERE m.patient_id = x.patient_id AND m.type = 'remboursement'
                               -- Mouvement retrouvé par le numéro du reçu inscrit dans sa référence (revue 2026-11-15, M5) ;
                               -- reçus plus anciens (sans numéro dans la référence) : même heure, dans la même transaction
                               AND (COALESCE(m.reference, '') LIKE ('% · reçu ' || x.numero_recu)
                                    OR (m.cree_le = x.cree_le AND COALESCE(m.reference, '') NOT LIKE '% · reçu %'))
                             ORDER BY (COALESCE(m.reference, '') LIKE ('% · reçu ' || x.numero_recu)) DESC, m.id DESC LIMIT 1), 0),
                   u.nom_complet, s.poste
            FROM paiement x
            LEFT JOIN utilisateur u ON u.id = x.utilisateur_id
            LEFT JOIN session_caisse s ON s.id = x.session_caisse_id
            WHERE x.numero_recu = $1 AND x.vente_id IS NULL
            """);
        cmd.Parameters.AddWithValue(numero);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new RecuCreditEnregistre(r.GetString(0), r.GetDateTime(1), r.GetDecimal(2), r.GetString(3), r.GetDecimal(4),
            r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6));
    }
}
