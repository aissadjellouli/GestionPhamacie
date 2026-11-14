using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Catalogue;

namespace Pharma.Infrastructure.Lecture;

/// <summary>Lecture et modification de la fiche d'un produit (la DCI est créée si besoin).</summary>
public sealed class ProduitsModificationSql(NpgsqlDataSource db) : IProduitsModification
{
    public async Task<FicheProduitModifiable?> LireAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT p.id, p.nom_commercial, d.nom, p.dosage, p.forme, p.code_barre, p.prix_public, p.tarif_reference, p.remboursable,
                   p.taux_tva, p.actif, p.classe, p.num_enregistrement, p.classe_confirmee_par IS NOT NULL
            FROM produit p LEFT JOIN dci d ON d.id = p.dci_id WHERE p.id = $1
            """);
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        string? T(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new FicheProduitModifiable(r.GetInt64(0), r.GetString(1), T(2), T(3), T(4), T(5), r.GetDecimal(6),
            r.IsDBNull(7) ? null : r.GetDecimal(7), r.GetBoolean(8), r.GetDecimal(9), r.GetBoolean(10), r.GetString(11), T(12), r.GetBoolean(13));
    }

    public async Task<bool> CodeBarreAutreProduitAsync(string codeBarre, long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT EXISTS (SELECT 1 FROM produit WHERE code_barre = $1 AND id <> $2)");
        cmd.Parameters.AddWithValue(codeBarre);
        cmd.Parameters.AddWithValue(id);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public Task<string?> ClasseSurveilleeAsync(string dci, CancellationToken ct = default) => CatalogueEcritureSql.ClasseSurveilleeAsync(db, dci, ct);

    public Task<string?> ClasseNomenclatureAsync(string numEnregistrement, CancellationToken ct = default) =>
        CatalogueEcritureSql.ClasseNomenclatureAsync(db, numEnregistrement, ct);

    public async Task ModifierAsync(FicheProduitModifiable f, bool classeImposee, long utilisateurId, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        long? dciId = null;
        if (f.Dci is { } nomDci)
        {
            await using var dci = new NpgsqlCommand("""
                WITH existante AS (SELECT id FROM dci WHERE lower(nom) = lower($1)),
                     creee AS (INSERT INTO dci (nom) SELECT $1 WHERE NOT EXISTS (SELECT 1 FROM existante) RETURNING id)
                SELECT id FROM existante UNION ALL SELECT id FROM creee
                """, cnx, tx);
            dci.Parameters.AddWithValue(nomDci);
            dciId = (long)(await dci.ExecuteScalarAsync(ct))!;
        }
        // DCI changée : la confirmation de classe du titulaire (080) tombe, elle portait sur l'ancienne DCI
        await using var cmd = new NpgsqlCommand("""
            UPDATE produit SET nom_commercial = $2, dci_id = $3, dosage = $4, forme = $5, code_barre = $6, prix_public = $7,
                               tarif_reference = $8, remboursable = $9, taux_tva = $10, actif = $11,
                               classe_confirmee_par = CASE WHEN dci_id IS DISTINCT FROM $3 THEN NULL ELSE classe_confirmee_par END,
                               classe_confirmee_le = CASE WHEN dci_id IS DISTINCT FROM $3 THEN NULL ELSE classe_confirmee_le END
            WHERE id = $1
            """, cnx, tx);
        cmd.Parameters.AddWithValue(f.Id);
        cmd.Parameters.AddWithValue(f.NomCommercial);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)dciId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        foreach (var v in new[] { f.Dosage, f.Forme, f.CodeBarre })
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(f.PrixPublic);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.TarifReference ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric });
        cmd.Parameters.AddWithValue(f.Remboursable);
        cmd.Parameters.AddWithValue(f.TauxTva);
        cmd.Parameters.AddWithValue(f.Actif);
        try { await cmd.ExecuteNonQueryAsync(ct); }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new Pharma.Domain.Commun.RegleMetierException("Un autre produit porte déjà ce nom, ce dosage et cette forme (ou ce code-barres).");
        }
        // Classe imposée (produit modifié par un employé, DCI de substance surveillée ou n° classé par la nomenclature) : produit « à vérifier », invendable jusqu'à la
        // confirmation du titulaire ; avec du stock, solde d'ouverture au registre, comme un classement par le titulaire.
        // Seulement si la classe en base est encore moins stricte (hors registre, ou psychotrope monté en stupéfiant) et n'a pas été
        // confirmée par le titulaire entre-temps. Un psychotrope monté en stupéfiant était déjà sous registre : pas de solde d'ouverture.
        if (classeImposee)
        {
            await using var classe = new NpgsqlCommand("""
                UPDATE produit p SET classe = $2, liste_psychotrope = NULL, a_verifier = true
                FROM (SELECT classe FROM produit WHERE id = $1 FOR UPDATE) avant
                WHERE p.id = $1 AND p.classe_confirmee_par IS NULL
                  AND (p.classe NOT IN ('psychotrope', 'stupefiant') OR (p.classe = 'psychotrope' AND $2 = 'stupefiant'))
                RETURNING avant.classe
                """, cnx, tx);
            classe.Parameters.AddWithValue(f.Id);
            classe.Parameters.AddWithValue(f.Classe);
            if (await classe.ExecuteScalarAsync(ct) is string avant && avant is not ("psychotrope" or "stupefiant"))
                await Catalogue.NomenclatureSql.InscrireOuvertureAsync(cnx, tx, f.Id, utilisateurId, ct);
        }
        await tx.CommitAsync(ct);
    }
}
