using Npgsql;
using Pharma.Application.Caisse;

namespace Pharma.Infrastructure.Caisse;

/// <summary>Volets Chifa repris à la caisse : codes produits associés, patient de l'assuré, volet déjà repris (database/migration/060_volet_chifa.sql).</summary>
public sealed class VoletsChifaSql(NpgsqlDataSource db) : IVoletsChifa
{
    public async Task<string?> VenteDuVoletAsync(string numeroFacture, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT numero FROM vente WHERE chifa_facture = $1 AND statut <> 'annulee' LIMIT 1");
        cmd.Parameters.AddWithValue(numeroFacture);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<IReadOnlyDictionary<string, ProduitChifa>> ProduitsAsync(IEnumerable<string> codes, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT c.code, p.id, p.nom_commercial, p.prix_public
            FROM chifa_code_produit c JOIN produit p ON p.id = c.produit_id
            WHERE c.code = ANY($1) AND p.actif
            """);
        cmd.Parameters.AddWithValue(codes.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var produits = new Dictionary<string, ProduitChifa>();
        while (await r.ReadAsync(ct)) produits[r.GetString(0)] = new ProduitChifa(r.GetInt64(1), r.GetString(2), r.GetDecimal(3));
        return produits;
    }

    public async Task<(long Id, string Nom, string NumAssure, string? Organisme)?> PatientAsync(string numeroAssure, string suffixe, CancellationToken ct = default)
    {
        // Recherche STRICTE n° + suffixe, chiffres seuls (« 690755003842/80 » = « 690755003842 80 ») : plus de repli sur le n° seul (2026-11-15)
        await using var cnx = await db.OpenConnectionAsync(ct);
        var p = PatientScanVolet.Patient(await CandidatsAsync(cnx, null, numeroAssure, suffixe, ct), numeroAssure, suffixe);
        return p is null ? null : (p.Id, p.Nom, p.NumAssure, p.Organisme);
    }

    public async Task<(long Id, string Nom)?> AssurePrincipalProposeAsync(string numeroAssure, string suffixe, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        var p = PatientScanVolet.AssurePrincipalPropose(await CandidatsAsync(cnx, null, numeroAssure, suffixe, ct), numeroAssure, suffixe);
        return p is null ? null : (p.Id, p.Nom);
    }

    /// <summary>Fiches dont le n° d'assuré (chiffres seuls) commence par le n° de base de l'assuré du volet (index ix_patient_num_assure_base, script 090).</summary>
    private static async Task<List<CandidatVolet>> CandidatsAsync(NpgsqlConnection cnx, NpgsqlTransaction? tx, string numeroAssure, string suffixe,
        CancellationToken ct)
    {
        var prefixe = PatientScanVolet.Prefixe(numeroAssure, suffixe);
        var liste = new List<CandidatVolet>();
        if (prefixe.Length == 0) return liste;
        await using var cmd = new NpgsqlCommand("""
            SELECT p.id, trim(p.nom || ' ' || COALESCE(p.prenom, '')), p.num_assure, o.code, p.organisme_id, p.lien, p.assure_principal_id
            FROM patient p LEFT JOIN organisme o ON o.id = p.organisme_id
            WHERE p.num_assure IS NOT NULL AND left(regexp_replace(p.num_assure, '\D', '', 'g'), 12) = left($1, 12)
              AND regexp_replace(p.num_assure, '\D', '', 'g') LIKE $1 || '%'
            ORDER BY p.id
            LIMIT 200
            """, cnx, tx);
        cmd.Parameters.AddWithValue(prefixe);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new CandidatVolet(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetInt64(6)));
        return liste;
    }

    public async Task<string?> CodeOrganismeAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT code FROM organisme WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<long?> OrganismeAsync(string code, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT id FROM organisme WHERE upper(code) = upper($1) AND actif LIMIT 1");
        cmd.Parameters.AddWithValue(code);
        return await cmd.ExecuteScalarAsync(ct) as long?;
    }

    public async Task<(string Produit, string? Ancien)?> AssocierAsync(string code, long produitId, long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            WITH ancien AS (SELECT p.nom_commercial FROM chifa_code_produit c JOIN produit p ON p.id = c.produit_id WHERE c.code = $1),
            nouveau AS (SELECT id, nom_commercial FROM produit WHERE id = $2 AND actif),
            maj AS (
                INSERT INTO chifa_code_produit (code, produit_id, associe_par)
                SELECT $1, id, $3 FROM nouveau
                ON CONFLICT (code) DO UPDATE SET produit_id = EXCLUDED.produit_id, associe_par = EXCLUDED.associe_par, associe_le = now()
                RETURNING code)
            SELECT n.nom_commercial, (SELECT nom_commercial FROM ancien) FROM nouveau n JOIN maj ON true
            """);
        cmd.Parameters.AddWithValue(code);
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.AddWithValue(utilisateurId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)) : null;
    }

    public async Task<IReadOnlyList<MajCatalogueChifa>> MettreAJourCatalogueAsync(IReadOnlyDictionary<long, decimal> tarifs, CancellationToken ct = default)
    {
        // Une transaction : lignes verrouillées, valeurs avant relues, puis mise à jour des seuls produits à changer (idempotent).
        // Prix public inchangé.
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        var avant = new Dictionary<long, (string Nom, bool Remboursable, decimal? Tarif)>();
        await using (var lire = new NpgsqlCommand(
            "SELECT id, nom_commercial, remboursable, tarif_reference FROM produit WHERE id = ANY($1) ORDER BY id FOR UPDATE", cnx, tx))
        {
            lire.Parameters.AddWithValue(tarifs.Keys.ToArray());
            await using var r = await lire.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) avant[r.GetInt64(0)] = (r.GetString(1), r.GetBoolean(2), r.IsDBNull(3) ? null : r.GetDecimal(3));
        }
        var majs = new List<MajCatalogueChifa>();
        foreach (var (id, (nom, remboursable, tarif)) in avant)
        {
            var nouveau = tarifs[id];
            if (remboursable && tarif == nouveau) continue;
            await using var maj = new NpgsqlCommand("UPDATE produit SET remboursable = true, tarif_reference = $2 WHERE id = $1", cnx, tx);
            maj.Parameters.AddWithValue(id);
            maj.Parameters.AddWithValue(nouveau);
            await maj.ExecuteNonQueryAsync(ct);
            majs.Add(new MajCatalogueChifa(id, nom, remboursable, tarif, nouveau));
        }
        await tx.CommitAsync(ct);
        return majs;
    }

    public async Task<(long Id, string Nom, string NumAssure, long? OrganismeId, bool Cree)> PatientOuCreerAsync(string numeroAssure, string suffixe,
        string nom, long organismeId, long creePar, string? lien, long? assurePrincipalId, CancellationToken ct = default)
    {
        // Index unique du n° d'assuré absent tant que des doublons repris existent (script 089) : verrou transactionnel propre au
        // n° d'assuré, puis recherche stricte (n° + suffixe) avant création, dans la même transaction
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await using (var verrou = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('patient_assure:' || $1))", cnx, tx))
        {
            verrou.Parameters.AddWithValue(numeroAssure);
            await verrou.ExecuteNonQueryAsync(ct);
        }
        if (PatientScanVolet.Patient(await CandidatsAsync(cnx, tx, numeroAssure, suffixe, ct), numeroAssure, suffixe) is { } e)
        {
            await tx.CommitAsync(ct);
            return (e.Id, e.Nom, e.NumAssure, e.OrganismeId, false);
        }

        var numAssure = $"{numeroAssure}/{suffixe}";
        await using var cree = new NpgsqlCommand("""
            INSERT INTO patient (nom, num_assure, organisme_id, plafond_credit, cree_par, lien, assure_principal_id)
            VALUES ($1, $2, $3, 0, $4, $5, $6) RETURNING id
            """, cnx, tx);
        cree.Parameters.AddWithValue(nom);
        cree.Parameters.AddWithValue(numAssure);
        cree.Parameters.AddWithValue(organismeId);
        cree.Parameters.AddWithValue(creePar);
        cree.Parameters.Add(new NpgsqlParameter { Value = (object?)lien ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cree.Parameters.Add(new NpgsqlParameter { Value = (object?)assurePrincipalId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
        var id = (long)(await cree.ExecuteScalarAsync(ct))!;
        await tx.CommitAsync(ct);
        return (id, nom, numAssure, organismeId, true);
    }
}
