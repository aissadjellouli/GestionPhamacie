using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Synchronisation;

namespace Pharma.Infrastructure.Synchronisation;

/// <summary>Ordres reçus du serveur central (script 145) : lecture, liste paginée, changement d'état par sync_ordre_noter.</summary>
public sealed class OrdresCentrauxSql(NpgsqlDataSource db) : IOrdresCentrauxDepot
{
    private const string Colonnes = """
        id, type, cible, attendu::text, donnees::text, auteur, cree_le, expire_le, statut, motif, recu_le, traite_le, traite_par
        """;

    public async Task<IReadOnlyList<OrdreRecu>> EnCoursAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Colonnes} FROM sync_ordre_recu WHERE statut IN ('a_appliquer', 'a_confirmer') ORDER BY recu_le, id");
        return await LireListeAsync(cmd, ct);
    }

    public async Task<OrdreRecu?> LireAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Colonnes} FROM sync_ordre_recu WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        return (await LireListeAsync(cmd, ct)).FirstOrDefault();
    }

    public async Task<PageOrdresRecus> ListerAsync(string? statut, int page, int taille, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            SELECT {Colonnes} FROM sync_ordre_recu WHERE ($1::text IS NULL OR statut = $1)
            ORDER BY (statut = 'a_confirmer') DESC, recu_le DESC, id LIMIT $2 OFFSET $3
            """, cnx);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)statut ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(taille);
        cmd.Parameters.AddWithValue((page - 1) * taille);
        var ordres = await LireListeAsync(cmd, ct);
        await using var compte = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE $1::text IS NULL OR statut = $1), count(*) FILTER (WHERE statut = 'a_confirmer') FROM sync_ordre_recu
            """, cnx);
        compte.Parameters.Add(new NpgsqlParameter { Value = (object?)statut ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await using var r = await compte.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new PageOrdresRecus(ordres, (int)r.GetInt64(0), (int)r.GetInt64(1));
    }

    public async Task NoterAsync(Guid id, string statut, string? motif, long? par, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_ordre_noter($1, $2, $3, $4)");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(statut);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)motif ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)par ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> BaseEnLectureSeuleAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT pg_is_in_recovery()");
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> ModifierPrixAsync(long produitId, decimal? prix, decimal? tarif, decimal prixAttendu, decimal? tarifAttendu, long? par,
        string auteurCentral, Guid ordre, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            WITH avant AS (SELECT id, nom_commercial, prix_public, tarif_reference FROM produit WHERE id = $1 FOR UPDATE)
            UPDATE produit p SET prix_public = coalesce($2, p.prix_public), tarif_reference = coalesce($3, p.tarif_reference)
            FROM avant WHERE p.id = avant.id AND avant.prix_public = $4 AND avant.tarif_reference IS NOT DISTINCT FROM $5
            RETURNING avant.nom_commercial, avant.prix_public, avant.tarif_reference, p.prix_public, p.tarif_reference
            """, cnx, tx);
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)prix ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)tarif ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric });
        cmd.Parameters.AddWithValue(prixAttendu);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)tarifAttendu ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric });
        string nom;
        decimal prixAvant, prixApres;
        decimal? tarifAvant, tarifApres;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return false;
            (nom, prixAvant, tarifAvant, prixApres, tarifApres) = (r.GetString(0), r.GetDecimal(1), r.IsDBNull(2) ? null : r.GetDecimal(2),
                r.GetDecimal(3), r.IsDBNull(4) ? null : r.GetDecimal(4));
        }
        await using var j = new NpgsqlCommand("""
            INSERT INTO journal_audit (utilisateur_id, action, table_cible, enregistrement_id, details)
            VALUES ($1, 'prix_modifie', 'produit', $2, $3::jsonb)
            """, cnx, tx);
        j.Parameters.Add(new NpgsqlParameter { Value = (object?)par ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        j.Parameters.AddWithValue(produitId);
        j.Parameters.AddWithValue(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["produit"] = nom, ["avant"] = prixAvant, ["apres"] = prixApres, ["tarif_avant"] = tarifAvant, ["tarif_apres"] = tarifApres,
            ["source"] = "ordre du serveur central", ["auteur_central"] = auteurCentral, ["ordre"] = ordre
        }));
        await j.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<string> CreerCommandeAsync(long fournisseurId, IReadOnlyList<Pharma.Domain.Achats.LigneCommandeSaisie> lignes, string auteurCentral,
        Guid ordre, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        // Verrou de l'ordre : deux passages simultanés ne créent pas deux bons
        await using (var verrou = new NpgsqlCommand("SELECT 1 FROM sync_ordre_recu WHERE id = $1 FOR UPDATE", cnx, tx))
        {
            verrou.Parameters.AddWithValue(ordre);
            await verrou.ExecuteNonQueryAsync(ct);
        }
        await using (var deja = new NpgsqlCommand("""
            SELECT c.numero FROM commande_ordre_central x JOIN commande_fournisseur c ON c.id = x.commande_id WHERE x.ordre_id = $1
            """, cnx, tx))
        {
            deja.Parameters.AddWithValue(ordre);
            if (await deja.ExecuteScalarAsync(ct) is string existant)
            {
                await tx.CommitAsync(ct);
                return existant;
            }
        }
        long id;
        string numero;
        await using (var cree = new NpgsqlCommand("""
            INSERT INTO commande_fournisseur (fournisseur_id, numero, utilisateur_id, montant_total)
            VALUES ($1, prochain_numero_commande(), NULL, $2) RETURNING id, numero
            """, cnx, tx))
        {
            cree.Parameters.AddWithValue(fournisseurId);
            cree.Parameters.AddWithValue(lignes.Sum(l => l.Quantite * l.PrixAchatHt));
            await using var r = await cree.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            (id, numero) = (r.GetInt64(0), r.GetString(1));
        }
        await using (var l = new NpgsqlCommand("""
            INSERT INTO ligne_commande (commande_id, produit_id, qte_commandee, prix_achat_ht)
            SELECT $1, p, q, x FROM unnest($2::bigint[], $3::int[], $4::numeric[]) AS t(p, q, x)
            """, cnx, tx))
        {
            l.Parameters.AddWithValue(id);
            l.Parameters.AddWithValue(lignes.Select(x => x.ProduitId).ToArray());
            l.Parameters.AddWithValue(lignes.Select(x => x.Quantite).ToArray());
            l.Parameters.AddWithValue(lignes.Select(x => x.PrixAchatHt).ToArray());
            await l.ExecuteNonQueryAsync(ct);
        }
        await using (var lien = new NpgsqlCommand("INSERT INTO commande_ordre_central (commande_id, ordre_id, auteur) VALUES ($1, $2, $3)", cnx, tx))
        {
            lien.Parameters.AddWithValue(id);
            lien.Parameters.AddWithValue(ordre);
            lien.Parameters.AddWithValue(auteurCentral);
            await lien.ExecuteNonQueryAsync(ct);
        }
        await using (var j = new NpgsqlCommand("""
            INSERT INTO journal_audit (utilisateur_id, action, table_cible, enregistrement_id, details)
            VALUES (NULL, 'commande_preparee_central', 'commande_fournisseur', $1, $2::jsonb)
            """, cnx, tx))
        {
            j.Parameters.AddWithValue(id);
            j.Parameters.AddWithValue(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["numero"] = numero, ["lignes"] = lignes.Count, ["montant_ht"] = lignes.Sum(x => x.Quantite * x.PrixAchatHt),
                ["source"] = "ordre du serveur central", ["auteur_central"] = auteurCentral, ["ordre"] = ordre
            }));
            await j.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return numero;
    }

    private static async Task<IReadOnlyList<OrdreRecu>> LireListeAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<OrdreRecu>();
        while (await r.ReadAsync(ct))
            liste.Add(new OrdreRecu(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                Utc(r.GetDateTime(6)), Utc(r.GetDateTime(7)), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), Utc(r.GetDateTime(10)),
                r.IsDBNull(11) ? null : Utc(r.GetDateTime(11)), r.IsDBNull(12) ? null : r.GetInt64(12)));
        return liste;
    }

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
}
