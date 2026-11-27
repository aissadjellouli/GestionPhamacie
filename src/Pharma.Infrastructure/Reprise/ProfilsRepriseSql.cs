using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Commun;
using Pharma.Application.Reprise;
using Pharma.Domain.Commun;
using Pharma.Domain.Reprise;

namespace Pharma.Infrastructure.Reprise;

/// <summary>Profils de reprise (import.profil_reprise, database/migration/114_profils_reprise.sql).</summary>
public sealed class ProfilsRepriseSql(NpgsqlDataSource db) : IProfilsReprise
{
    private const string Select = """
        SELECT id, logiciel, version, statut, remarque, correspondances::text, separateur_decimal, format_date, codes_classe::text, modifie_le, modifie_par
        FROM import.profil_reprise
        """;

    private const string Filtre = """
        WHERE ($1::text IS NULL OR logiciel ILIKE '%' || $1 || '%' OR version ILIKE '%' || $1 || '%')
          AND ($2::text IS NULL OR statut = $2)
        """;

    /// <summary>Correspondance stockée en JSON : cible (code de l'écran), feuille ou table, colonnes.</summary>
    [System.Reflection.Obfuscation(Exclude = true, ApplyToMembers = true)]
    private sealed record CorrespondanceJson(string Cible, string Table, Dictionary<string, string> Colonnes);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Page<ProfilReprise>> ListerAsync(FiltreProfilsReprise filtre, DemandePage page, CancellationToken ct = default)
    {
        int total;
        await using (var cmd = db.CreateCommand($"SELECT count(*)::int FROM import.profil_reprise {Filtre}"))
        {
            Parametres(cmd, filtre);
            total = (int)(await cmd.ExecuteScalarAsync(ct))!;
        }
        var lignes = new List<ProfilReprise>();
        await using (var cmd = db.CreateCommand($"{Select} {Filtre} ORDER BY lower(logiciel), lower(version), id LIMIT $3 OFFSET $4"))
        {
            Parametres(cmd, filtre);
            cmd.Parameters.AddWithValue(page.Taille);
            cmd.Parameters.AddWithValue(page.Decalage);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) lignes.Add(Lire(r));
        }
        return new Page<ProfilReprise>(lignes, total, page.Page, page.Taille);
    }

    public async Task<ProfilReprise?> LireAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"{Select} WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Lire(r) : null;
    }

    public async Task<long> EnregistrerAsync(ProfilReprise p, string auteur, CancellationToken ct = default)
    {
        var sql = p.Id == 0
            ? """
              INSERT INTO import.profil_reprise (logiciel, version, statut, remarque, correspondances, separateur_decimal, format_date, codes_classe, modifie_par)
              VALUES ($1, $2, $3, $4, $5::jsonb, $6, $7, $8::jsonb, $9) RETURNING id
              """
            : """
              UPDATE import.profil_reprise SET logiciel = $1, version = $2, statut = $3, remarque = $4, correspondances = $5::jsonb,
                     separateur_decimal = $6, format_date = $7, codes_classe = $8::jsonb, modifie_par = $9, modifie_le = now()
              WHERE id = $10 RETURNING id
              """;
        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(p.Logiciel);
        cmd.Parameters.AddWithValue(p.Version);
        cmd.Parameters.AddWithValue(p.Statut);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)p.Remarque ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(p.Correspondances.Select(c => new CorrespondanceJson(CodeCible(c.Cible), c.Table, c.Colonnes.ToDictionary())), Json));
        cmd.Parameters.AddWithValue(ProfilsReprise.CodeSeparateur(p.Transformations.Separateur));
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)p.Transformations.FormatDate ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(p.Transformations.CodesClasse, Json));
        cmd.Parameters.AddWithValue(auteur);
        if (p.Id != 0) cmd.Parameters.AddWithValue(p.Id);
        try
        {
            return (long)(await cmd.ExecuteScalarAsync(ct) ?? throw new RegleMetierException("Profil de reprise introuvable."));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new RegleMetierException($"Un profil existe déjà pour {p.Logiciel} {p.Version} : modifiez-le ou changez la version.", "version");
        }
    }

    private static void Parametres(NpgsqlCommand cmd, FiltreProfilsReprise f)
    {
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.Recherche ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.Statut ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
    }

    private static ProfilReprise Lire(NpgsqlDataReader r)
    {
        var correspondances = (JsonSerializer.Deserialize<List<CorrespondanceJson>>(r.GetString(5), Json) ?? [])
            .Where(c => CibleDe(c.Cible) is not null)
            .Select(c => new CorrespondanceProfil(CibleDe(c.Cible)!.Value, c.Table, c.Colonnes ?? []))
            .ToList();
        var codes = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(8), Json) ?? [];
        return new ProfilReprise(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
            correspondances, new TransformationsReprise(ProfilsReprise.LireSeparateur(r.GetString(6)), r.IsDBNull(7) ? null : r.GetString(7), codes),
            r.GetDateTime(9), r.IsDBNull(10) ? null : r.GetString(10));
    }

    /// <summary>Codes des cibles, identiques à ceux de l'écran et de l'API de reprise.</summary>
    public static string CodeCible(CibleReprise c) => c switch
    {
        CibleReprise.Produits => "produits", CibleReprise.Stock => "stock", CibleReprise.Fournisseurs => "fournisseurs",
        CibleReprise.HistoriqueChifa => "historique_chifa", CibleReprise.HistoriqueMdn => "historique_mdn", _ => "clients"
    };

    public static CibleReprise? CibleDe(string? code) => code switch
    {
        "produits" => CibleReprise.Produits, "stock" => CibleReprise.Stock, "fournisseurs" => CibleReprise.Fournisseurs,
        "clients" => CibleReprise.Clients, "historique_chifa" => CibleReprise.HistoriqueChifa,
        "historique_mdn" => CibleReprise.HistoriqueMdn, _ => null
    };
}

/// <summary>Historique des organismes repris : Chifa et MDN (historique_chifa, lecture seule, scripts 114 et 121).</summary>
public sealed class HistoriqueChifaSql(NpgsqlDataSource db) : IHistoriqueChifa
{
    private const string Filtre = """
        WHERE ($1::text IS NULL OR h.num_facture ILIKE '%' || $1 || '%' OR COALESCE(h.num_assure, '') ILIKE '%' || $1 || '%'
               OR COALESCE(h.nom_assure, '') ILIKE '%' || $1 || '%')
          AND ($2::date IS NULL OR h.date_facture >= $2)
          AND ($3::date IS NULL OR h.date_facture <= $3)
          AND ($4::text IS NULL OR h.organisme = $4 OR ($4 = 'CHIFA' AND h.organisme IN ('CNAS', 'CASNOS')))
        """;

    public async Task<Page<LigneHistoriqueChifa>> ListerAsync(FiltreHistoriqueChifa filtre, DemandePage page, CancellationToken ct = default)
    {
        int total;
        await using (var cmd = db.CreateCommand($"SELECT count(*)::int FROM historique_chifa h {Filtre}"))
        {
            Parametres(cmd, filtre);
            total = (int)(await cmd.ExecuteScalarAsync(ct))!;
        }
        var lignes = new List<LigneHistoriqueChifa>();
        await using (var cmd = db.CreateCommand($"""
            SELECT h.id, h.session_id, h.logiciel_source, h.num_facture, h.date_facture, h.num_assure, h.nom_assure, h.montant_total, h.part_chifa, h.part_assure,
                   h.organisme
            FROM historique_chifa h {Filtre}
            ORDER BY h.date_facture DESC, h.num_facture, h.id LIMIT $5 OFFSET $6
            """))
        {
            Parametres(cmd, filtre);
            cmd.Parameters.AddWithValue(page.Taille);
            cmd.Parameters.AddWithValue(page.Decalage);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                lignes.Add(new LigneHistoriqueChifa(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateOnly>(4),
                    r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                    r.IsDBNull(7) ? null : r.GetDecimal(7), r.IsDBNull(8) ? null : r.GetDecimal(8), r.IsDBNull(9) ? null : r.GetDecimal(9), r.GetString(10)));
        }
        return new Page<LigneHistoriqueChifa>(lignes, total, page.Page, page.Taille);
    }

    private static void Parametres(NpgsqlCommand cmd, FiltreHistoriqueChifa f)
    {
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.Recherche ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = f.Du is { } du ? du : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
        cmd.Parameters.Add(new NpgsqlParameter { Value = f.Au is { } au ? au : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.Organisme ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
    }
}
