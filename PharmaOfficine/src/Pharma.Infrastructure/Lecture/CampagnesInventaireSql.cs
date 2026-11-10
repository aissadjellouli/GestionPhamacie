using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Stock;
using Pharma.Domain.Commun;
using Pharma.Domain.Stock;

namespace Pharma.Infrastructure.Lecture;

/// <summary>Inventaire complet (032_inventaire_complet.sql) : campagne qui regroupe les inventaires de rayon.</summary>
public sealed class CampagnesInventaireSql(NpgsqlDataSource db) : ICampagnesInventaire
{
    // Même rattachement des lots aux rayons que l'inventaire tournant
    private const string LotsParRayon = """
        SELECT COALESCE(ev.rayon, er.rayon, '(Sans emplacement)') AS rayon, l.id AS lot_id
        FROM lot l
        JOIN produit p ON p.id = l.produit_id
        LEFT JOIN emplacement ev ON ev.id = p.emplacement_id
        LEFT JOIN emplacement er ON er.id = p.reserve_id
        WHERE l.quantite_disponible > 0
        """;

    private const string SelectCampagne = """
        SELECT c.id, c.mode, c.statut, c.commence_le, u.nom_complet, c.cloture_le, v.nom_complet, c.nombre_lots, c.nombre_boites, c.valeur_stock, c.commentaire
        FROM campagne_inventaire c
        JOIN utilisateur u ON u.id = c.commence_par
        LEFT JOIN utilisateur v ON v.id = c.cloture_par
        """;

    public async Task<CampagneInventaire?> EnCoursAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"{SelectCampagne} WHERE c.statut = 'en_cours'");
        return (await Lire(cmd, ct)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<CampagneInventaire>> HistoriqueAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"{SelectCampagne} ORDER BY c.commence_le DESC LIMIT 20");
        return await Lire(cmd, ct);
    }

    private static async Task<IReadOnlyList<CampagneInventaire>> Lire(NpgsqlCommand cmd, CancellationToken ct)
    {
        var liste = new List<CampagneInventaire>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new CampagneInventaire(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDateTime(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetDateTime(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetInt32(7),
                r.IsDBNull(8) ? null : r.GetInt32(8), r.IsDBNull(9) ? null : r.GetDecimal(9), r.IsDBNull(10) ? null : r.GetString(10)));
        return liste;
    }

    public async Task<long> CommencerAsync(string mode, long utilisateurId, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("SELECT string_agg(rayon, ', ') FROM inventaire WHERE statut = 'en_cours'", cx, tx))
            if (await cmd.ExecuteScalarAsync(ct) is string rayons)
                throw new RegleMetierException($"Inventaire tournant en cours ({rayons}) : terminez-le ou annulez-le avant l’inventaire complet.");
        await using (var cmd = new NpgsqlCommand("INSERT INTO campagne_inventaire (mode, commence_par) VALUES ($1, $2) RETURNING id", cx, tx))
        {
            cmd.Parameters.AddWithValue(mode);
            cmd.Parameters.AddWithValue(utilisateurId);
            try
            {
                var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
                await tx.CommitAsync(ct);
                return id;
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new RegleMetierException("Un inventaire complet est déjà en cours.");
            }
        }
    }

    public async Task<IReadOnlyList<RayonCampagne>> RayonsAsync(long campagneId, CancellationToken ct = default)
    {
        // Rayons qui ont du stock, plus ceux déjà comptés dans la campagne ; dernier inventaire du rayon dans la campagne
        await using var cmd = db.CreateCommand($"""
            WITH lots AS ({LotsParRayon}),
                 rayons AS (SELECT rayon FROM lots UNION SELECT rayon FROM inventaire WHERE campagne_id = $1 AND statut <> 'annule'),
                 dernier AS (SELECT DISTINCT ON (rayon) id, rayon, statut FROM inventaire
                             WHERE campagne_id = $1 AND statut <> 'annule' ORDER BY rayon, commence_le DESC)
            SELECT r.rayon,
                   (SELECT count(*) FROM lots WHERE lots.rayon = r.rayon)::int,
                   d.id, COALESCE(d.statut, 'a_compter'),
                   (SELECT count(*) FROM ligne_inventaire li JOIN inventaire i ON i.id = li.inventaire_id
                    WHERE i.campagne_id = $1 AND i.rayon = r.rayon AND li.statut = 'a_valider')::int
            FROM rayons r LEFT JOIN dernier d ON d.rayon = r.rayon
            ORDER BY r.rayon = '(Sans emplacement)', r.rayon
            """);
        cmd.Parameters.AddWithValue(campagneId);
        var liste = new List<RayonCampagne>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new RayonCampagne(r.GetString(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetInt64(2),
                r.GetString(3) == "termine" ? "termine" : r.GetString(3) == "en_cours" ? "en_cours" : "a_compter", r.GetInt32(4)));
        return liste;
    }

    public async Task CloturerAsync(long campagneId, long utilisateurId, string? commentaire, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand($"""
            INSERT INTO campagne_inventaire_ligne (campagne_id, lot_id, produit, numero_lot, date_peremption, rayon, quantite, prix_achat_ht, valeur)
            SELECT $1, l.id, p.nom_commercial, l.numero_lot, l.date_peremption, lots.rayon, l.quantite_disponible, l.prix_achat_ht,
                   round(l.quantite_disponible * l.prix_achat_ht, 2)
            FROM ({LotsParRayon}) lots JOIN lot l ON l.id = lots.lot_id JOIN produit p ON p.id = l.produit_id
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(campagneId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand("""
            UPDATE campagne_inventaire c SET statut = 'cloturee', cloture_le = now(), cloture_par = $2, commentaire = $3,
                   nombre_lots = (SELECT count(*) FROM campagne_inventaire_ligne x WHERE x.campagne_id = c.id),
                   nombre_boites = (SELECT COALESCE(sum(quantite), 0) FROM campagne_inventaire_ligne x WHERE x.campagne_id = c.id),
                   valeur_stock = (SELECT COALESCE(sum(valeur), 0) FROM campagne_inventaire_ligne x WHERE x.campagne_id = c.id)
            WHERE c.id = $1 AND c.statut = 'en_cours'
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(campagneId);
            cmd.Parameters.AddWithValue(utilisateurId);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)commentaire ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new RegleMetierException("Cet inventaire complet n’est plus en cours.");
        }
        await tx.CommitAsync(ct);
    }

    public async Task AnnulerAsync(long campagneId, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("UPDATE inventaire SET statut = 'annule' WHERE campagne_id = $1 AND statut = 'en_cours'", cx, tx))
        {
            cmd.Parameters.AddWithValue(campagneId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand("UPDATE campagne_inventaire SET statut = 'annulee' WHERE id = $1 AND statut = 'en_cours'", cx, tx))
        {
            cmd.Parameters.AddWithValue(campagneId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<EtatInventaire?> EtatAsync(long campagneId, CancellationToken ct = default)
    {
        CampagneInventaire? c;
        await using (var cmd = db.CreateCommand($"{SelectCampagne} WHERE c.id = $1"))
        {
            cmd.Parameters.AddWithValue(campagneId);
            c = (await Lire(cmd, ct)).FirstOrDefault();
        }
        if (c is null) return null;
        var lignes = new List<LigneEtatInventaire>();
        await using (var cmd = db.CreateCommand("""
            SELECT produit, numero_lot, date_peremption, rayon, quantite, prix_achat_ht, valeur
            FROM campagne_inventaire_ligne WHERE campagne_id = $1 ORDER BY rayon = '(Sans emplacement)', rayon, produit, date_peremption
            """))
        {
            cmd.Parameters.AddWithValue(campagneId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                lignes.Add(new LigneEtatInventaire(r.GetString(0), r.GetString(1), DateOnly.FromDateTime(r.GetDateTime(2)), r.GetString(3), r.GetInt32(4),
                    r.GetDecimal(5), r.GetDecimal(6)));
        }
        int ecarts;
        decimal valeur;
        await using (var cmd = db.CreateCommand("""
            SELECT count(*)::int, COALESCE(sum(li.valeur_ecart), 0) FROM ligne_inventaire li JOIN inventaire i ON i.id = li.inventaire_id
            WHERE i.campagne_id = $1 AND li.statut IN ('ajuste', 'valide') AND li.ecart <> 0
            """))
        {
            cmd.Parameters.AddWithValue(campagneId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            (ecarts, valeur) = (r.GetInt32(0), r.GetDecimal(1));
        }
        return new EtatInventaire(c, lignes, ecarts, valeur);
    }
}
