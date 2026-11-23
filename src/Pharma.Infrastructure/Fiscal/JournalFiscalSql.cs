using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Commun;
using Pharma.Application.Fiscal;

namespace Pharma.Infrastructure.Fiscal;

/// <summary>Journal fiscal des ventes (script 112) : lignes, récapitulatif TVA, continuité, clôtures scellées, paramètres.</summary>
public sealed class JournalFiscalSql(NpgsqlDataSource db) : IJournalFiscalDepot
{
    // Jour local de l'officine (parametre_fiscal.fuseau)
    private const string JourLocal = "(j.survenu_le AT TIME ZONE (SELECT fuseau FROM parametre_fiscal WHERE id = 1))::date";

    private const string Colonnes = """
        j.rang, j.id, j.evenement, j.numero_ticket, j.survenu_le, j.poste, u.nom_complet, j.montant_ttc, j.montant_ht, j.montant_tva,
        j.part_patient, j.part_organisme, j.paiements, j.montant_mouvement, j.motif, encode(j.hash, 'hex')
        """;

    public async Task<Page<LigneJournalFiscal>> RechercherAsync(DateOnly du, DateOnly au, string? ticket, DemandePage page, CancellationToken ct = default)
    {
        const string Filtre = $"WHERE {JourLocal} BETWEEN $1 AND $2 AND ($3::text IS NULL OR j.numero_ticket ILIKE '%' || $3 || '%')";
        int total;
        await using (var c = db.CreateCommand($"SELECT count(*)::int FROM journal_vente j {Filtre}"))
        {
            Parametres(c, du, au, ticket);
            total = (int)(await c.ExecuteScalarAsync(ct))!;
        }
        await using var cmd = db.CreateCommand($"""
            SELECT {Colonnes} FROM journal_vente j LEFT JOIN utilisateur u ON u.id = j.utilisateur_id
            {Filtre} ORDER BY j.rang DESC LIMIT $4 OFFSET $5
            """);
        Parametres(cmd, du, au, ticket);
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        return new Page<LigneJournalFiscal>(await LireLignesAsync(cmd, ct), total, page.Page, page.Taille);
    }

    public async Task<IReadOnlyList<LigneJournalFiscal>> LignesAsync(DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT {Colonnes} FROM journal_vente j LEFT JOIN utilisateur u ON u.id = j.utilisateur_id
            WHERE {JourLocal} BETWEEN $1 AND $2 ORDER BY j.rang
            """);
        cmd.Parameters.AddWithValue(du);
        cmd.Parameters.AddWithValue(au);
        return await LireLignesAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<LigneRecapTva>> RecapTvaAsync(DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT taux, montant_ttc, montant_ht, montant_tva, evenements FROM journal_vente_recap_tva($1, $2)");
        cmd.Parameters.AddWithValue(du);
        cmd.Parameters.AddWithValue(au);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<LigneRecapTva>();
        while (await r.ReadAsync(ct)) liste.Add(new LigneRecapTva(r.GetDecimal(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3), r.GetInt64(4)));
        return liste;
    }

    public async Task<TotauxFiscaux> TotauxAsync(DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT count(*) FILTER (WHERE j.evenement NOT IN ('ouverture', 'saut_compteur')), COALESCE(sum(j.montant_ttc), 0), COALESCE(sum(j.montant_ht), 0),
                   COALESCE(sum(j.montant_tva), 0)
            FROM journal_vente j WHERE {JourLocal} BETWEEN $1 AND $2
            """);
        cmd.Parameters.AddWithValue(du);
        cmd.Parameters.AddWithValue(au);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new TotauxFiscaux(r.GetInt64(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3));
    }

    public async Task<IReadOnlyList<AnomalieNumerotation>> ContinuiteAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT anomalie, numero, vente_id FROM journal_vente_continuite()");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<AnomalieNumerotation>();
        while (await r.ReadAsync(ct))
            liste.Add(new AnomalieNumerotation(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2)));
        return liste;
    }

    public async Task<IReadOnlyList<TrouHistorique>> TrousHistoriqueAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT du, au, manquants FROM journal_vente_trous_historique()");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<TrouHistorique>();
        while (await r.ReadAsync(ct)) liste.Add(new TrouHistorique(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)));
        return liste;
    }

    private const string SelectCloture = """
        SELECT c.id, c.type, c.session_caisse_id, c.periode_du, c.periode_au, c.sceau_id, s.scelle_le, s.dernier_rang, s.algorithme,
               s.empreinte_cle, s.sceau, c.evenements, c.montant_ttc, c.montant_ht, c.montant_tva, c.fichier_archive
        FROM cloture_fiscale c JOIN sceau_integrite s ON s.id = c.sceau_id
        """;

    public async Task<IReadOnlyList<ClotureFiscale>> CloturesAsync(DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"{SelectCloture} WHERE c.periode_au >= $1 AND c.periode_du <= $2 ORDER BY s.scelle_le");
        cmd.Parameters.AddWithValue(du);
        cmd.Parameters.AddWithValue(au);
        return await LireCloturesAsync(cmd, ct);
    }

    public async Task<ClotureFiscale?> ClotureSessionAsync(long sessionCaisseId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"{SelectCloture} WHERE c.type = 'journaliere' AND c.session_caisse_id = $1");
        cmd.Parameters.AddWithValue(sessionCaisseId);
        return (await LireCloturesAsync(cmd, ct)).FirstOrDefault();
    }

    public async Task<bool> MoisClotureAsync(DateOnly premierJour, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT EXISTS (SELECT 1 FROM cloture_fiscale WHERE type = 'mensuelle' AND periode_du = $1)");
        cmd.Parameters.AddWithValue(premierJour);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<long>> SessionsNonScelleesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT s.id FROM session_caisse s, compteur_ticket c
            WHERE c.id = 1 AND s.fermee_le IS NOT NULL AND s.fermee_le >= c.mis_en_service_le
              AND NOT EXISTS (SELECT 1 FROM cloture_fiscale f WHERE f.type = 'journaliere' AND f.session_caisse_id = s.id)
            ORDER BY s.fermee_le
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<long>();
        while (await r.ReadAsync(ct)) liste.Add(r.GetInt64(0));
        return liste;
    }

    public async Task<long> EnregistrerClotureAsync(NouvelleClotureFiscale c, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO cloture_fiscale (type, session_caisse_id, periode_du, periode_au, sceau_id, evenements, montant_ttc, montant_ht, montant_tva,
                                         fichier_archive, empreinte_archive)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) RETURNING id
            """);
        cmd.Parameters.AddWithValue(c.Type);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)c.SessionCaisseId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.AddWithValue(c.Du);
        cmd.Parameters.AddWithValue(c.Au);
        cmd.Parameters.AddWithValue(c.SceauId);
        cmd.Parameters.AddWithValue(c.Totaux.Evenements);
        cmd.Parameters.AddWithValue(c.Totaux.Ttc);
        cmd.Parameters.AddWithValue(c.Totaux.Ht);
        cmd.Parameters.AddWithValue(c.Totaux.Tva);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)c.FichierArchive ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)c.EmpreinteArchive ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bytea });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<ParametresFiscaux> ParametresAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT conservation_annees, format_export_dgi, separateur_csv, fuseau FROM parametre_fiscal WHERE id = 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new ParametresFiscaux(r.GetInt16(0), r.GetString(1), r.GetString(2), r.GetString(3))
            : new ParametresFiscaux(6, "csv-v1", ";", "Africa/Algiers");
    }

    public async Task<CompteurTickets> CompteurAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT prefixe, dernier_numero, premier_numero_continu, mis_en_service_le FROM compteur_ticket WHERE id = 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Compteur des tickets absent (script 112).");
        return new CompteurTickets(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetDateTime(3));
    }

    private static void Parametres(NpgsqlCommand c, DateOnly du, DateOnly au, string? ticket)
    {
        c.Parameters.AddWithValue(du);
        c.Parameters.AddWithValue(au);
        c.Parameters.Add(new NpgsqlParameter { Value = (object?)ticket ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
    }

    private static async Task<IReadOnlyList<LigneJournalFiscal>> LireLignesAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<LigneJournalFiscal>();
        while (await r.ReadAsync(ct))
            liste.Add(new LigneJournalFiscal(r.GetInt64(0), r.GetInt64(1), r.GetString(2), Texte(r, 3), r.GetDateTime(4), Texte(r, 5), Texte(r, 6),
                r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), r.GetDecimal(10), r.GetDecimal(11), r.GetString(12), r.GetDecimal(13), Texte(r, 14),
                r.GetString(15)));
        return liste;
    }

    private static async Task<IReadOnlyList<ClotureFiscale>> LireCloturesAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<ClotureFiscale>();
        while (await r.ReadAsync(ct))
            liste.Add(new ClotureFiscale(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.GetFieldValue<DateOnly>(3),
                r.GetFieldValue<DateOnly>(4), r.GetInt64(5), DateTime.SpecifyKind(r.GetDateTime(6), DateTimeKind.Utc), r.GetInt64(7), r.GetString(8),
                r.GetString(9), r.GetFieldValue<byte[]>(10), r.GetInt64(11), r.GetDecimal(12), r.GetDecimal(13), r.GetDecimal(14), Texte(r, 15)));
        return liste;
    }

    private static string? Texte(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}

/// <summary>
/// Archivage local des rapports scellés : dossier du PC serveur (par défaut C:\ProgramData\PharmaOfficine\archives-fiscales),
/// rangé par année et mois ; un fichier existant n'est jamais écrasé (suffixe ajouté). À inclure dans la sauvegarde du PC serveur.
/// </summary>
public sealed class ArchivesFiscalesLocales(string racine) : IArchivesFiscales
{
    public async Task<string> RangerAsync(string sousDossier, string nomFichier, byte[] contenu, CancellationToken ct = default)
    {
        if (nomFichier.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || sousDossier.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Nom de fichier d'archive invalide.", nameof(nomFichier));
        var dossier = Path.Combine(racine, sousDossier.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dossier);
        var nom = nomFichier;
        for (var i = 2; File.Exists(Path.Combine(dossier, nom)); i++)
            nom = $"{Path.GetFileNameWithoutExtension(nomFichier)}_{i}{Path.GetExtension(nomFichier)}";
        await using (var f = new FileStream(Path.Combine(dossier, nom), FileMode.CreateNew, FileAccess.Write))
            await f.WriteAsync(contenu, ct);
        return Path.Combine(sousDossier, nom).Replace('\\', '/');
    }
}
