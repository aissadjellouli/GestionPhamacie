using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Reprise;
using Pharma.Domain.Reprise;

namespace Pharma.Infrastructure.Reprise;

/// <summary>Tables de transit et fonctions du schéma import (database/008_reprise_donnees.sql).</summary>
public sealed class RepriseSql(NpgsqlDataSource db) : IRepriseImport
{
    private const int Paquet = 500;

    /// <summary>Table de transit, colonnes autorisées (codes des champs de DetectionReprise) et colonnes obligatoires.</summary>
    private static readonly IReadOnlyDictionary<CibleReprise, (string Table, string[] Colonnes, string[] Requises)> Transit =
        new Dictionary<CibleReprise, (string, string[], string[])>
        {
            [CibleReprise.Produits] = ("import.produit_src", ["ref_source", "code_barre", "num_enregistrement", "nom", "dosage", "forme", "prix_vente", "prix_achat"], ["ref_source", "nom"]),
            [CibleReprise.Stock] = ("import.stock_src", ["ref_produit", "numero_lot", "date_peremption", "quantite", "fournisseur_ref"], ["ref_produit"]),
            [CibleReprise.Fournisseurs] = ("import.fournisseur_src", ["ref_source", "raison_sociale", "telephone", "solde"], ["ref_source", "raison_sociale"]),
            [CibleReprise.Clients] = ("import.client_src", ["ref_source", "nom", "prenom", "telephone", "num_assure", "solde_credit"], ["ref_source", "nom"])
        };

    public async Task<long> CreerSessionAsync(string logiciel, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("INSERT INTO import.session (logiciel_source) VALUES ($1) RETURNING id");
        cmd.Parameters.AddWithValue(logiciel);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<(int Inserees, int Doublons)> InsererAsync(long session, CibleReprise cible, IReadOnlyList<string> champs,
        IAsyncEnumerable<string?[]> lignes, CancellationToken ct = default)
    {
        var (table, autorisees, requises) = Transit[cible];
        if (champs.Any(c => !autorisees.Contains(c))) throw new ArgumentException("Champ de reprise inconnu.", nameof(champs));
        var idxRequis = requises.Select(r => champs.ToList().IndexOf(r)).Where(i => i >= 0).ToArray();

        // INSERT … SELECT unnest(tableaux) : un aller-retour par paquet de lignes
        var colonnes = string.Join(", ", champs);
        var tableaux = string.Join(", ", champs.Select((_, i) => $"${i + 2}::text[]"));
        var conflit = cible == CibleReprise.Stock ? "" : "ON CONFLICT DO NOTHING";
        var sql = $"INSERT INTO {table} (session_id, {colonnes}) SELECT $1, * FROM unnest({tableaux}) {conflit}";

        int inserees = 0, lues = 0;
        var paquet = new List<string?[]>(Paquet);
        async Task Envoyer()
        {
            if (paquet.Count == 0) return;
            await using var cmd = db.CreateCommand(sql);
            cmd.Parameters.AddWithValue(session);
            for (var i = 0; i < champs.Count; i++)
                cmd.Parameters.Add(new NpgsqlParameter { Value = paquet.Select(l => Nettoyer(l[i])).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
            inserees += await cmd.ExecuteNonQueryAsync(ct);
            paquet.Clear();
        }

        await foreach (var l in lignes.WithCancellation(ct))
        {
            if (idxRequis.Any(i => string.IsNullOrWhiteSpace(l[i]))) continue;   // ligne sans identifiant ou sans nom : ignorée
            lues++;
            paquet.Add(l);
            if (paquet.Count == Paquet) await Envoyer();
        }
        await Envoyer();
        return (inserees, lues - inserees);
    }

    private static string? Nettoyer(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    public async Task<int> RapprocherEtControlerAsync(long session, CancellationToken ct = default)
    {
        await using (var cmd = db.CreateCommand("SELECT import.rapprocher_produits($1)"))
        {
            cmd.Parameters.AddWithValue(session);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return await ControlerAsync(session, ct);
    }

    public async Task<int> ControlerAsync(long session, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT import.controler($1)");
        cmd.Parameters.AddWithValue(session);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<SessionReprise>> SessionsAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT id, logiciel_source, debut, statut FROM import.session ORDER BY id DESC LIMIT 50");
        var liste = new List<SessionReprise>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(new SessionReprise(r.GetInt64(0), r.GetString(1), r.GetDateTime(2), r.GetString(3)));
        return liste;
    }

    public async Task<EtatReprise?> EtatAsync(long session, CancellationToken ct = default)
    {
        SessionReprise? s;
        await using (var cmd = db.CreateCommand("SELECT id, logiciel_source, debut, statut FROM import.session WHERE id = $1"))
        {
            cmd.Parameters.AddWithValue(session);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            s = new SessionReprise(r.GetInt64(0), r.GetString(1), r.GetDateTime(2), r.GetString(3));
        }

        var lignes = await Comptes("""
            SELECT 'produits', count(*)::int FROM import.produit_src WHERE session_id = $1
            UNION ALL SELECT 'stock', count(*)::int FROM import.stock_src WHERE session_id = $1
            UNION ALL SELECT 'grossistes', count(*)::int FROM import.fournisseur_src WHERE session_id = $1
            UNION ALL SELECT 'clients', count(*)::int FROM import.client_src WHERE session_id = $1
            """, session, ct);
        var rapprochement = await Comptes("""
            SELECT CASE WHEN c.ref_source IS NULL THEN 'aucune'
                        WHEN c.methode = 'nom_approche' AND NOT c.valide THEN 'nom_a_confirmer'
                        ELSE c.methode END, count(*)::int
            FROM import.produit_src p
            LEFT JOIN import.correspondance_produit c ON c.session_id = p.session_id AND c.ref_source = p.ref_source
            WHERE p.session_id = $1 GROUP BY 1
            """, session, ct);

        var aConfirmer = new List<ProduitAConfirmer>();
        await using (var cmd = db.CreateCommand("""
            SELECT p.ref_source, p.nom, p.dosage, p.code_barre, c.produit_id, pr.nom_commercial, c.score
            FROM import.produit_src p
            LEFT JOIN import.correspondance_produit c ON c.session_id = p.session_id AND c.ref_source = p.ref_source
            LEFT JOIN produit pr ON pr.id = c.produit_id
            WHERE p.session_id = $1 AND (c.ref_source IS NULL OR NOT c.valide)
            ORDER BY c.score DESC NULLS LAST, p.nom LIMIT 300
            """))
        {
            cmd.Parameters.AddWithValue(session);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                aConfirmer.Add(new ProduitAConfirmer(r.GetString(0), r.GetString(1), T(r, 2), T(r, 3),
                    r.IsDBNull(4) ? null : r.GetInt64(4), T(r, 5), r.IsDBNull(6) ? null : r.GetDecimal(6)));
        }

        var anomalies = new List<AnomalieReprise>();
        await using (var cmd = db.CreateCommand("""
            SELECT entite, ref_source, gravite, message FROM import.anomalie WHERE session_id = $1
            ORDER BY gravite = 'bloquant' DESC, entite, id LIMIT 500
            """))
        {
            cmd.Parameters.AddWithValue(session);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) anomalies.Add(new AnomalieReprise(r.GetString(0), T(r, 1), r.GetString(2), r.GetString(3)));
        }

        var verification = new List<LigneVerification>();
        if (s.Statut == "charge")
        {
            await using var cmd = db.CreateCommand("SELECT indicateur, ancien, nouveau, ecart FROM import.controle_apres_chargement($1)");
            cmd.Parameters.AddWithValue(session);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) verification.Add(new LigneVerification(r.GetString(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3)));
        }
        return new EtatReprise(s, lignes, rapprochement, aConfirmer, anomalies, verification);
    }

    public async Task DeciderAsync(long session, string refSource, DecisionProduit decision, long? produitId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(decision == DecisionProduit.Confirmer
            ? "UPDATE import.correspondance_produit SET valide = true WHERE session_id = $1 AND ref_source = $2 AND produit_id IS NOT NULL"
            : """
              INSERT INTO import.correspondance_produit (session_id, ref_source, produit_id, methode, score, valide)
              VALUES ($1, $2, $3, $4, NULL, true)
              ON CONFLICT (session_id, ref_source) DO UPDATE SET produit_id = EXCLUDED.produit_id, methode = EXCLUDED.methode, score = NULL, valide = true
              """);
        cmd.Parameters.AddWithValue(session);
        cmd.Parameters.AddWithValue(refSource);
        if (decision != DecisionProduit.Confirmer)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = decision == DecisionProduit.Autre ? produitId! : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            cmd.Parameters.AddWithValue(decision == DecisionProduit.Autre ? "manuel" : "nouveau");
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ChargerAsync(long session, long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT import.charger($1, $2)");
        cmd.CommandTimeout = 600;
        cmd.Parameters.AddWithValue(session);
        cmd.Parameters.AddWithValue(utilisateurId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AnnulerAsync(long session, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE import.session SET statut = 'annule' WHERE id = $1 AND statut = 'en_cours'");
        cmd.Parameters.AddWithValue(session);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<IReadOnlyDictionary<string, int>> Comptes(string sql, long session, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(session);
        var d = new Dictionary<string, int>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) d[r.GetString(0)] = r.GetInt32(1);
        return d;
    }

    private static string? T(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
