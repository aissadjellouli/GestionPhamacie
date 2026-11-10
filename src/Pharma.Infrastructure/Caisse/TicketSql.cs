using Npgsql;
using Pharma.Application.Caisse;

namespace Pharma.Infrastructure.Caisse;

public sealed class TicketSql(NpgsqlDataSource db) : ITicketLecture
{
    public async Task<TicketVente?> LireAsync(long venteId, CancellationToken ct = default)
    {
        TicketVente? t;
        await using (var cmd = db.CreateCommand("""
            SELECT v.numero, v.date_vente, u.nom_complet, s.poste, v.statut, o.code, v.taux_tiers_payant,
                   v.montant_total + v.montant_remise, v.montant_remise, v.part_organisme, v.part_patient,
                   (SELECT p.mode FROM paiement p WHERE p.vente_id = v.id ORDER BY p.id LIMIT 1)
            FROM vente v
            JOIN utilisateur u ON u.id = v.utilisateur_id
            LEFT JOIN session_caisse s ON s.id = v.session_caisse_id
            LEFT JOIN organisme o ON o.id = v.organisme_id
            WHERE v.id = $1
            """))
        {
            cmd.Parameters.AddWithValue(venteId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            t = new TicketVente(r.GetString(0), r.GetDateTime(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetDecimal(6), [],
                r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), r.GetDecimal(10), r.IsDBNull(11) ? null : r.GetString(11),
                null, null, null, null);
        }

        var lignes = new List<LigneTicket>();
        await using (var cmd = db.CreateCommand("""
            SELECT p.nom_commercial, l.quantite, l.prix_unitaire, l.prix_unitaire * l.quantite, l.montant_organisme
            FROM ligne_vente l JOIN produit p ON p.id = l.produit_id
            WHERE l.vente_id = $1 ORDER BY l.id
            """))
        {
            cmd.Parameters.AddWithValue(venteId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                lignes.Add(new LigneTicket(r.GetString(0), r.GetInt32(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4)));
        }
        return t with { Lignes = lignes };
    }
}
