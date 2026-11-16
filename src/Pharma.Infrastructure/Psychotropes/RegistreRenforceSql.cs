using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Catalogue;
using Pharma.Application.Commun;
using Pharma.Application.Psychotropes;
using Pharma.Infrastructure.Lecture;

namespace Pharma.Infrastructure.Psychotropes;

/// <summary>Tables et fonctions du script 117 (registre renforcé).</summary>
public sealed class RegistreRenforceSql(NpgsqlDataSource db) : IRegistreRenforceDepot, IZonePsychotropesDepot
{
    // ---- Wilayas et zone de l'officine (script 118) ----

    public async Task<IReadOnlyList<Wilaya>> WilayasAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT code, nom FROM wilaya ORDER BY code");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<Wilaya>();
        while (await r.ReadAsync(ct)) liste.Add(new Wilaya(r.GetString(0), r.GetString(1)));
        return liste;
    }

    public async Task<ZoneOfficine> ZoneAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT wilaya, officine FROM zone_psychotrope_wilaya ORDER BY wilaya");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        string? officine = null;
        var voisines = new List<string>();
        while (await r.ReadAsync(ct))
            if (r.GetBoolean(1)) officine = r.GetString(0); else voisines.Add(r.GetString(0));
        return new ZoneOfficine(officine, voisines);
    }

    public async Task EnregistrerZoneAsync(ZoneOfficine zone, long auteurId, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        await using (var del = new NpgsqlCommand("DELETE FROM zone_psychotrope_wilaya", cx, tx))
            await del.ExecuteNonQueryAsync(ct);
        var lignes = (zone.Officine is { } o ? new[] { (o, true) } : []).Concat(zone.Voisines.Select(v => (v, false)));
        foreach (var (code, officine) in lignes)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO zone_psychotrope_wilaya (wilaya, officine, modifie_par) VALUES ($1, $2, $3)", cx, tx);
            cmd.Parameters.AddWithValue(code);
            cmd.Parameters.AddWithValue(officine);
            cmd.Parameters.AddWithValue(auteurId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Lignes de sortie (délivrances) non annulées : base des contrôles.</summary>
    private const string Sorties = """
        FROM registre_psychotrope r
        LEFT JOIN ligne_vente lv ON lv.id = r.ligne_vente_id
        WHERE r.type_mouvement = 'sortie' AND NOT EXISTS (SELECT 1 FROM annulation_registre a WHERE a.registre_id = r.id)
        """;

    public async Task<IReadOnlyList<RegleAlertePsychotrope>> ReglesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT code, libelle, active, seuil, periode_jours, exiger_valideur, unites_par_jour FROM regle_alerte_psychotrope ORDER BY ordre, code");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<RegleAlertePsychotrope>();
        while (await r.ReadAsync(ct))
            liste.Add(new RegleAlertePsychotrope(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetInt32(3), r.GetInt32(4), r.GetBoolean(5),
                r.IsDBNull(6) ? null : r.GetInt32(6)));
        return liste;
    }

    public async Task EnregistrerReglesAsync(IReadOnlyList<RegleAlertePsychotrope> regles, long auteurId, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        foreach (var g in regles)
        {
            await using var cmd = new NpgsqlCommand("""
                UPDATE regle_alerte_psychotrope SET active = $2, seuil = $3, periode_jours = $4, exiger_valideur = $5, modifie_le = now(), modifie_par = $6,
                       unites_par_jour = $7
                WHERE code = $1
                """, cx, tx);
            cmd.Parameters.AddWithValue(g.Code);
            cmd.Parameters.AddWithValue(g.Active);
            cmd.Parameters.AddWithValue(g.Seuil);
            cmd.Parameters.AddWithValue(g.PeriodeJours);
            cmd.Parameters.AddWithValue(g.ExigerValideur);
            cmd.Parameters.AddWithValue(auteurId);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)g.UnitesParJour ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyDictionary<long, string>> ProduitsSousRegistreAsync(IReadOnlyCollection<long> produitIds, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT id, nom_commercial FROM produit WHERE id = ANY($1) AND classe IN ('psychotrope', 'stupefiant')");
        cmd.Parameters.AddWithValue(produitIds.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var d = new Dictionary<long, string>();
        while (await r.ReadAsync(ct)) d[r.GetInt64(0)] = r.GetString(1);
        return d;
    }

    public async Task<HistoriquePsychotrope> HistoriqueAsync(DelivrancePsychotrope d, IReadOnlyList<RegleAlertePsychotrope> regles, CancellationToken ct = default)
    {
        int Periode(string code) => regles.FirstOrDefault(g => g.Code == code)?.PeriodeJours ?? 30;
        // Une délivrance = une vente (plusieurs lignes du registre pour plusieurs lots), sinon la ligne elle-même
        await using var cmd = db.CreateCommand($"""
            SELECT
              (SELECT count(DISTINCT COALESCE(lv.vente_id, -r.id)) {Sorties}
                 AND $1::bigint IS NOT NULL AND r.patient_id = $1 AND r.cree_le >= now() - make_interval(days => $4))::int,
              (SELECT count(DISTINCT COALESCE(lv.vente_id, -r.id)) {Sorties}
                 AND $3::text IS NOT NULL AND r.medecin_id = $2
                 AND EXISTS (SELECT 1 FROM ordonnance o WHERE o.id = r.ordonnance_id AND lower(btrim(o.numero)) = lower($3))
                 AND r.cree_le >= now() - make_interval(days => $5))::int,
              (SELECT count(DISTINCT COALESCE(lv.vente_id, -r.id)) {Sorties}
                 AND $2::bigint IS NOT NULL AND r.medecin_id = $2 AND r.cree_le >= now() - make_interval(days => $6))::int,
              (SELECT m.wilaya FROM medecin m WHERE m.id = $2),
              ARRAY(SELECT z.wilaya FROM zone_psychotrope_wilaya z),
              ARRAY(SELECT p.id FROM produit p WHERE p.id = ANY($7) ORDER BY p.id),
              ARRAY(SELECT COALESCE(p.contenance, 0) FROM produit p WHERE p.id = ANY($7) ORDER BY p.id),
              ARRAY(SELECT COALESCE((SELECT string_agg(COALESCE(n.conditionnement, ''), E'\n') FROM nomenclature_produit n
                                     WHERE n.num_enregistrement = p.num_enregistrement
                                       AND n.version_id = (SELECT max(x.version_id) FROM nomenclature_produit x
                                                           WHERE x.num_enregistrement = p.num_enregistrement)), '')
                    FROM produit p WHERE p.id = ANY($7) ORDER BY p.id)
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)d.PatientId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)d.MedecinId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)d.NumeroOrdonnance ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(Periode(CodesAlertePsychotrope.PatientRepete));
        cmd.Parameters.AddWithValue(Periode(CodesAlertePsychotrope.OrdonnanceReutilisee));
        cmd.Parameters.AddWithValue(Periode(CodesAlertePsychotrope.PrescripteurInhabituel));
        cmd.Parameters.AddWithValue(d.Lignes.Select(l => l.ProduitId).Distinct().ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        var ids = r.GetFieldValue<long[]>(5);
        var saisies = r.GetFieldValue<int[]>(6);
        var nomenclature = r.GetFieldValue<string[]>(7);
        // Saisie du pharmacien d'abord ; sinon conditionnement de la nomenclature s'il est analysable (sinon : contenance inconnue)
        var contenances = new Dictionary<long, int>();
        for (var i = 0; i < ids.Length; i++)
        {
            var nom = nomenclature[i].Length == 0 ? null : ContenanceProduit.DepuisNomenclature(nomenclature[i].Split('\n'));
            if (ContenanceProduit.Retenue(saisies[i] > 0 ? saisies[i] : null, nom) is { } c) contenances[ids[i]] = c;
        }
        return new HistoriquePsychotrope(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3),
            r.GetFieldValue<string[]>(4).ToHashSet(StringComparer.Ordinal), contenances);
    }

    public async Task<long> EnregistrerForcageAsync(NouveauForcage f, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO forcage_alerte_psychotrope (utilisateur_id, valide_par, codes, messages, motif, patient_id, medecin_id, numero_ordonnance, produits, poste)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10) RETURNING id
            """);
        cmd.Parameters.AddWithValue(f.UtilisateurId);
        cmd.Parameters.Add(Long(f.ValidePar));
        cmd.Parameters.AddWithValue(f.Codes.ToArray());
        cmd.Parameters.AddWithValue(f.Messages);
        cmd.Parameters.AddWithValue(f.Motif);
        cmd.Parameters.Add(Long(f.PatientId));
        cmd.Parameters.Add(Long(f.MedecinId));
        cmd.Parameters.Add(Texte(f.NumeroOrdonnance));
        cmd.Parameters.AddWithValue(f.Produits.ToArray());
        cmd.Parameters.Add(Texte(f.Poste));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlySet<string>> CodesForcesAsync(long utilisateurId, DelivrancePsychotrope d, DateTime depuisUtc, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT DISTINCT unnest(codes) FROM forcage_alerte_psychotrope
            WHERE utilisateur_id = $1 AND cree_le >= $2 AND patient_id IS NOT DISTINCT FROM $3 AND medecin_id IS NOT DISTINCT FROM $4
              AND lower(numero_ordonnance) IS NOT DISTINCT FROM lower($5) AND produits @> $6
            """);
        cmd.Parameters.AddWithValue(utilisateurId);
        cmd.Parameters.Add(new NpgsqlParameter { Value = DateTime.SpecifyKind(depuisUtc, DateTimeKind.Utc), NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(Long(d.PatientId));
        cmd.Parameters.Add(Long(d.MedecinId));
        cmd.Parameters.Add(Texte(d.NumeroOrdonnance));
        cmd.Parameters.AddWithValue(d.Lignes.Select(l => l.ProduitId).ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var codes = new HashSet<string>();
        while (await r.ReadAsync(ct)) codes.Add(r.GetString(0));
        return codes;
    }

    public async Task<Page<ForcageAlerte>> ForcagesAsync(DateOnly du, DateOnly au, string? code, DemandePage page, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT f.id, f.cree_le, u.nom_complet, v.nom_complet, f.codes, f.messages, f.motif,
                   NULLIF(btrim(concat_ws(' ', pa.nom, pa.prenom)), ''), m.nom, f.numero_ordonnance,
                   (SELECT string_agg(p.nom_commercial, ', ' ORDER BY p.nom_commercial) FROM produit p WHERE p.id = ANY (f.produits)),
                   {PaginationSql.Total}
            FROM forcage_alerte_psychotrope f
            JOIN utilisateur u ON u.id = f.utilisateur_id
            LEFT JOIN utilisateur v ON v.id = f.valide_par
            LEFT JOIN patient pa ON pa.id = f.patient_id
            LEFT JOIN medecin m ON m.id = f.medecin_id
            WHERE f.cree_le >= $1 AND f.cree_le < $2 AND ($3::text IS NULL OR $3 = ANY (f.codes))
            ORDER BY f.cree_le DESC, f.id DESC {PaginationSql.Limite(4)}
            """);
        cmd.Parameters.AddWithValue(du.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime());
        cmd.Parameters.AddWithValue(au.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime());
        cmd.Parameters.Add(Texte(code));
        PaginationSql.Parametres(cmd, page);
        return await PaginationSql.LireAsync(cmd, page, 11, r => new ForcageAlerte(r.GetInt64(0), DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc),
            r.GetString(2), S(r, 3), r.GetFieldValue<string[]>(4), r.GetString(5), r.GetString(6), S(r, 7), S(r, 8), S(r, 9), S(r, 10) ?? ""), ct);
    }

    public async Task<LigneRegistreAnnulable?> LigneAsync(long numero, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT r.id, r.numero_ordre, p.nom_commercial, r.type_mouvement, r.cree_le,
                   EXISTS (SELECT 1 FROM annulation_registre a WHERE a.registre_id = r.id)
            FROM registre_psychotrope r JOIN lot l ON l.id = r.lot_id JOIN produit p ON p.id = l.produit_id
            WHERE r.numero_ordre = $1
            """);
        cmd.Parameters.AddWithValue(numero);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new LigneRegistreAnnulable(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), DateTime.SpecifyKind(r.GetDateTime(4), DateTimeKind.Utc), r.GetBoolean(5))
            : null;
    }

    public async Task<long> AnnulerAsync(long registreId, string motif, long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("INSERT INTO annulation_registre (registre_id, motif, utilisateur_id) VALUES ($1, $2, $3) RETURNING id");
        cmd.Parameters.AddWithValue(registreId);
        cmd.Parameters.AddWithValue(motif);
        cmd.Parameters.AddWithValue(utilisateurId);
        try
        {
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new Pharma.Domain.Commun.RegleMetierException("Cette ligne du registre est déjà annulée.");
        }
    }

    public async Task<IReadOnlyDictionary<long, AnnulationEcriture>> AnnulationsAsync(IReadOnlyCollection<long> numeros, CancellationToken ct = default)
    {
        var d = new Dictionary<long, AnnulationEcriture>();
        if (numeros.Count == 0) return d;
        await using var cmd = db.CreateCommand("""
            SELECT r.numero_ordre, a.cree_le, a.motif, u.nom_complet
            FROM annulation_registre a JOIN registre_psychotrope r ON r.id = a.registre_id JOIN utilisateur u ON u.id = a.utilisateur_id
            WHERE r.numero_ordre = ANY ($1)
            """);
        cmd.Parameters.AddWithValue(numeros.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            d[r.GetInt64(0)] = new AnnulationEcriture(r.GetInt64(0), DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc), r.GetString(2), r.GetString(3));
        return d;
    }

    public async Task<ResultatConcordance> ConcordanceAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT lignes, motif, registre_id, attendu, trouve FROM registre_concordance()");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new ResultatConcordance(r.GetInt64(0), S(r, 1), r.IsDBNull(2) ? null : r.GetInt64(2), S(r, 3), S(r, 4));
    }

    public async Task<IReadOnlyList<ClasseSuivie>> ClassesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT c.code, c.libelle, c.active, (SELECT count(*) FROM classe_suivie_dci d WHERE d.classe_code = c.code)::int
            FROM classe_suivie c ORDER BY c.libelle
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<ClasseSuivie>();
        while (await r.ReadAsync(ct)) liste.Add(new ClasseSuivie(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetInt32(3)));
        return liste;
    }

    public async Task<IReadOnlyList<DciClasse>> DciClasseAsync(string classe, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT d.id, d.nom FROM classe_suivie_dci c JOIN dci d ON d.id = c.dci_id WHERE c.classe_code = $1 ORDER BY d.nom
            """);
        cmd.Parameters.AddWithValue(classe);
        return await DciAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<DciClasse>> ChercherDciAsync(string texte, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT id, nom FROM dci WHERE nom ILIKE '%' || $1 || '%' ORDER BY nom LIMIT 30");
        cmd.Parameters.AddWithValue(texte.Replace("%", "").Replace("_", ""));
        return await DciAsync(cmd, ct);
    }

    public async Task AjouterDciAsync(string classe, long dciId, long auteurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("INSERT INTO classe_suivie_dci (classe_code, dci_id, ajoute_par) VALUES ($1, $2, $3) ON CONFLICT DO NOTHING");
        cmd.Parameters.AddWithValue(classe);
        cmd.Parameters.AddWithValue(dciId);
        cmd.Parameters.AddWithValue(auteurId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RetirerDciAsync(string classe, long dciId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("DELETE FROM classe_suivie_dci WHERE classe_code = $1 AND dci_id = $2");
        cmd.Parameters.AddWithValue(classe);
        cmd.Parameters.AddWithValue(dciId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<LigneSuiviClasse>> SuiviClasseAsync(string classe, DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        // Ventes validées (les ventes annulées sont exclues) ; boîtes vendues, sans déduire les retours [À VÉRIFIER]
        await using var cmd = db.CreateCommand("""
            SELECT d.nom, extract(year FROM v.date_vente AT TIME ZONE $4)::int, extract(month FROM v.date_vente AT TIME ZONE $4)::int,
                   sum(lv.quantite)::int, count(DISTINCT v.id)::int
            FROM ligne_vente lv
            JOIN vente v ON v.id = lv.vente_id
            JOIN produit p ON p.id = lv.produit_id
            JOIN classe_suivie_dci c ON c.dci_id = p.dci_id AND c.classe_code = $1
            JOIN dci d ON d.id = p.dci_id
            WHERE v.statut = 'validee' AND v.date_vente >= $2 AND v.date_vente < $3
            GROUP BY 1, 2, 3 ORDER BY 2, 3, 1
            """);
        cmd.Parameters.AddWithValue(classe);
        cmd.Parameters.AddWithValue(du.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime());
        cmd.Parameters.AddWithValue(au.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime());
        cmd.Parameters.AddWithValue(FuseauLocal());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<LigneSuiviClasse>();
        while (await r.ReadAsync(ct)) liste.Add(new LigneSuiviClasse(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4)));
        return liste;
    }

    private static async Task<IReadOnlyList<DciClasse>> DciAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<DciClasse>();
        while (await r.ReadAsync(ct)) liste.Add(new DciClasse(r.GetInt64(0), r.GetString(1)));
        return liste;
    }

    /// <summary>Fuseau du serveur au format IANA (mois en heure locale), comme RegistreSql.</summary>
    private static string FuseauLocal() =>
        TimeZoneInfo.Local.HasIanaId ? TimeZoneInfo.Local.Id
        : TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : "UTC";

    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static NpgsqlParameter Long(long? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint };
    private static NpgsqlParameter Texte(string? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };
}

/// <summary>
/// Adaptateur FACTICE du répertoire national des ordonnances (loi 23-05) : l'API n'est pas publiée [À VÉRIFIER]. Ne contacte aucun
/// service et ne transmet rien ; répond « indisponible ». À remplacer par l'adaptateur réel quand le répertoire existera.
/// </summary>
public sealed class RepertoireOrdonnancesFactice : IRepertoireOrdonnancesConnecteur
{
    public string Nom => "factice";

    public Task<ReponseRepertoire> SignalerDelivranceAsync(DelivranceRepertoire d, CancellationToken ct = default) =>
        Task.FromResult(new ReponseRepertoire("indisponible",
            "Répertoire national des ordonnances : non relié (connecteur factice, en attente de l’API officielle)."));
}
