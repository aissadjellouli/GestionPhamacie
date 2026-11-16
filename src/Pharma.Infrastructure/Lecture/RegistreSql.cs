using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Stock;

namespace Pharma.Infrastructure.Lecture;

/// <summary>Registre des psychotropes (vue v_registre_psychotrope, 024_registre_psychotropes.sql).</summary>
public sealed class RegistreSql(NpgsqlDataSource db) : IRegistrePsychotropes
{
    private const string Colonnes = """
        numero, cree_le, produit_id, produit, numero_lot, type_mouvement, entree, sortie, solde_produit, solde_lot,
        NULLIF(patient, ''), medecin, ordonnance, fournisseur, reference,
        porteur_nom, porteur_adresse, porteur_piece, employe, patient_id,
        (SELECT rp.duree_traitement_jours FROM registre_psychotrope rp WHERE rp.numero_ordre = v_registre_psychotrope.numero)
        """;

    /// <summary>Filtre commun : $1 et $2 bornes de la période (instants), $3 produit (ou null).</summary>
    private const string Periode = "cree_le >= $1 AND cree_le < $2 AND ($3::bigint IS NULL OR produit_id = $3)";

    /// <summary>Jours du poste (heure locale) convertis en instants : début du premier jour, début du lendemain du dernier.</summary>
    private static void ParametresPeriode(NpgsqlCommand cmd, long? produitId, DateOnly du, DateOnly au)
    {
        cmd.Parameters.AddWithValue(du.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime());
        cmd.Parameters.AddWithValue(au.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime());
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)produitId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
    }

    private static LigneRegistre Lire(NpgsqlDataReader r)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        int? N(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
        return new LigneRegistre(r.GetInt64(0), r.GetDateTime(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5),
            N(6), N(7), N(8), r.GetInt32(9), S(10), S(11), S(12), S(13), S(14), S(15), S(16), S(17), r.GetString(18), r.IsDBNull(19) ? null : r.GetInt64(19), N(20));
    }

    /// <summary>
    /// Fuseau du serveur, au format de la base (IANA, ex. « Africa/Algiers ») : les mois de la balance sont ceux de l'heure locale,
    /// comme les jours de la période. Sans équivalent IANA connu, UTC (signalé par le nom du fuseau dans les journaux de la base).
    /// </summary>
    private static string FuseauLocal() =>
        TimeZoneInfo.Local.HasIanaId ? TimeZoneInfo.Local.Id
        : TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : "UTC";

    public async Task<IReadOnlyList<LigneRegistre>> LireAsync(long? produitId, DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Colonnes} FROM v_registre_psychotrope WHERE {Periode} ORDER BY numero");
        ParametresPeriode(cmd, produitId, du, au);
        var liste = new List<LigneRegistre>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(Lire(r));
        return liste;
    }

    public async Task<Pharma.Application.Commun.Page<LigneRegistre>> LirePageAsync(long? produitId, DateOnly du, DateOnly au, string? type,
        Pharma.Application.Commun.DemandePage page, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT {Colonnes}, {PaginationSql.Total}
            FROM v_registre_psychotrope
            WHERE {Periode} AND ($4::text IS NULL OR type_mouvement = $4)
            ORDER BY numero {PaginationSql.Limite(5)}
            """);
        ParametresPeriode(cmd, produitId, du, au);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)type ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        PaginationSql.Parametres(cmd, page);
        return await PaginationSql.LireAsync(cmd, page, 21, Lire, ct);
    }

    public async Task<IReadOnlyList<string>> TypesAsync(long? produitId, DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT DISTINCT type_mouvement FROM v_registre_psychotrope WHERE {Periode}");
        ParametresPeriode(cmd, produitId, du, au);
        var liste = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(r.GetString(0));
        return liste;
    }

    public async Task<(int Inscriptions, IReadOnlyList<BalanceMensuelle> Balance)> SyntheseAsync(long? produitId, DateOnly du, DateOnly au,
        CancellationToken ct = default)
    {
        // Balance mensuelle (arrêté du 23 août 1999, art. 4) : solde reporté (solde d'ouverture du registre, pas une entrée),
        // entrées, sorties et solde du produit à la dernière ligne du mois, mois en heure locale ($4 : fuseau). Les lignes
        // antérieures à l'ouverture (solde du produit non calculé par la vue) sont exclues de la balance, mais comptées dans le
        // nombre d'inscriptions de la période (toutes les lignes de chaque mois)
        await using var cmd = db.CreateCommand($"""
            SELECT produit_id, produit, extract(year FROM jour)::int, extract(month FROM jour)::int,
                   COALESCE(sum(entree) FILTER (WHERE solde_produit IS NOT NULL AND type_mouvement <> 'ouverture'), 0)::int,
                   COALESCE(sum(sortie) FILTER (WHERE solde_produit IS NOT NULL), 0)::int,
                   (array_agg(solde_produit ORDER BY numero DESC) FILTER (WHERE solde_produit IS NOT NULL))[1],
                   count(*)::int,
                   COALESCE(sum(entree) FILTER (WHERE solde_produit IS NOT NULL AND type_mouvement = 'ouverture'), 0)::int,
                   count(*) FILTER (WHERE solde_produit IS NOT NULL)::int
            FROM (SELECT produit_id, produit, numero, type_mouvement, entree, sortie, solde_produit, cree_le AT TIME ZONE $4 AS jour
                  FROM v_registre_psychotrope WHERE {Periode}) l
            GROUP BY produit_id, produit, extract(year FROM jour), extract(month FROM jour)
            ORDER BY produit, produit_id, 3, 4
            """);
        ParametresPeriode(cmd, produitId, du, au);
        cmd.Parameters.AddWithValue(FuseauLocal());
        var balance = new List<BalanceMensuelle>();
        var nombre = 0;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            nombre += r.GetInt32(7);
            if (r.GetInt32(9) == 0) continue;   // mois sans ligne postérieure à l'ouverture : hors balance
            balance.Add(new BalanceMensuelle(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5),
                r.IsDBNull(6) ? null : r.GetInt32(6), r.GetInt32(8)));
        }
        return (nombre, balance);
    }

    public async Task<IReadOnlyList<ProduitRegistre>> ProduitsAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT p.id, p.nom_commercial FROM produit p
            WHERE p.classe IN ('psychotrope', 'stupefiant')
               OR EXISTS (SELECT 1 FROM registre_psychotrope r JOIN lot l ON l.id = r.lot_id WHERE l.produit_id = p.id)
            ORDER BY p.nom_commercial
            """);
        var liste = new List<ProduitRegistre>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(new ProduitRegistre(r.GetInt64(0), r.GetString(1)));
        return liste;
    }
}
