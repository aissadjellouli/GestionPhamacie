using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Synchronisation;

namespace Pharma.Infrastructure.Synchronisation;

/// <summary>Réglages d'envoi, lots non accusés et accusés (scripts 126 à 128).</summary>
public sealed class EnvoiSyncSql(NpgsqlDataSource db) : IEnvoiSyncDepot
{
    public async Task<ParametresEnvoiSync> ParametresAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT p.active, p.officine_id, p.epoque, p.version_contrat, p.dernier_lot, p.dernier_accuse, p.adresse_serveur, p.inscrite_le,
                   p.dernier_envoi_le, p.dernier_accuse_le, p.derniere_erreur, p.derniere_erreur_le, p.textes_libres, p.envoyer_medecins,
                   (SELECT count(*) FROM sync_sortie s WHERE s.lot_numero IS NULL AND s.epoque = p.epoque),
                   (SELECT count(*) FROM sync_lot l WHERE l.accuse_le IS NULL AND l.epoque = p.epoque),
                   pg_is_in_recovery(),
                   (SELECT count(*) FROM sync_lot l WHERE l.accuse_le IS NULL AND l.epoque < p.epoque),
                   CASE WHEN rc.cle IS NULL THEN NULL ELSE sync_familles_effectives() END, rc.recu_le, p.descendant_version, coalesce(rc.version, 0),
                   sync_colonnes_interdites(), p.retour_arriere_le, p.retour_arriere_central, p.retour_arriere_sequences_le,
                   p.controle_demarrage_delai_s
            FROM sync_parametre p LEFT JOIN reglage_central rc ON rc.cle = 'familles_envoyees'
            WHERE p.id = 1
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Réglage de synchronisation absent (script 126).");
        return new ParametresEnvoiSync(r.GetBoolean(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5),
            r.IsDBNull(6) ? null : r.GetString(6), Date(r, 7), Date(r, 8), Date(r, 9), r.IsDBNull(10) ? null : r.GetString(10), Date(r, 11),
            r.GetString(12), r.GetBoolean(13), r.GetInt64(14), r.GetInt64(15), r.GetBoolean(16), r.GetInt64(17),
            r.IsDBNull(18) ? null : r.GetFieldValue<string[]>(18), Date(r, 19), r.GetInt64(20), r.GetInt64(21), r.GetFieldValue<string[]>(22),
            Date(r, 23), r.IsDBNull(24) ? null : r.GetInt64(24), Date(r, 25), r.GetInt32(26));
    }

    // ---- Script 152 : encaissement suspendu au démarrage jusqu'au contrôle du serveur central ----
    public async Task<bool> NoterDemarrageAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_noter_demarrage()");
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    public Task NoterControleDemarrageAsync(CancellationToken ct = default) => ExecuterAsync("SELECT sync_noter_controle_demarrage()", ct);

    public async Task<int> AttenteEncaissementAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT encaissement_attente_secondes()");
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public Task ReglerControleDemarrageAsync(int secondes, CancellationToken ct = default) =>
        ExecuterAsync("SELECT sync_regler_controle_demarrage($1)", ct, secondes);

    public async Task<IReadOnlyList<LotEnregistre>> LotsNonAccusesAsync(int maximum, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT l.epoque, l.numero, l.version_contrat, l.empreinte, {Colonnes} FROM sync_lot l JOIN sync_parametre p ON p.id = 1 AND p.epoque = l.epoque
            WHERE l.accuse_le IS NULL ORDER BY l.numero LIMIT $1
            """);
        cmd.Parameters.AddWithValue(maximum);
        return await LotsAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<LotEnregistre>> LotsEntreAsync(int epoque, long du, long au, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT l.epoque, l.numero, l.version_contrat, l.empreinte, {Colonnes} FROM sync_lot l JOIN sync_parametre p ON p.id = 1
            WHERE l.epoque = $1 AND l.numero BETWEEN $2 AND $3 ORDER BY l.numero
            """);
        cmd.Parameters.AddWithValue(epoque);
        cmd.Parameters.AddWithValue(du);
        cmd.Parameters.AddWithValue(au);
        return await LotsAsync(cmd, ct);
    }

    /// <summary>Colonnes retirées figées (script 136) ou, pour un lot antérieur, reconstituées depuis ses familles et le catalogue actuel.</summary>
    private const string Colonnes = "coalesce(l.colonnes_retirees, CASE WHEN l.familles IS NULL THEN '{}'::text[] ELSE sync_colonnes_hors_familles(l.familles) END), "
        + "l.colonnes_retirees IS NOT NULL, l.accuse_le IS NOT NULL, l.reforme_le, coalesce(p.purge_epoque = l.epoque AND l.numero <= p.purge_dernier_lot, false)";

    private static async Task<IReadOnlyList<LotEnregistre>> LotsAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var res = new List<LotEnregistre>();
        while (await r.ReadAsync(ct))
            res.Add(new LotEnregistre(r.GetInt32(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetFieldValue<string[]>(4), r.GetBoolean(5),
                r.GetBoolean(6), r.IsDBNull(7) ? null : DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc), r.GetBoolean(8)));
        return res;
    }

    public async Task<IReadOnlyList<LigneSortieSync>> LignesDuLotAsync(int epoque, long numero, IReadOnlyList<string>? colonnesRetirees, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT s.id, s.nom_table, s.id_local, s.operation, s.survenu_le, sync_retirer_colonnes(s.nom_table, s.donnees, $3)::text, s.ordre
            FROM sync_sortie s WHERE s.epoque = $1 AND s.lot_numero = $2 ORDER BY coalesce(s.ordre, s.id), s.id
            """);
        cmd.Parameters.AddWithValue(epoque);
        cmd.Parameters.AddWithValue(numero);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)colonnesRetirees?.ToArray() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<LigneSortieSync>();
        while (await r.ReadAsync(ct))
            liste.Add(new LigneSortieSync(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                DateTime.SpecifyKind(r.GetDateTime(4), DateTimeKind.Utc), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetInt64(6)));
        return liste;
    }

    public Task EnregistrerAccuseAsync(int epoque, long numero, CancellationToken ct = default) =>
        ExecuterAsync("SELECT sync_enregistrer_accuse($1, $2)", ct, epoque, numero);

    public async Task NoterEnvoiAsync(string? erreur, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_noter_envoi($1)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)erreur ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task NoterPassageAsync(string? natureErreur, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_noter_passage($1)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)natureErreur ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Éléments descendants reçus dans une transaction (sync_recevoir_descendant, script 136) : un statut par élément.</summary>
    public async Task<IReadOnlyList<string>> AppliquerDescendantsAsync(IReadOnlyList<ChangementDescendantSync> changements, long versionMax, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        var statuts = new string[changements.Count];
        foreach (var (c, i) in changements.Select((c, i) => (c, i)).OrderBy(x => x.c.Version))
        {
            await using var cmd = new NpgsqlCommand("SELECT sync_recevoir_descendant($1, $2, $3, $4::jsonb, $5)", cnx, tx);
            cmd.Parameters.AddWithValue(c.Table);
            cmd.Parameters.AddWithValue(c.Cle);
            cmd.Parameters.AddWithValue(c.Version);
            cmd.Parameters.AddWithValue(c.DonneesJson);
            cmd.Parameters.AddWithValue(versionMax);
            statuts[i] = (string)(await cmd.ExecuteScalarAsync(ct))!;
        }
        await tx.CommitAsync(ct);
        return statuts;
    }

    public async Task NoterVersionDescendanteAsync(long version, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_noter_version_descendante($1)");
        cmd.Parameters.AddWithValue(version);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ReformerLotAsync(int epoque, long numero, string empreinte, int octets, IReadOnlyList<string> colonnesRetirees, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_reformer_lot_colonnes($1, $2, $3, $4, $5)");
        cmd.Parameters.AddWithValue(epoque);
        cmd.Parameters.AddWithValue(numero);
        cmd.Parameters.AddWithValue(empreinte);
        cmd.Parameters.AddWithValue(octets);
        cmd.Parameters.Add(new NpgsqlParameter { Value = colonnesRetirees.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task EnregistrerInscriptionAsync(Guid officine, CancellationToken ct = default) =>
        ExecuterAsync("SELECT sync_enregistrer_inscription($1)", ct, officine);

    /// <summary>Adresse, options et activation (pose ou retrait des déclencheurs) dans une seule transaction.</summary>
    public async Task ReglerAsync(string? adresse, string textesLibres, bool envoyerMedecins, bool active, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await using (var c = new NpgsqlCommand("SELECT sync_regler_adresse($1)", cnx, tx))
        {
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)adresse ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            await c.ExecuteNonQueryAsync(ct);
        }
        await using (var c = new NpgsqlCommand("SELECT sync_definir_options($1, $2)", cnx, tx))
        {
            c.Parameters.AddWithValue(textesLibres);
            c.Parameters.AddWithValue(envoyerMedecins);
            await c.ExecuteNonQueryAsync(ct);
        }
        await using (var c = new NpgsqlCommand("SELECT sync_definir_active($1)", cnx, tx))
        {
            c.Parameters.AddWithValue(active);
            await c.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    // ---- Lot S6.2 : base revenue en arrière, nouvelle époque (script 149) ----
    public async Task<bool> NoterRetourArriereAsync(long dernierLotCentral, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_noter_retour_arriere($1)");
        cmd.Parameters.AddWithValue(dernierLotCentral);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    public async Task<bool> NoterRetourArriereRefusAsync(long dernierLotCentral, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_noter_retour_arriere_refus($1)");
        cmd.Parameters.AddWithValue(dernierLotCentral);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>Une ligne par table retenue ; une table en échec (verrou, séquence trop petite…) porte son erreur, les autres sont avancées.</summary>
    public async Task<IReadOnlyList<SequenceAvancee>> AvancerSequencesAsync(IReadOnlyDictionary<string, long> maximums, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT nom_table, avant, apres, erreur FROM sync_avancer_sequences($1)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = System.Text.Json.JsonSerializer.Serialize(maximums), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<SequenceAvancee>();
        while (await r.ReadAsync(ct))
            liste.Add(new SequenceAvancee(r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2),
                r.IsDBNull(3) ? null : r.GetString(3)));
        return liste;
    }

    /// <summary>Lot S6.5.4 (script 151) : déclaration pas encore envoyée, la plus récente (une époque dépassée est notée par l'appelant).</summary>
    public async Task<DeclarationEpoqueSync?> DeclarationAEnvoyerAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT ancienne_epoque, nouvelle_epoque, dernier_lot_local, dernier_lot_central, detecte_le, decide_le, titulaire_id, fins_chaine::text
            FROM sync_declaration_epoque WHERE envoyee_le IS NULL ORDER BY nouvelle_epoque DESC LIMIT 1
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var fins = new List<FinChaineSync>();
        using (var doc = System.Text.Json.JsonDocument.Parse(r.GetString(7)))
            foreach (var f in doc.RootElement.EnumerateArray())
                fins.Add(new FinChaineSync(f.GetProperty("journal").GetString() ?? "", f.GetProperty("cle_chaine").GetString() ?? "",
                    f.GetProperty("rang").GetInt64(),
                    f.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String ? id.GetString() : null,
                    f.TryGetProperty("hash", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.String ? h.GetString() : null));
        return new DeclarationEpoqueSync(r.GetInt32(0), r.GetInt32(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3),
            r.IsDBNull(4) ? null : r.GetFieldValue<DateTime>(4), r.GetFieldValue<DateTime>(5), r.IsDBNull(6) ? null : r.GetInt64(6), fins);
    }

    public async Task NoterDeclarationAsync(int nouvelleEpoque, bool? avantPremierLot, string resultat, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_noter_declaration($1, $2, $3)");
        cmd.Parameters.AddWithValue(nouvelleEpoque);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)avantPremierLot ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Boolean });
        cmd.Parameters.AddWithValue(resultat);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Lot S6.5.4 (S6.5-D6 a) : compteur des tickets avancé (jamais en arrière), plage notée ; nombre de n° sautés.</summary>
    public async Task<long> AvancerCompteurTicketAsync(long dernierTicketCentral, int epoque, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_avancer_compteur_ticket($1, $2)");
        cmd.Parameters.AddWithValue(dernierTicketCentral);
        cmd.Parameters.AddWithValue(epoque);
        return await cmd.ExecuteScalarAsync(ct) is long n ? n : 0;
    }

    /// <summary>Prise d'acte (script 151) et entrée du journal des actions dans UNE transaction ; une seule par époque.</summary>
    public async Task PrendreActeRestaurationAsync(int nouvelleEpoque, string cause, long titulaireId, IReadOnlyDictionary<string, object?> journal,
        CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        long id;
        try
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO integrite_prise_acte (nouvelle_epoque, cause, utilisateur_id) VALUES ($1, $2, $3) RETURNING id", cnx, tx);
            cmd.Parameters.AddWithValue(nouvelleEpoque);
            cmd.Parameters.AddWithValue(cause);
            cmd.Parameters.AddWithValue(titulaireId);
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new Pharma.Domain.Commun.RegleMetierException($"La prise d’acte de la restauration de l’époque {nouvelleEpoque} est déjà enregistrée.");
        }
        await Securite.JournalSql.EcrireAsync(cnx, tx, titulaireId, "integrite_prise_acte", "integrite_prise_acte", id, journal, null, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Nouvelle époque et entrée du journal des actions dans UNE transaction (rien n'est noté si l'une échoue).</summary>
    public async Task<NouvelleEpoqueSync> NouvelleEpoqueAsync(long titulaireId, int? epoqueCentral, int epoqueAttendue,
        Func<NouvelleEpoqueSync, IReadOnlyDictionary<string, object?>> journal, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        NouvelleEpoqueSync res;
        await using (var cmd = new NpgsqlCommand("SELECT epoque, ancienne_epoque, chargement FROM sync_nouvelle_epoque($1, $2, $3)", cnx, tx))
        {
            cmd.Parameters.AddWithValue(titulaireId);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)epoqueCentral ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
            cmd.Parameters.AddWithValue(epoqueAttendue);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Nouvelle époque : aucune réponse de sync_nouvelle_epoque.");
            res = new NouvelleEpoqueSync(r.GetInt32(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetInt32(2));
        }
        await Securite.JournalSql.EcrireAsync(cnx, tx, titulaireId, "synchronisation_nouvelle_epoque", "sync_parametre", 1, journal(res), null, ct);
        await tx.CommitAsync(ct);
        return res;
    }

    private async Task ExecuterAsync(string sql, CancellationToken ct, params object[] valeurs)
    {
        await using var cmd = db.CreateCommand(sql);
        foreach (var v in valeurs) cmd.Parameters.AddWithValue(v);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static DateTime? Date(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc);
}
