using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Mobile;

namespace Pharma.Infrastructure.Lecture;

/// <summary>Scanner mobile : lecture des codes, fiche produit en rayon, dates confirmées, scans de réception en attente.</summary>
public sealed class MobileSql(NpgsqlDataSource db) : IMobile
{
    public async Task<CodeResolu?> ResoudreCodeAsync(string code, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT c.produit_id, c.lot_id, c.source,
                   (SELECT b.statut FROM boite b WHERE b.code_etiquette = $1)
            FROM rechercher_par_code($1) c LIMIT 1
            """);
        cmd.Parameters.AddWithValue(code);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var type = r.GetString(2) switch { "etiquette_boite" => "boite", "code_interne" => "lot", _ => "produit" };
        return new CodeResolu(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), type, r.IsDBNull(3) ? null : r.GetString(3));
    }

    public async Task<CodeResolu?> ResoudreDataMatrixAsync(string gtin, string? numeroLot, string? numeroSerie, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            WITH p AS (
                SELECT id FROM produit
                WHERE code_barre = $1 OR (length($1) = 14 AND left($1, 1) = '0' AND code_barre = substr($1, 2))
                ORDER BY actif DESC LIMIT 1)
            SELECT p.id,
                   COALESCE((SELECT b.lot_id FROM boite b WHERE b.gtin = $1 AND b.numero_serie = $3),
                            (SELECT l.id FROM lot l WHERE l.produit_id = p.id AND l.numero_lot = $2
                             ORDER BY l.quantite_disponible DESC, l.id LIMIT 1)),
                   (SELECT b.statut FROM boite b WHERE b.gtin = $1 AND b.numero_serie = $3)
            FROM p
            """);
        cmd.Parameters.AddWithValue(gtin);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)numeroLot ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)numeroSerie ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var statut = r.IsDBNull(2) ? null : r.GetString(2);
        long? lot = r.IsDBNull(1) ? null : r.GetInt64(1);
        return new CodeResolu(r.GetInt64(0), lot, statut is not null ? "boite" : lot is not null ? "lot" : "produit", statut);
    }

    public async Task<FicheProduit?> FicheAsync(long produitId, CancellationToken ct = default)
    {
        FicheProduit? fiche;
        await using (var cmd = db.CreateCommand("""
            SELECT p.id, p.nom_commercial, d.nom, p.dosage, p.forme, p.prix_public,
                   COALESCE((SELECT sum(quantite_disponible) FROM lot WHERE produit_id = p.id), 0)::int,
                   p.emplacement_id, ev.code, p.reserve_id, er.code
            FROM produit p
            LEFT JOIN dci d ON d.id = p.dci_id
            LEFT JOIN emplacement ev ON ev.id = p.emplacement_id
            LEFT JOIN emplacement er ON er.id = p.reserve_id
            WHERE p.id = $1
            """))
        {
            cmd.Parameters.AddWithValue(produitId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            fiche = new FicheProduit(r.GetInt64(0), r.GetString(1), Texte(r, 2), Texte(r, 3), Texte(r, 4), r.GetDecimal(5), r.GetInt32(6),
                r.IsDBNull(7) ? null : r.GetInt64(7), Texte(r, 8), r.IsDBNull(9) ? null : r.GetInt64(9), Texte(r, 10), []);
        }

        await using var lots = db.CreateCommand("""
            SELECT l.id, l.numero_lot, l.date_peremption, l.date_a_verifier, l.quantite_disponible, l.bloque, l.motif_blocage,
                   f.raison_sociale, COALESCE(v.niveau, 'ok')
            FROM lot l
            JOIN fournisseur f ON f.id = l.fournisseur_id
            -- Filtre par produit dans la vue : le calcul (fenêtre par produit) se limite aux lots de ce produit
            LEFT JOIN (SELECT lot_id, niveau FROM v_risque_peremption WHERE produit_id = $1) v ON v.lot_id = l.id
            WHERE l.produit_id = $1 AND l.quantite_disponible > 0
            ORDER BY l.date_peremption, l.id
            """);
        lots.Parameters.AddWithValue(produitId);
        await using var rl = await lots.ExecuteReaderAsync(ct);
        var liste = new List<LotFiche>();
        while (await rl.ReadAsync(ct))
            liste.Add(new LotFiche(rl.GetInt64(0), rl.GetString(1), rl.GetFieldValue<DateOnly>(2), rl.GetBoolean(3), rl.GetInt32(4),
                rl.GetBoolean(5), Texte(rl, 6), rl.GetString(7), rl.GetString(8)));
        return fiche with { Lots = liste };
    }

    public async Task<LotAConfirmer?> LotAsync(long lotId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT l.id, p.nom_commercial, l.numero_lot, l.date_a_verifier FROM lot l JOIN produit p ON p.id = l.produit_id WHERE l.id = $1
            """);
        cmd.Parameters.AddWithValue(lotId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new LotAConfirmer(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetBoolean(3)) : null;
    }

    public async Task ConfirmerPeremptionAsync(long lotId, DateOnly date, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE lot SET date_peremption = $2, date_a_verifier = false WHERE id = $1 AND date_a_verifier");
        cmd.Parameters.AddWithValue(lotId);
        cmd.Parameters.AddWithValue(date);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> AjouterScanAsync(NouveauScanReception s, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO scan_reception (utilisateur_id, produit_id, code_brut, gtin, numero_lot, date_peremption, numero_serie)
            VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING id
            """);
        cmd.Parameters.AddWithValue(s.UtilisateurId);
        cmd.Parameters.AddWithValue(s.ProduitId);
        cmd.Parameters.AddWithValue(s.CodeBrut);
        cmd.Parameters.Add(Nul(s.Gtin, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nul(s.NumeroLot, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nul(s.Peremption, NpgsqlDbType.Date));
        cmd.Parameters.Add(Nul(s.NumeroSerie, NpgsqlDbType.Text));
        try
        {
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new Pharma.Domain.Commun.RegleMetierException($"Boîte {s.NumeroSerie} déjà scannée.");
        }
    }

    private const string SelectScans = """
        SELECT s.id, s.produit_id, p.nom_commercial, s.gtin, s.numero_lot, s.date_peremption, s.numero_serie, u.nom_complet, s.scanne_le
        FROM scan_reception s JOIN produit p ON p.id = s.produit_id JOIN utilisateur u ON u.id = s.utilisateur_id
        """;

    public async Task<IReadOnlyList<ScanReception>> ScansEnAttenteAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"{SelectScans} WHERE s.importe_le IS NULL ORDER BY s.id DESC");
        return await Lire(cmd, ct);
    }

    public async Task<bool> SupprimerScanAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("DELETE FROM scan_reception WHERE id = $1 AND importe_le IS NULL");
        cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<ScanReception>> ImporterScansAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            WITH importes AS (
                UPDATE scan_reception SET importe_le = now() WHERE importe_le IS NULL RETURNING id)
            {SelectScans} JOIN importes i ON i.id = s.id ORDER BY s.id
            """);
        return await Lire(cmd, ct);
    }

    public async Task<int> RemettreEnAttenteAsync(IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        // Pas de doublon : une boîte sérialisée déjà rescannée ou déjà entrée en stock n'est pas remise
        await using var cmd = db.CreateCommand("""
            UPDATE scan_reception s SET importe_le = NULL
            WHERE s.id = ANY($1) AND s.importe_le IS NOT NULL
              AND NOT (s.numero_serie IS NOT NULL AND (
                  EXISTS (SELECT 1 FROM scan_reception a WHERE a.importe_le IS NULL AND a.gtin = s.gtin AND a.numero_serie = s.numero_serie)
                  OR EXISTS (SELECT 1 FROM boite b WHERE b.gtin = s.gtin AND b.numero_serie = s.numero_serie)))
            """);
        cmd.Parameters.AddWithValue(ids.ToArray());
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<ScanReception>> Lire(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<ScanReception>();
        while (await r.ReadAsync(ct))
            liste.Add(new ScanReception(r.GetInt64(0), r.GetInt64(1), r.GetString(2), Texte(r, 3), Texte(r, 4),
                r.IsDBNull(5) ? null : r.GetFieldValue<DateOnly>(5), Texte(r, 6), r.GetString(7), r.GetDateTime(8)));
        return liste;
    }

    private static string? Texte(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static NpgsqlParameter Nul(object? valeur, NpgsqlDbType type) => new() { Value = valeur ?? DBNull.Value, NpgsqlDbType = type };
}
