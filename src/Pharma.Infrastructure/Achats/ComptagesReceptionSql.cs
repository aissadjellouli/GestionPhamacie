using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Achats;
using Pharma.Domain.Achats;

namespace Pharma.Infrastructure.Achats;

/// <summary>Réception à l'aveugle : comptages, facture en écart, boîtes manquantes (database/migration/057_reception_aveugle.sql).</summary>
public sealed class ComptagesReceptionSql(NpgsqlDataSource db) : IComptagesReception
{
    private static NpgsqlParameter Valeur(object? v, NpgsqlDbType type) => new() { Value = v ?? DBNull.Value, NpgsqlDbType = type };

    public async Task<long> CreerAsync(long fournisseurId, string numeroBl, IReadOnlyList<LigneComptee> lignes, IReadOnlyList<long> scansMobile,
        long utilisateurId, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        long id;
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO comptage_reception (fournisseur_id, numero_bl, compte_par) VALUES ($1, $2, $3) RETURNING id", cnx, tx))
        {
            cmd.Parameters.AddWithValue(fournisseurId);
            cmd.Parameters.AddWithValue(numeroBl);
            cmd.Parameters.AddWithValue(utilisateurId);
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        var premiere = true;
        foreach (var l in lignes)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO comptage_reception_ligne (comptage_id, produit_id, numero_lot, date_peremption, gtin, quantite, numeros_serie, scans_mobile)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                """, cnx, tx);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(l.ProduitId);
            cmd.Parameters.AddWithValue(l.NumeroLot);
            cmd.Parameters.AddWithValue(l.DatePeremption);
            cmd.Parameters.Add(Valeur(l.Gtin, NpgsqlDbType.Text));
            cmd.Parameters.AddWithValue(l.Quantite);
            cmd.Parameters.AddWithValue((l.NumerosSerie ?? []).ToArray());
            // Scans du mobile repris dans ce comptage : gardés une fois (sur la première ligne)
            cmd.Parameters.AddWithValue(premiere ? scansMobile.ToArray() : Array.Empty<long>());
            premiere = false;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return id;
    }

    private const string Entete = """
        SELECT c.id, c.fournisseur_id, f.raison_sociale, c.numero_bl, c.statut, c.compte_par, uc.nom_complet, c.compte_le,
               c.facture_par, uf.nom_complet, c.facture_le, c.saisie::text, c.ecarts::text, c.boites_manquantes, c.boites_en_trop, c.valeur_manquante
        FROM comptage_reception c
        JOIN fournisseur f ON f.id = c.fournisseur_id
        JOIN utilisateur uc ON uc.id = c.compte_par
        LEFT JOIN utilisateur uf ON uf.id = c.facture_par
        """;

    [System.Reflection.Obfuscation(Exclude = true, ApplyToMembers = true)]
    private sealed record EcartJson(long ProduitId, string Nom, string NumeroLot, int Facture, int Compte);

    private async Task<List<ComptageReception>> LireAsync(string condition, Action<NpgsqlParameterCollection> parametres, CancellationToken ct)
    {
        var liste = new List<ComptageReception>();
        await using (var cmd = db.CreateCommand(Entete + condition))
        {
            parametres(cmd.Parameters);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var ecarts = r.IsDBNull(12) ? [] : JsonSerializer.Deserialize<List<EcartJson>>(r.GetString(12))!
                    .Select(e => new EcartComptage(e.ProduitId, e.Nom, e.NumeroLot, e.Facture, e.Compte)).ToList();
                liste.Add(new ComptageReception(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5),
                    r.GetString(6), r.GetDateTime(7), r.IsDBNull(8) ? null : r.GetInt64(8), r.IsDBNull(9) ? null : r.GetString(9),
                    r.IsDBNull(10) ? null : r.GetDateTime(10), [], [], r.IsDBNull(11) ? null : r.GetString(11), ecarts,
                    r.GetInt32(13), r.GetInt32(14), r.GetDecimal(15)));
            }
        }
        if (liste.Count == 0) return liste;

        // Lignes comptées
        var lignes = new Dictionary<long, List<LigneComptee>>();
        var scans = new Dictionary<long, List<long>>();
        await using (var cmd = db.CreateCommand("""
            SELECT l.comptage_id, l.produit_id, p.nom_commercial, l.numero_lot, l.date_peremption, l.quantite, l.gtin, l.numeros_serie, l.scans_mobile
            FROM comptage_reception_ligne l JOIN produit p ON p.id = l.produit_id
            WHERE l.comptage_id = ANY($1) ORDER BY l.id
            """))
        {
            cmd.Parameters.AddWithValue(liste.Select(c => c.Id).ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var cid = r.GetInt64(0);
                var series = r.GetFieldValue<string[]>(7);
                (lignes.TryGetValue(cid, out var ls) ? ls : lignes[cid] = []).Add(new LigneComptee(r.GetInt64(1), r.GetString(2), r.GetString(3),
                    DateOnly.FromDateTime(r.GetDateTime(4)), r.GetInt32(5), r.IsDBNull(6) ? null : r.GetString(6), series.Length > 0 ? series : null));
                (scans.TryGetValue(cid, out var ss) ? ss : scans[cid] = []).AddRange(r.GetFieldValue<long[]>(8));
            }
        }
        return liste.Select(c => c with { Lignes = lignes.GetValueOrDefault(c.Id) ?? [], ScansMobile = scans.GetValueOrDefault(c.Id) ?? [] }).ToList();
    }

    public async Task<ComptageReception?> LireAsync(long id, CancellationToken ct = default) =>
        (await LireAsync(" WHERE c.id = $1", p => p.AddWithValue(id), ct)).FirstOrDefault();

    public async Task<IReadOnlyList<ComptageReception>> EnCoursAsync(CancellationToken ct = default) =>
        await LireAsync(" WHERE c.statut IN ('compte', 'ecart', 'a_recompter') ORDER BY c.compte_le", _ => { }, ct);

    public async Task<bool> ChangerStatutAsync(long id, string attendu, string nouveau, long? facturePar, long? decidePar, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE comptage_reception
            SET statut = $3,
                facture_par = CASE WHEN $4::bigint IS NOT NULL THEN $4 WHEN $3 = 'compte' THEN NULL ELSE facture_par END,
                facture_le = CASE WHEN $4::bigint IS NOT NULL THEN now() WHEN $3 = 'compte' THEN NULL ELSE facture_le END,
                decide_par = COALESCE($5, decide_par), decide_le = CASE WHEN $5::bigint IS NOT NULL THEN now() ELSE decide_le END
            WHERE id = $1 AND statut = $2
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(attendu);
        cmd.Parameters.AddWithValue(nouveau);
        cmd.Parameters.Add(Valeur(facturePar, NpgsqlDbType.Bigint));
        cmd.Parameters.Add(Valeur(decidePar, NpgsqlDbType.Bigint));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> EnregistrerEcartAsync(long id, string saisie, FusionComptage fusion, decimal valeurManquante, long facturePar,
        CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE comptage_reception
            SET statut = 'ecart', saisie = $2::jsonb, ecarts = $3::jsonb, boites_manquantes = $4, boites_en_trop = $5, valeur_manquante = $6,
                facture_par = $7, facture_le = now()
            WHERE id = $1 AND statut = 'compte'
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(saisie);
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(fusion.Ecarts.Select(e => new EcartJson(e.ProduitId, e.Nom, e.NumeroLot, e.Facture, e.Compte))));
        cmd.Parameters.AddWithValue(fusion.BoitesManquantes);
        cmd.Parameters.AddWithValue(fusion.BoitesEnTrop);
        cmd.Parameters.AddWithValue(valeurManquante);
        cmd.Parameters.AddWithValue(facturePar);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> ComptageOuvertAsync(long fournisseurId, string numeroBl, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT EXISTS (SELECT 1 FROM comptage_reception
                           WHERE fournisseur_id = $1 AND statut IN ('compte', 'ecart', 'a_recompter')
                             AND regexp_replace(upper(numero_bl), '\s', '', 'g') = regexp_replace(upper($2), '\s', '', 'g'))
            """);
        cmd.Parameters.AddWithValue(fournisseurId);
        cmd.Parameters.AddWithValue(numeroBl);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> RecompterAsync(long id, IReadOnlyList<LigneComptee> lignes, FusionComptage fusion, decimal valeurManquante, long utilisateurId,
        CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        // Nouveau comptage : écart recalculé avec la facture gardée, de nouveau à décider
        await using (var cmd = new NpgsqlCommand("""
            UPDATE comptage_reception
            SET statut = 'ecart', compte_par = $2, compte_le = now(), ecarts = $3::jsonb, boites_manquantes = $4, boites_en_trop = $5,
                valeur_manquante = $6, decide_par = NULL, decide_le = NULL
            WHERE id = $1 AND statut = 'a_recompter'
            """, cnx, tx))
        {
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(utilisateurId);
            cmd.Parameters.AddWithValue(JsonSerializer.Serialize(fusion.Ecarts.Select(e => new EcartJson(e.ProduitId, e.Nom, e.NumeroLot, e.Facture, e.Compte))));
            cmd.Parameters.AddWithValue(fusion.BoitesManquantes);
            cmd.Parameters.AddWithValue(fusion.BoitesEnTrop);
            cmd.Parameters.AddWithValue(valeurManquante);
            if (await cmd.ExecuteNonQueryAsync(ct) != 1) return false;
        }
        await using (var cmd = new NpgsqlCommand("DELETE FROM comptage_reception_ligne WHERE comptage_id = $1", cnx, tx))
        {
            cmd.Parameters.AddWithValue(id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var l in lignes)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO comptage_reception_ligne (comptage_id, produit_id, numero_lot, date_peremption, gtin, quantite, numeros_serie)
                VALUES ($1, $2, $3, $4, $5, $6, $7)
                """, cnx, tx);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(l.ProduitId);
            cmd.Parameters.AddWithValue(l.NumeroLot);
            cmd.Parameters.AddWithValue(l.DatePeremption);
            cmd.Parameters.Add(Valeur(l.Gtin, NpgsqlDbType.Text));
            cmd.Parameters.AddWithValue(l.Quantite);
            cmd.Parameters.AddWithValue((l.NumerosSerie ?? []).ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return true;
    }
}
