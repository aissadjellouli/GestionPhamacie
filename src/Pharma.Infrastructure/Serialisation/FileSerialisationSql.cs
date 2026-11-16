using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Serialisation;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Serialisation;

/// <summary>
/// File des opérations de sérialisation (script 120) : remplie par le déclencheur trg_boite_serialisation (boîte Data Matrix reçue,
/// vendue, remise en stock), vidée par TraiterFileSerialisation. Aucune ligne n'est supprimée : la file est aussi la trace des réponses.
/// </summary>
public sealed class FileSerialisationSql(NpgsqlDataSource db) : IFileSerialisation
{
    private const string Colonnes = """
        v.id, v.boite_id, v.operation, b.gtin, b.numero_serie, l.numero_lot, l.date_peremption, p.nom_commercial, v.essais
        """;
    private const string Jointures = """
        FROM verification_serialisation v
        JOIN boite b ON b.id = v.boite_id
        JOIN lot l ON l.id = b.lot_id
        JOIN produit p ON p.id = l.produit_id
        """;

    public async Task<IReadOnlyList<OperationEnFile>> AFaireAsync(int maximum, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT {Colonnes} {Jointures}
            WHERE v.statut = 'en_attente' AND v.prochain_essai_le <= now()
            ORDER BY v.id LIMIT $1
            """);
        cmd.Parameters.AddWithValue(maximum);
        var liste = new List<OperationEnFile>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new OperationEnFile(r.GetInt64(0), r.GetInt64(1), LibellesSerialisation.OperationDepuisCode(r.GetString(2)),
                new BoiteSerialisee(r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetFieldValue<DateOnly>(6)),
                r.GetString(7), r.GetInt32(8)));
        return liste;
    }

    public async Task TerminerAsync(long id, bool acceptee, StatutSerialisation resultat, string? message, bool alerteTitulaire, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE verification_serialisation
            SET statut = $2, resultat = $3, message = $4, alerte_titulaire = $5,
                essais = essais + 1, dernier_essai_le = now(), traite_le = now()
            WHERE id = $1 AND statut = 'en_attente'
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(acceptee ? "reussie" : "refusee");
        cmd.Parameters.AddWithValue(LibellesSerialisation.Code(resultat));
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)message ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(alerteTitulaire);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ReporterAsync(long id, string message, CancellationToken ct = default)
    {
        // Délai croissant : 1, 2, 4… minutes, au plus 30 minutes
        await using var cmd = db.CreateCommand("""
            UPDATE verification_serialisation
            SET essais = essais + 1, dernier_essai_le = now(), message = left($2, 500),
                prochain_essai_le = now() + make_interval(mins => least(30, power(2, least(essais, 5))::int))
            WHERE id = $1 AND statut = 'en_attente'
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(message);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<EtatFileSerialisation> EtatAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT count(*) FILTER (WHERE statut = 'en_attente')::int,
                   count(*) FILTER (WHERE statut = 'reussie')::int,
                   count(*) FILTER (WHERE statut = 'refusee')::int,
                   max(dernier_essai_le),
                   (SELECT message FROM verification_serialisation
                    WHERE statut = 'en_attente' AND essais > 0 ORDER BY dernier_essai_le DESC LIMIT 1)
            FROM verification_serialisation
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new EtatFileSerialisation(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2),
            r.IsDBNull(3) ? null : r.GetDateTime(3), r.IsDBNull(4) ? null : r.GetString(4));
    }

    public async Task<(IReadOnlyList<LigneFileSerialisation> Lignes, int Total)> ListerAsync(string? statut, int page, int taille, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT v.id, v.boite_id, p.nom_commercial, b.gtin, b.numero_serie, l.numero_lot, v.operation, v.statut, v.resultat,
                   v.message, v.essais, v.cree_le, v.dernier_essai_le, v.traite_le, count(*) OVER ()::int
            {Jointures}
            WHERE $1::text IS NULL OR v.statut = $1
            ORDER BY v.id DESC
            LIMIT $2 OFFSET $3
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)statut ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(taille);
        cmd.Parameters.AddWithValue((page - 1) * taille);
        var liste = new List<LigneFileSerialisation>();
        var total = 0;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            liste.Add(new LigneFileSerialisation(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), LibellesSerialisation.OperationDepuisCode(r.GetString(6)), r.GetString(7),
                LibellesSerialisation.DepuisCode(r.IsDBNull(8) ? null : r.GetString(8)), r.IsDBNull(9) ? null : r.GetString(9),
                r.GetInt32(10), r.GetDateTime(11), r.IsDBNull(12) ? null : r.GetDateTime(12), r.IsDBNull(13) ? null : r.GetDateTime(13)));
            total = r.GetInt32(14);
        }
        return (liste, total);
    }

    public async Task<int> RelancerAsync(long? id, long utilisateurId, CancellationToken ct = default)
    {
        await using var connexion = await db.OpenConnectionAsync(ct);
        await using var tx = await connexion.BeginTransactionAsync(ct);
        int n;
        await using (var cmd = new NpgsqlCommand("""
            UPDATE verification_serialisation SET statut = 'en_attente', prochain_essai_le = now()
            WHERE ($1::bigint IS NULL AND statut = 'en_attente') OR (id = $1 AND statut IN ('en_attente', 'refusee'))
            """, connexion, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)id ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            n = await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO journal_audit (utilisateur_id, action, table_cible, details)
            VALUES ($1, 'serialisation_relance', 'verification_serialisation', jsonb_build_object('id', $2, 'relancees', $3))
            """, connexion, tx))
        {
            cmd.Parameters.AddWithValue(utilisateurId);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)id ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            cmd.Parameters.AddWithValue(n);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return n;
    }

    public async Task<ParametresSerialisation> ParametresAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT actif, connecteur, factice_hors_ligne FROM parametre_serialisation WHERE id = 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new ParametresSerialisation(r.GetBoolean(0), r.GetString(1), r.GetBoolean(2))
            : new ParametresSerialisation(true, "factice", false);
    }

    public async Task EnregistrerParametresAsync(bool actif, bool facticeHorsLigne, long utilisateurId, CancellationToken ct = default)
    {
        await using var connexion = await db.OpenConnectionAsync(ct);
        await using var tx = await connexion.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            "UPDATE parametre_serialisation SET actif = $1, factice_hors_ligne = $2, modifie_le = now() WHERE id = 1", connexion, tx))
        {
            cmd.Parameters.AddWithValue(actif);
            cmd.Parameters.AddWithValue(facticeHorsLigne);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO journal_audit (utilisateur_id, action, table_cible, details)
            VALUES ($1, 'serialisation_parametres', 'parametre_serialisation', jsonb_build_object('actif', $2, 'factice_hors_ligne', $3))
            """, connexion, tx))
        {
            cmd.Parameters.AddWithValue(utilisateurId);
            cmd.Parameters.AddWithValue(actif);
            cmd.Parameters.AddWithValue(facticeHorsLigne);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<Gs1Identifiant>> IdentifiantsGs1Async(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT code, champ, longueur_fixe, longueur_max FROM parametre_gs1_ai WHERE actif ORDER BY code");
        var liste = new List<Gs1Identifiant>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            ChampGs1? champ = r.GetString(1) switch
            {
                "gtin" => ChampGs1.Gtin, "peremption" => ChampGs1.Peremption, "lot" => ChampGs1.Lot, "serie" => ChampGs1.Serie, _ => null
            };
            if (champ is { } c)
                liste.Add(new Gs1Identifiant(r.GetString(0), c, r.IsDBNull(2) ? null : r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt32(3)));
        }
        return liste;
    }
}
