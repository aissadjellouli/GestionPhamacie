using Npgsql;
using Pharma.Application.Notifications;

namespace Pharma.Infrastructure.Notifications;

/// <summary>
/// Chiffres du rapport quotidien. Les requêtes ne lisent aucune colonne de patient ni d'ordonnance :
/// montants, employés, postes et produits seulement.
/// </summary>
public sealed class DonneesRapportSql(NpgsqlDataSource db) : IDonneesRapport
{
    public async Task<DonneesRapportQuotidien> LireAsync(DateOnly jour, CancellationToken ct = default)
    {
        // Journée en heure locale du PC serveur, convertie en UTC
        var debut = jour.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
        var fin = jour.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();

        int ventes = 0, annulations = 0;
        decimal ca = 0, tiersPayant = 0, annule = 0;
        await using (var cmd = db.CreateCommand("""
            SELECT count(*) FILTER (WHERE statut = 'validee')::int,
                   COALESCE(SUM(montant_total) FILTER (WHERE statut = 'validee'), 0),
                   COALESCE(SUM(part_organisme) FILTER (WHERE statut = 'validee'), 0),
                   count(*) FILTER (WHERE statut = 'annulee')::int,
                   COALESCE(SUM(montant_total) FILTER (WHERE statut = 'annulee'), 0)
            FROM vente WHERE date_vente >= $1 AND date_vente < $2
            """))
        {
            cmd.Parameters.AddWithValue(debut);
            cmd.Parameters.AddWithValue(fin);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
                (ventes, ca, tiersPayant, annulations, annule) = (r.GetInt32(0), r.GetDecimal(1), r.GetDecimal(2), r.GetInt32(3), r.GetDecimal(4));
        }

        var ecarts = new List<EcartEmploye>();
        await using (var cmd = db.CreateCommand("""
            SELECT u.nom_complet, s.poste, s.ecart
            FROM session_caisse s JOIN utilisateur u ON u.id = s.utilisateur_id
            WHERE s.fermee_le >= $1 AND s.fermee_le < $2 AND s.ecart IS NOT NULL
            ORDER BY abs(s.ecart) DESC, u.nom_complet
            """))
        {
            cmd.Parameters.AddWithValue(debut);
            cmd.Parameters.AddWithValue(fin);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ecarts.Add(new EcartEmploye(r.GetString(0), r.GetString(1), r.GetDecimal(2)));
        }

        var peremptions = new List<AlerteDuJour>();
        var ruptures = new List<AlerteDuJour>();
        await using (var cmd = db.CreateCommand("""
            SELECT a.type, p.nom_commercial, l.numero_lot, a.message
            FROM alerte a JOIN produit p ON p.id = a.produit_id LEFT JOIN lot l ON l.id = a.lot_id
            WHERE a.cree_le >= $1 AND a.cree_le < $2 AND a.type IN ('peremption', 'perime', 'risque_ecoulement', 'rupture')
            ORDER BY a.type, p.nom_commercial
            """))
        {
            cmd.Parameters.AddWithValue(debut);
            cmd.Parameters.AddWithValue(fin);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var a = new AlerteDuJour(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3));
                (a.Type == "rupture" ? ruptures : peremptions).Add(a);
            }
        }

        return new DonneesRapportQuotidien(jour, ventes, ca, tiersPayant, ecarts, annulations, annule, peremptions, ruptures);
    }
}
