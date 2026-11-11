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

    public async Task<(long Id, string Nom, string NumAssure)?> PatientAsync(string numeroAssure, string suffixe, CancellationToken ct = default)
    {
        // N° d'assuré comparé chiffres seuls : « 690755003842/80 », « 690755003842 80 » ou « 69075500384280 », sinon le n° sans suffixe
        await using var cmd = db.CreateCommand("""
            SELECT id, trim(nom || ' ' || COALESCE(prenom, '')), num_assure FROM patient
            WHERE regexp_replace(COALESCE(num_assure, ''), '\D', '', 'g') IN ($1 || $2, $1)
            ORDER BY regexp_replace(num_assure, '\D', '', 'g') = $1 || $2 DESC, id DESC
            LIMIT 1
            """);
        cmd.Parameters.AddWithValue(numeroAssure);
        cmd.Parameters.AddWithValue(suffixe);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetInt64(0), r.GetString(1), r.GetString(2)) : null;
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
}
