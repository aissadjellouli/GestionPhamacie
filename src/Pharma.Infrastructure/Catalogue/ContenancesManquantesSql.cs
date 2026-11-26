using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Catalogue;
using Pharma.Application.Commun;
using Pharma.Infrastructure.Referentiels;

namespace Pharma.Infrastructure.Catalogue;

/// <summary>Écran Stock › Contenance manquante (2026-11-26) : produits actifs sans contenance, les plus vendus sur 12 mois d'abord.</summary>
public sealed class ContenancesManquantesSql(NpgsqlDataSource db) : IContenancesManquantes, IContenancesImport
{
    public async Task<IReadOnlyList<ProduitIdentifie>> ProduitsAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT id, nom_commercial, num_enregistrement, code_barre, contenance FROM produit");
        var liste = new List<ProduitIdentifie>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new ProduitIdentifie(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetInt32(4)));
        return liste;
    }

    public async Task<IReadOnlyList<(ContenanceManquante Produit, string? NumEnregistrement, string? CodeBarre)>> AExporterAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            WITH vendues AS (
                SELECT lv.produit_id, sum(lv.quantite)::int AS n
                FROM ligne_vente lv JOIN vente v ON v.id = lv.vente_id
                WHERE v.statut = 'validee' AND v.date_vente > now() - interval '12 months' AND lv.produit_id IS NOT NULL
                GROUP BY lv.produit_id
            ), stock AS (
                SELECT produit_id, sum(quantite_disponible)::int AS n FROM lot GROUP BY produit_id
            )
            SELECT p.id, p.nom_commercial, p.forme, p.dosage, p.conditionnement, coalesce(v.n, 0), coalesce(s.n, 0), p.num_enregistrement, p.code_barre
            FROM produit p
            LEFT JOIN vendues v ON v.produit_id = p.id
            LEFT JOIN stock s ON s.produit_id = p.id
            WHERE p.actif AND p.contenance IS NULL
            ORDER BY coalesce(v.n, 0) DESC, coalesce(s.n, 0) DESC, lower(p.nom_commercial), p.id
            """);
        var liste = new List<(ContenanceManquante, string?, string?)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add((new ContenanceManquante(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5), r.GetInt32(6)), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8)));
        return liste;
    }

    public async Task<Page<ContenanceManquante>> ListerAsync(string? recherche, DemandePage page, CancellationToken ct = default, bool toutes = false)
    {
        await using var cmd = db.CreateCommand("""
            WITH vendues AS (
                SELECT lv.produit_id, sum(lv.quantite)::int AS n
                FROM ligne_vente lv JOIN vente v ON v.id = lv.vente_id
                WHERE v.statut = 'validee' AND v.date_vente > now() - interval '12 months' AND lv.produit_id IS NOT NULL
                GROUP BY lv.produit_id
            ), stock AS (
                SELECT produit_id, sum(quantite_disponible)::int AS n FROM lot GROUP BY produit_id
            )
            SELECT p.id, p.nom_commercial, p.forme, p.dosage, p.conditionnement, coalesce(v.n, 0), coalesce(s.n, 0), count(*) OVER (), p.contenance
            FROM produit p
            LEFT JOIN vendues v ON v.produit_id = p.id
            LEFT JOIN stock s ON s.produit_id = p.id
            WHERE p.actif AND ($4 OR p.contenance IS NULL)
              AND ($1::text IS NULL OR p.nom_commercial ILIKE '%' || $1 || '%' ESCAPE '\' OR p.conditionnement ILIKE '%' || $1 || '%' ESCAPE '\')
            ORDER BY coalesce(v.n, 0) DESC, coalesce(s.n, 0) DESC, lower(p.nom_commercial), p.id
            LIMIT $2 OFFSET $3
            """);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)MotifRecherche.EchapperOuNull(recherche) ?? DBNull.Value });
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        cmd.Parameters.AddWithValue(toutes);
        var lignes = new List<ContenanceManquante>();
        var total = 0;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            lignes.Add(new ContenanceManquante(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5), r.GetInt32(6), r.IsDBNull(8) ? null : r.GetInt32(8)));
            total = (int)r.GetInt64(7);
        }
        return new Page<ContenanceManquante>(lignes, total, page.Page, page.Taille);
    }

    public async Task<string?> CorrigerAsync(long produitId, int? attendue, int contenance, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE produit SET contenance = $2 WHERE id = $1 AND contenance IS NOT DISTINCT FROM $3 RETURNING nom_commercial");
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.AddWithValue(contenance);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = (object?)attendue ?? DBNull.Value });
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<string?> EnregistrerSiVideAsync(long produitId, int contenance, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE produit SET contenance = $2 WHERE id = $1 AND contenance IS NULL RETURNING nom_commercial");
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.AddWithValue(contenance);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
}
