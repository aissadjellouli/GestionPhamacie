using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Commun;
using Pharma.Application.Financements;
using Pharma.Domain.Commun;
using Pharma.Domain.Financements;
using Pharma.Domain.Tresorerie;
using Pharma.Infrastructure.Achats;
using Pharma.Infrastructure.Tresorerie;

namespace Pharma.Infrastructure.Financements;

/// <summary>
/// Prêts bancaires (banque de la trésorerie ou prêteur hors trésorerie), échéances, compte du titulaire et usage des sources (078, 079) ;
/// taux, durée et assurance des échéances (081 : vues v_pret_bancaire_complet et v_echeance_pret_complete).
/// </summary>
public sealed class FinancementsSql(NpgsqlDataSource db) : IFinancements
{
    // Script 170 : échéancier à remplacer et capital remboursé par anticipation lus à part (les vues gardent leurs colonnes)
    private const string ColonnesPret = """
        id, compte_id, banque, reference, objet, montant, date_pret, document, capital_rembourse, reste_du, utilise, echeances, echeances_payees,
        echues_non_payees, prochaine_echeance, prochain_montant, employe, taux, duree_mois, prochaine_assurance,
        (SELECT pb.echeancier_a_remplacer FROM pret_bancaire pb WHERE pb.id = v_pret_bancaire_complet.id),
        COALESCE((SELECT sum(ra.capital) FROM remboursement_anticipe_pret ra WHERE ra.pret_id = v_pret_bancaire_complet.id), 0)
        """;

    // Script 170 : déjà payé de chaque échéance (même en partie)
    private const string ColonnesEcheance = """
        id, pret_id, pret, banque, rang, date_echeance, capital, interets, tva, marge, montant, capital_restant, date_paiement, moyen, compte, reference, employe,
        assurance,
        COALESCE((SELECT x.montant_paye FROM v_echeance_pret_paye x WHERE x.echeance_id = v_echeance_pret_complete.id), 0),
        COALESCE((SELECT x.capital_paye FROM v_echeance_pret_paye x WHERE x.echeance_id = v_echeance_pret_complete.id), 0),
        COALESCE((SELECT x.charges_payees FROM v_echeance_pret_paye x WHERE x.echeance_id = v_echeance_pret_complete.id), 0)
        """;

    public async Task<Page<PretResume>> PretsAsync(FiltrePrets filtre, DemandePage page, CancellationToken ct = default)
    {
        var etat = filtre.Etat switch { "rembourses" => "reste_du <= 0", "tous" => "true", _ => "reste_du > 0" };
        await using var cmd = db.CreateCommand($"""
            SELECT {ColonnesPret}, {Lecture.PaginationSql.Total}
            FROM v_pret_bancaire_complet
            WHERE {etat}
              AND ($1::bigint IS NULL OR compte_id = $1)
              AND ($2::text IS NULL OR reference ILIKE '%' || $2 || '%' OR objet ILIKE '%' || $2 || '%' OR banque ILIKE '%' || $2 || '%')
            ORDER BY date_pret DESC, id DESC
            {Lecture.PaginationSql.Limite(3)}
            """);
        cmd.Parameters.Add(Long(filtre.CompteId));
        cmd.Parameters.Add(Texte(filtre.Texte));
        Lecture.PaginationSql.Parametres(cmd, page);
        return await Lecture.PaginationSql.LireAsync(cmd, page, 22, LirePret, ct);
    }

    public async Task<PretDetail?> PretAsync(long id, CancellationToken ct = default)
    {
        PretResume pret;
        await using (var cmd = db.CreateCommand($"SELECT {ColonnesPret} FROM v_pret_bancaire_complet WHERE id = $1"))
        {
            cmd.Parameters.AddWithValue(id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            pret = LirePret(r);
        }
        var echeances = new List<EcheancePret>();
        await using (var cmd = db.CreateCommand($"SELECT {ColonnesEcheance} FROM v_echeance_pret_complete WHERE pret_id = $1 ORDER BY rang"))
        {
            cmd.Parameters.AddWithValue(id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) echeances.Add(LireEcheance(r));
        }
        var utilisations = new List<UtilisationFinancement>();
        await using (var cmd = db.CreateCommand("""
            SELECT f.id, f.source, f.pret_id, f.usage, f.date_usage, f.montant, f.libelle, u.nom_complet
            FROM utilisation_financement f JOIN utilisateur u ON u.id = f.utilisateur_id
            WHERE f.pret_id = $1 ORDER BY f.date_usage DESC, f.id DESC
            """))
        {
            cmd.Parameters.AddWithValue(id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                utilisations.Add(new UtilisationFinancement(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.GetString(3),
                    r.GetFieldValue<DateOnly>(4), r.GetDecimal(5), r.GetString(6), r.GetString(7)));
        }
        var anticipes = new List<RemboursementAnticipe>();
        await using (var cmd = db.CreateCommand("""
            SELECT a.id, a.date_paiement, a.capital, a.frais, a.moyen, c.nom, a.reference, u.nom_complet
            FROM remboursement_anticipe_pret a JOIN utilisateur u ON u.id = a.utilisateur_id LEFT JOIN compte_tresorerie c ON c.id = a.compte_id
            WHERE a.pret_id = $1 ORDER BY a.date_paiement DESC, a.id DESC
            """))
        {
            cmd.Parameters.AddWithValue(id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                anticipes.Add(new RemboursementAnticipe(r.GetInt64(0), r.GetFieldValue<DateOnly>(1), r.GetDecimal(2), r.GetDecimal(3), r.GetString(4),
                    Txt(r, 5), Txt(r, 6), r.GetString(7)));
        }
        return new PretDetail(pret, echeances, utilisations, anticipes);
    }

    public async Task<long> CreerPretAsync(PretAEcrire p, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO pret_bancaire (compte_id, reference, objet, montant, date_pret, preteur, taux, duree_mois, utilisateur_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9) RETURNING id
            """);
        Parametres(cmd, p);
        cmd.Parameters.AddWithValue(p.UtilisateurId);
        try
        {
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new RegleMetierException($"Un prêt de ce prêteur porte déjà la référence « {p.Reference} ».", "reference");
        }
    }

    /// <summary>
    /// Nom d'un prêteur hors trésorerie corrigé : ses colonnes d'import retenues (correspondance_echeancier_preteur, nom en clé) sont
    /// reportées sur le nouveau nom s'il n'en a pas déjà, et l'ancienne entrée retirée si plus aucun prêt ne porte l'ancien nom.
    /// </summary>
    public async Task ModifierPretAsync(long id, PretAEcrire p, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        string? ancien;
        await using (var lecture = new NpgsqlCommand("SELECT preteur FROM pret_bancaire WHERE id = $1 FOR UPDATE", cx, tx))
        {
            lecture.Parameters.AddWithValue(id);
            ancien = await lecture.ExecuteScalarAsync(ct) as string;
        }
        await using (var cmd = new NpgsqlCommand("""
            UPDATE pret_bancaire SET compte_id = $1, reference = $2, objet = $3, montant = $4, date_pret = $5, preteur = $6, taux = $7, duree_mois = $8
            WHERE id = $9
            """, cx, tx))
        {
            Parametres(cmd, p);
            cmd.Parameters.AddWithValue(id);
            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new RegleMetierException($"Un prêt de ce prêteur porte déjà la référence « {p.Reference} ».", "reference");
            }
        }
        if (ancien is not null && p.Preteur is { } nouveau && PreteurPret.Cle(ancien) != PreteurPret.Cle(nouveau))
        {
            await using (var report = new NpgsqlCommand("""
                INSERT INTO correspondance_echeancier_preteur (preteur, col_date, col_capital, col_interets, col_tva, col_marge, col_montant, col_encours,
                                                               lignes_ignorees, col_assurance, utilisateur_id)
                SELECT $2, col_date, col_capital, col_interets, col_tva, col_marge, col_montant, col_encours, lignes_ignorees, col_assurance, $3
                FROM correspondance_echeancier_preteur WHERE preteur = $1
                ON CONFLICT (preteur) DO NOTHING
                """, cx, tx))
            {
                report.Parameters.AddWithValue(PreteurPret.Cle(ancien));
                report.Parameters.AddWithValue(PreteurPret.Cle(nouveau));
                report.Parameters.AddWithValue(p.UtilisateurId);
                await report.ExecuteNonQueryAsync(ct);
            }
            await using var retrait = new NpgsqlCommand("""
                DELETE FROM correspondance_echeancier_preteur c
                WHERE c.preteur = $1 AND NOT EXISTS (SELECT 1 FROM pret_bancaire b WHERE b.compte_id IS NULL AND lower(b.preteur) = $1)
                """, cx, tx);
            retrait.Parameters.AddWithValue(PreteurPret.Cle(ancien));
            await retrait.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task JoindreDocumentAsync(long id, string chemin, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE pret_bancaire SET document = $2 WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(chemin);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemplacerEcheancierAsync(long pretId, int payees, IReadOnlyList<LigneEcheancier> lignes, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        // Prêt verrouillé : un paiement d'échéance simultané attend la fin du remplacement
        await using (var cmd = new NpgsqlCommand("SELECT 1 FROM pret_bancaire WHERE id = $1 FOR UPDATE", cx, tx))
        {
            cmd.Parameters.AddWithValue(pretId);
            if (await cmd.ExecuteScalarAsync(ct) is null) throw new RegleMetierException("Prêt introuvable.");
        }
        await using (var cmd = new NpgsqlCommand("""
            SELECT count(*)::int, COALESCE(max(e.rang), 0) FROM echeance_pret e
            WHERE e.pret_id = $1 AND EXISTS (SELECT 1 FROM paiement_echeance_pret p WHERE p.echeance_id = e.id)
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(pretId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            if (r.GetInt32(0) != payees || r.GetInt32(1) != payees)
                throw new RegleMetierException("Une échéance de ce prêt vient d’être payée : rechargez le prêt.", "echeancier");
        }
        await using (var cmd = new NpgsqlCommand("""
            DELETE FROM echeance_pret e WHERE e.pret_id = $1 AND NOT EXISTS (SELECT 1 FROM paiement_echeance_pret p WHERE p.echeance_id = e.id)
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(pretId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        // Nouveau tableau de la banque importé : les échéances se paient de nouveau (script 170)
        await using (var cmd = new NpgsqlCommand("UPDATE pret_bancaire SET echeancier_a_remplacer = false WHERE id = $1", cx, tx))
        {
            cmd.Parameters.AddWithValue(pretId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var rang = payees;
        foreach (var l in lignes)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO echeance_pret (pret_id, rang, date_echeance, capital, interets, tva, marge, montant, capital_restant, assurance)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
                """, cx, tx);
            cmd.Parameters.AddWithValue(pretId);
            cmd.Parameters.AddWithValue(++rang);
            cmd.Parameters.AddWithValue(l.Date);
            cmd.Parameters.AddWithValue(l.Capital);
            cmd.Parameters.AddWithValue(l.Interets);
            cmd.Parameters.AddWithValue(l.Tva);
            cmd.Parameters.AddWithValue(l.Marge);
            cmd.Parameters.AddWithValue(l.Montant);
            cmd.Parameters.AddWithValue(l.CapitalRestant);
            cmd.Parameters.AddWithValue(l.Assurance);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Banque de la trésorerie : correspondance_echeancier ; prêteur hors trésorerie : correspondance_echeancier_preteur (nom en clé).</summary>
    private static (string Table, string Cle, object Valeur) CleCorrespondance(long? compteId, string? preteur) =>
        compteId is { } id ? ("correspondance_echeancier", "compte_id", id)
        : !string.IsNullOrWhiteSpace(preteur) ? ("correspondance_echeancier_preteur", "preteur", PreteurPret.Cle(preteur))
        : throw new RegleMetierException("Prêteur du prêt introuvable.");

    public async Task<CorrespondanceColonnes?> CorrespondanceAsync(long? compteId, string? preteur, CancellationToken ct = default)
    {
        var (table, cle, valeur) = CleCorrespondance(compteId, preteur);
        await using var cmd = db.CreateCommand($"""
            SELECT col_date, col_capital, col_interets, col_tva, col_marge, col_montant, col_encours, lignes_ignorees, col_assurance
            FROM {table} WHERE {cle} = $1
            """);
        cmd.Parameters.AddWithValue(valeur);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        int? Col(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
        return new CorrespondanceColonnes(r.GetInt32(0), Col(1), Col(2), Col(3), Col(4), Col(5), Col(6), r.GetInt32(7), Col(8));
    }

    public async Task EnregistrerCorrespondanceAsync(long? compteId, string? preteur, CorrespondanceColonnes c, long utilisateurId, CancellationToken ct = default)
    {
        var (table, cle, valeur) = CleCorrespondance(compteId, preteur);
        await using var cmd = db.CreateCommand($"""
            INSERT INTO {table} ({cle}, col_date, col_capital, col_interets, col_tva, col_marge, col_montant, col_encours,
                                 lignes_ignorees, utilisateur_id, col_assurance)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            ON CONFLICT ({cle}) DO UPDATE SET col_date = EXCLUDED.col_date, col_capital = EXCLUDED.col_capital,
                col_interets = EXCLUDED.col_interets, col_tva = EXCLUDED.col_tva, col_marge = EXCLUDED.col_marge, col_montant = EXCLUDED.col_montant,
                col_encours = EXCLUDED.col_encours, lignes_ignorees = EXCLUDED.lignes_ignorees, utilisateur_id = EXCLUDED.utilisateur_id,
                col_assurance = EXCLUDED.col_assurance, modifie_le = now()
            """);
        cmd.Parameters.AddWithValue(valeur);
        cmd.Parameters.AddWithValue(c.Date);
        foreach (var col in new[] { c.Capital, c.Interets, c.Tva, c.Marge, c.Montant, c.Encours })
            cmd.Parameters.Add(Entier(col));
        cmd.Parameters.AddWithValue(c.LignesIgnorees);
        cmd.Parameters.AddWithValue(utilisateurId);
        cmd.Parameters.Add(Entier(c.Assurance));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Page<EcheancePret>> EcheancesAsync(FiltreEcheances filtre, DemandePage page, CancellationToken ct = default)
    {
        var condition = filtre.Etat switch
        {
            "echues" => "date_paiement IS NULL AND date_echeance < $1",
            "a_venir" => "date_paiement IS NULL AND date_echeance >= $1",
            "payees" => "date_paiement IS NOT NULL",
            "toutes" => "true",
            _ => $"date_paiement IS NULL AND date_echeance <= $1 + {ConsulterFinancements.JoursAvantEcheance}"
        };
        var ordre = filtre.Etat == "payees" ? "date_paiement DESC, id DESC" : "date_echeance, id";
        await using var cmd = db.CreateCommand($"""
            SELECT {ColonnesEcheance}, {Lecture.PaginationSql.Total}
            FROM v_echeance_pret_complete
            WHERE {condition}
              AND ($2::text IS NULL OR pret ILIKE '%' || $2 || '%' OR banque ILIKE '%' || $2 || '%')
            ORDER BY {ordre}
            {Lecture.PaginationSql.Limite(3)}
            """);
        cmd.Parameters.AddWithValue(filtre.Aujourdhui);
        cmd.Parameters.Add(Texte(filtre.Texte));
        Lecture.PaginationSql.Parametres(cmd, page);
        return await Lecture.PaginationSql.LireAsync(cmd, page, 21, LireEcheance, ct);
    }

    public async Task<EcheancePret?> EcheanceAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {ColonnesEcheance} FROM v_echeance_pret_complete WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? LireEcheance(r) : null;
    }

    public async Task<long> PayerEcheanceAsync(PaiementEcheanceAEcrire p, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);

        // Prêt verrouillé (puis l'échéance) : deux confirmations simultanées ne paient pas deux fois, l'échéancier ne change pas entre-temps.
        // Déjà payé relu sous verrou (script 170 : une échéance se paie en une ou plusieurs fois).
        string pret;
        DateOnly datePret;
        LigneEcheancier ligne;
        decimal capitalDeja, chargesDeja;
        await using (var cmd = new NpgsqlCommand("""
            SELECT p.reference, p.date_pret, p.echeancier_a_remplacer, e.date_echeance, e.capital, e.interets, e.tva, e.marge, e.montant, e.capital_restant,
                   e.assurance,
                   COALESCE((SELECT sum(x.capital_paye) FROM paiement_echeance_pret x WHERE x.echeance_id = e.id), 0),
                   COALESCE((SELECT sum(x.charges_payees) FROM paiement_echeance_pret x WHERE x.echeance_id = e.id), 0),
                   EXISTS (SELECT 1 FROM echeance_pret a WHERE a.pret_id = e.pret_id AND a.rang < e.rang
                           AND COALESCE((SELECT sum(x.montant) FROM paiement_echeance_pret x WHERE x.echeance_id = a.id), 0) < a.montant)
            FROM echeance_pret e JOIN pret_bancaire p ON p.id = e.pret_id
            WHERE e.id = $1 FOR UPDATE OF p, e
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(p.EcheanceId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new RegleMetierException("Cette échéance est introuvable.");
            (pret, datePret) = (r.GetString(0), r.GetFieldValue<DateOnly>(1));
            if (r.GetBoolean(2))
                throw new RegleMetierException("Un remboursement anticipé a été enregistré sur ce prêt : importez d’abord le nouveau tableau de la banque.");
            ligne = new LigneEcheancier(r.GetFieldValue<DateOnly>(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8),
                r.GetDecimal(9), r.GetDecimal(10));
            (capitalDeja, chargesDeja) = (r.GetDecimal(11), r.GetDecimal(12));
            if (capitalDeja + chargesDeja >= ligne.Montant) throw new RegleMetierException("Cette échéance vient d’être payée depuis un autre poste : rechargez la liste.");
            if (capitalDeja + chargesDeja != p.DejaPaye) throw new RegleMetierException("Un paiement de cette échéance vient d’être enregistré depuis un autre poste : rechargez la liste.");
            if (r.GetBoolean(13)) throw new RegleMetierException("Une échéance plus ancienne de ce prêt n’est pas payée : payez-la d’abord.");
        }
        if (p.Date < datePret) throw new RegleMetierException("La date du paiement précède la date du prêt.", "date");
        var (capital, charges) = RegleEcheancier.Imputer(ligne, capitalDeja, chargesDeja, p.Montant);

        long compte;
        long? versementId = null;
        if (p.Moyen == "especes")
        {
            compte = await ReglementsFournisseursSql.CoffreAsync(cx, tx, ct);
            if (p.PartTiroir > 0)
                versementId = await VerserTiroirAsync(cx, tx, compte, p.Date, p.PartTiroir, p.SessionCaisseId!.Value,
                    $"paiement de l’échéance du prêt {pret}", p.UtilisateurId, ct);
        }
        else compte = p.CompteId!.Value;
        // Découvert accepté sur une banque (décision du titulaire) ; le coffre ne paie jamais plus qu'il ne contient
        await SoldeTresorerie.ExigerPourEcheanceAsync(cx, tx, compte, p.Montant, ct, p.Moyen == "especes" ? " : complétez avec l’argent du tiroir de la caisse" : null);

        var partiel = capitalDeja + chargesDeja + p.Montant < ligne.Montant || capitalDeja + chargesDeja > 0;
        var libelle = $"Échéance du {ligne.Date:dd/MM/yyyy} · prêt {pret}{(partiel ? " (paiement partiel)" : "")}";
        long? mouvementCapital = capital > 0
            ? await MouvementAsync(cx, tx, compte, p.Date, "sortie", capital, "remboursement_financement", null, libelle + " (capital)", p.Reference, p.UtilisateurId, ct)
            : null;
        // Intérêts, assurance, TVA et marge : une seule dépense « charges financières »
        long? mouvementCharges = charges > 0
            ? await MouvementAsync(cx, tx, compte, p.Date, "sortie", charges, "depense", RegleTresorerie.ChargesFinancieres,
                libelle + " (intérêts, assurance, TVA, marge)", p.Reference, p.UtilisateurId, ct)
            : null;

        long id;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO paiement_echeance_pret (echeance_id, date_paiement, moyen, compte_id, reference, part_tiroir, versement_id,
                                                mouvement_capital_id, mouvement_charges_id, utilisateur_id, montant, capital_paye, charges_payees)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13) RETURNING id
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(p.EcheanceId);
            cmd.Parameters.AddWithValue(p.Date);
            cmd.Parameters.AddWithValue(p.Moyen);
            cmd.Parameters.Add(Long(p.Moyen == "especes" ? null : p.CompteId));
            cmd.Parameters.Add(Texte(p.Reference));
            cmd.Parameters.AddWithValue(p.PartTiroir);
            cmd.Parameters.Add(Long(versementId));
            cmd.Parameters.Add(Long(mouvementCapital));
            cmd.Parameters.Add(Long(mouvementCharges));
            cmd.Parameters.AddWithValue(p.UtilisateurId);
            cmd.Parameters.AddWithValue(p.Montant);
            cmd.Parameters.AddWithValue(capital);
            cmd.Parameters.AddWithValue(charges);
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<long> RembourserParAnticipationAsync(RemboursementAnticipeAEcrire a, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);

        // Prêt et ses échéances verrouillés : capital remboursé relu, aucune échéance payée en partie
        string pret;
        decimal montantPret, capitalRembourse;
        await using (var cmd = new NpgsqlCommand("""
            SELECT p.reference, p.montant,
                   COALESCE((SELECT sum(x.capital_paye) FROM paiement_echeance_pret x JOIN echeance_pret e ON e.id = x.echeance_id WHERE e.pret_id = p.id), 0)
                   + COALESCE((SELECT sum(r.capital) FROM remboursement_anticipe_pret r WHERE r.pret_id = p.id), 0),
                   EXISTS (SELECT 1 FROM echeance_pret e WHERE e.pret_id = p.id
                           AND COALESCE((SELECT sum(x.montant) FROM paiement_echeance_pret x WHERE x.echeance_id = e.id), 0) BETWEEN 0.01 AND e.montant - 0.01)
            FROM pret_bancaire p WHERE p.id = $1 FOR UPDATE
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(a.PretId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new RegleMetierException("Ce prêt est introuvable.");
            (pret, montantPret, capitalRembourse) = (r.GetString(0), r.GetDecimal(1), r.GetDecimal(2));
            if (capitalRembourse != a.CapitalDejaRembourse)
                throw new RegleMetierException("Un paiement de ce prêt vient d’être enregistré depuis un autre poste : rechargez le prêt.");
            if (r.GetBoolean(3)) throw new RegleMetierException("Une échéance de ce prêt est payée en partie : terminez d’abord son paiement.");
        }
        RegleRemboursementAnticipe.Verifier(a.Capital, a.Frais, montantPret - capitalRembourse);

        long compte;
        long? versementId = null;
        if (a.Moyen == "especes")
        {
            compte = await ReglementsFournisseursSql.CoffreAsync(cx, tx, ct);
            if (a.PartTiroir > 0)
                versementId = await VerserTiroirAsync(cx, tx, compte, a.Date, a.PartTiroir, a.SessionCaisseId!.Value,
                    $"remboursement anticipé du prêt {pret}", a.UtilisateurId, ct);
        }
        else compte = a.CompteId!.Value;
        await SoldeTresorerie.ExigerPourEcheanceAsync(cx, tx, compte, a.Capital + a.Frais, ct,
            a.Moyen == "especes" ? " : complétez avec l’argent du tiroir de la caisse" : null);

        var libelle = $"Remboursement anticipé · prêt {pret}";
        var mouvementCapital = await MouvementAsync(cx, tx, compte, a.Date, "sortie", a.Capital, "remboursement_financement", null, libelle + " (capital)",
            a.Reference, a.UtilisateurId, ct);
        // Frais : charges financières, comme les intérêts (décision du titulaire du 2026-11-26)
        long? mouvementFrais = a.Frais > 0
            ? await MouvementAsync(cx, tx, compte, a.Date, "sortie", a.Frais, "depense", RegleTresorerie.ChargesFinancieres, libelle + " (frais)",
                a.Reference, a.UtilisateurId, ct)
            : null;

        long id;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO remboursement_anticipe_pret (pret_id, date_paiement, capital, frais, moyen, compte_id, reference, part_tiroir, versement_id,
                                                     mouvement_capital_id, mouvement_frais_id, utilisateur_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12) RETURNING id
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(a.PretId);
            cmd.Parameters.AddWithValue(a.Date);
            cmd.Parameters.AddWithValue(a.Capital);
            cmd.Parameters.AddWithValue(a.Frais);
            cmd.Parameters.AddWithValue(a.Moyen);
            cmd.Parameters.Add(Long(a.Moyen == "especes" ? null : a.CompteId));
            cmd.Parameters.Add(Texte(a.Reference));
            cmd.Parameters.AddWithValue(a.PartTiroir);
            cmd.Parameters.Add(Long(versementId));
            cmd.Parameters.AddWithValue(mouvementCapital);
            cmd.Parameters.Add(Long(mouvementFrais));
            cmd.Parameters.AddWithValue(a.UtilisateurId);
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        // Capital encore dû : échéances non payées bloquées jusqu'au nouveau tableau ; tout remboursé : elles sont retirées
        var solde = montantPret - capitalRembourse - a.Capital == 0;
        await using (var cmd = new NpgsqlCommand(solde
            ? "DELETE FROM echeance_pret e WHERE e.pret_id = $1 AND NOT EXISTS (SELECT 1 FROM paiement_echeance_pret x WHERE x.echeance_id = e.id)"
            : "UPDATE pret_bancaire SET echeancier_a_remplacer = true WHERE id = $1", cx, tx))
        {
            cmd.Parameters.AddWithValue(a.PretId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<long> UtiliserAsync(UtilisationAEcrire u, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        var origine = await LibelleSourceAsync(cx, tx, u.Source, ct);
        var entree = await MouvementAsync(cx, tx, u.CompteId, u.Date, "entree", u.Montant, "financement", null,
            u.Usage == "depense" ? $"{origine} · pour : {u.Libelle}" : (u.Libelle == origine ? origine : $"{origine} · {u.Libelle}"), u.Reference, u.UtilisateurId, ct);
        long? depense = u.Usage == "depense"
            ? await MouvementAsync(cx, tx, u.CompteId, u.Date, "sortie", u.Montant, "depense", u.Categorie, $"{u.Libelle} (payée par : {origine})", u.Reference,
                u.UtilisateurId, ct)
            : null;
        var id = await EnregistrerUtilisationAsync(cx, tx, u.Source, u.Usage, u.Date, u.Montant, u.Libelle, entree, depense, null, null, u.UtilisateurId, ct);
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<CompteTitulaire> CompteTitulaireAsync(FiltreCompteTitulaire filtre, DemandePage page, CancellationToken ct = default)
    {
        decimal apports, remboursements;
        await using (var cmd = db.CreateCommand("""
            SELECT COALESCE(sum(montant) FILTER (WHERE sens = 'apport'), 0), COALESCE(sum(montant) FILTER (WHERE sens = 'remboursement'), 0)
            FROM v_compte_titulaire
            """))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            (apports, remboursements) = (r.GetDecimal(0), r.GetDecimal(1));
        }
        await using var liste = db.CreateCommand($"""
            SELECT date, sens, montant, usage, libelle, employe, {Lecture.PaginationSql.Total}
            FROM v_compte_titulaire
            WHERE ($1::text IS NULL OR sens = $1) AND ($2::date IS NULL OR date >= $2) AND ($3::date IS NULL OR date <= $3)
            ORDER BY date DESC, cree_le DESC
            {Lecture.PaginationSql.Limite(4)}
            """);
        liste.Parameters.Add(Texte(filtre.Sens));
        liste.Parameters.Add(new NpgsqlParameter { Value = (object?)filtre.Du ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
        liste.Parameters.Add(new NpgsqlParameter { Value = (object?)filtre.Au ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
        Lecture.PaginationSql.Parametres(liste, page);
        var mouvements = await Lecture.PaginationSql.LireAsync(liste, page, 6, r => new MouvementTitulaire(r.GetFieldValue<DateOnly>(0), r.GetString(1),
            r.GetDecimal(2), r.GetString(3), r.GetString(4), r.GetString(5)), ct);
        return new CompteTitulaire(RegleCompteTitulaire.Solde([apports], [remboursements]), apports, remboursements, mouvements);
    }

    public async Task<long> RembourserTitulaireAsync(RemboursementTitulaireAEcrire r, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        // Un seul remboursement à la fois : verrou transactionnel propre au compte du titulaire, puis solde dû relu
        await using (var verrou = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('compte_titulaire'))", cx, tx))
            await verrou.ExecuteNonQueryAsync(ct);
        await using (var cmd = new NpgsqlCommand("SELECT COALESCE(sum(CASE WHEN sens = 'apport' THEN montant ELSE -montant END), 0) FROM v_compte_titulaire", cx, tx))
            RegleCompteTitulaire.VerifierRemboursement(r.Montant, (decimal)(await cmd.ExecuteScalarAsync(ct))!);
        await SoldeTresorerie.ExigerAsync(cx, tx, r.CompteId, r.Montant, ct);
        var mouvement = await MouvementAsync(cx, tx, r.CompteId, r.Date, "sortie", r.Montant, "remboursement_financement", null, r.Libelle, r.Reference,
            r.UtilisateurId, ct);
        long id;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO remboursement_titulaire (date_remboursement, montant, compte_id, mouvement_id, libelle, reference, utilisateur_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING id
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(r.Date);
            cmd.Parameters.AddWithValue(r.Montant);
            cmd.Parameters.AddWithValue(r.CompteId);
            cmd.Parameters.AddWithValue(mouvement);
            cmd.Parameters.AddWithValue(r.Libelle);
            cmd.Parameters.Add(Texte(r.Reference));
            cmd.Parameters.AddWithValue(r.UtilisateurId);
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<IReadOnlyList<SourceDisponible>> SourcesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, reference, banque, montant - utilise FROM v_pret_bancaire WHERE montant - utilise > 0 ORDER BY date_pret DESC, id DESC
            """);
        var liste = new List<SourceDisponible>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new SourceDisponible(new SourceFinancement(r.GetInt64(0)).Code, $"Prêt {r.GetString(1)} ({r.GetString(2)})", r.GetDecimal(3)));
        return liste;
    }

    /// <summary>
    /// Usage d'une source, dans la transaction de l'appelant (règlement d'une facture, paiement d'une traite, versement, dépense) :
    /// prêt verrouillé et jamais employé au-delà de son montant ; un apport du titulaire n'a pas de plafond (il crée la dette).
    /// </summary>
    internal static async Task<long> EnregistrerUtilisationAsync(NpgsqlConnection cx, NpgsqlTransaction tx, SourceFinancement source, string usage, DateOnly date,
        decimal montant, string libelle, long? mouvementId, long? depenseId, long? reglementId, long? paiementEffetId, long utilisateurId, CancellationToken ct)
    {
        if (source.PretId is { } pretId)
        {
            // Verrou du prêt dans une requête à part : en lecture validée, une requête garde l'instantané pris avant d'attendre le
            // verrou ; le disponible doit être relu par une requête suivante pour voir un emploi validé pendant l'attente
            await using (var verrou = new NpgsqlCommand("SELECT 1 FROM pret_bancaire WHERE id = $1 FOR UPDATE", cx, tx))
            {
                verrou.Parameters.AddWithValue(pretId);
                if (await verrou.ExecuteScalarAsync(ct) is null) throw new RegleMetierException("Ce prêt est introuvable.", "source");
            }
            await using var cmd = new NpgsqlCommand("""
                SELECT p.montant - COALESCE((SELECT sum(u.montant) FROM utilisation_financement u WHERE u.pret_id = p.id), 0), p.reference, p.date_pret
                FROM pret_bancaire p WHERE p.id = $1
                """, cx, tx);
            cmd.Parameters.AddWithValue(pretId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new RegleMetierException("Ce prêt est introuvable.", "source");
            var (disponible, reference, datePret) = (r.GetDecimal(0), r.GetString(1), r.GetFieldValue<DateOnly>(2));
            if (montant > disponible)
                throw new RegleMetierException($"Il ne reste que {Math.Max(disponible, 0):N2} DA à employer sur le prêt {reference}.", "source");
            if (date < datePret) throw new RegleMetierException($"La date précède celle du prêt {reference} ({datePret:dd/MM/yyyy}).", "date");
        }
        await using var insert = new NpgsqlCommand("""
            INSERT INTO utilisation_financement (source, pret_id, usage, date_usage, montant, libelle, mouvement_id, depense_id, reglement_id,
                                                 paiement_effet_id, utilisateur_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) RETURNING id
            """, cx, tx);
        insert.Parameters.AddWithValue(source.Type);
        insert.Parameters.Add(Long(source.PretId));
        insert.Parameters.AddWithValue(usage);
        insert.Parameters.AddWithValue(date);
        insert.Parameters.AddWithValue(montant);
        insert.Parameters.AddWithValue(libelle);
        insert.Parameters.Add(Long(mouvementId));
        insert.Parameters.Add(Long(depenseId));
        insert.Parameters.Add(Long(reglementId));
        insert.Parameters.Add(Long(paiementEffetId));
        insert.Parameters.AddWithValue(utilisateurId);
        return (long)(await insert.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Nom de la source tel qu'il s'écrit dans la trésorerie : « Apport du titulaire » ou « Prêt REF (banque) ».</summary>
    internal static async Task<string> LibelleSourceAsync(NpgsqlConnection cx, NpgsqlTransaction tx, SourceFinancement source, CancellationToken ct)
    {
        if (source.PretId is not { } id) return "Apport du titulaire";
        // Banque de la trésorerie, sinon prêteur hors trésorerie : la source reste nommée dans chaque écriture
        await using var cmd = new NpgsqlCommand("""
            SELECT 'Prêt ' || p.reference || ' (' || COALESCE(c.nom, p.preteur) || ')'
            FROM pret_bancaire p LEFT JOIN compte_tresorerie c ON c.id = p.compte_id WHERE p.id = $1
            """, cx, tx);
        cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteScalarAsync(ct) as string ?? throw new RegleMetierException("Ce prêt est introuvable.", "source");
    }

    /// <summary>Argent du tiroir de la caisse ouverte porté au coffre avant un paiement en espèces (l'attendu de la caisse diminue d'autant).</summary>
    private static async Task<long> VerserTiroirAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long coffre, DateOnly date, decimal montant, long sessionId,
        string objet, long utilisateurId, CancellationToken ct)
    {
        await using (var verrou = new NpgsqlCommand("SELECT fermee_le IS NULL FROM session_caisse WHERE id = $1 FOR UPDATE", cx, tx))
        {
            verrou.Parameters.AddWithValue(sessionId);
            if (await verrou.ExecuteScalarAsync(ct) is not true)
                throw new RegleMetierException("La caisse du poste vient d’être clôturée : ouvrez-la et recommencez.", "tiroir");
        }
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO mouvement_tresorerie (compte_id, date_mouvement, sens, montant, nature, libelle, session_caisse_id, pendant_session, automatique, utilisateur_id)
            SELECT $1, $2, 'entree', $3, 'versement_caisse', 'Argent de la caisse porté au coffre (' || s.poste || ') · ' || $5, s.id, true, false, $6
            FROM session_caisse s WHERE s.id = $4 RETURNING id
            """, cx, tx);
        cmd.Parameters.AddWithValue(coffre);
        cmd.Parameters.AddWithValue(date);
        cmd.Parameters.AddWithValue(montant);
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(objet);
        cmd.Parameters.AddWithValue(utilisateurId);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<long> MouvementAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long compteId, DateOnly date, string sens, decimal montant,
        string nature, string? categorie, string libelle, string? reference, long utilisateurId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO mouvement_tresorerie (compte_id, date_mouvement, sens, montant, nature, categorie, libelle, reference, utilisateur_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9) RETURNING id
            """, cx, tx);
        cmd.Parameters.AddWithValue(compteId);
        cmd.Parameters.AddWithValue(date);
        cmd.Parameters.AddWithValue(sens);
        cmd.Parameters.AddWithValue(montant);
        cmd.Parameters.AddWithValue(nature);
        cmd.Parameters.Add(Texte(categorie));
        cmd.Parameters.AddWithValue(libelle);
        cmd.Parameters.Add(Texte(reference));
        cmd.Parameters.AddWithValue(utilisateurId);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static void Parametres(NpgsqlCommand cmd, PretAEcrire p)
    {
        cmd.Parameters.Add(Long(p.CompteId));
        cmd.Parameters.AddWithValue(p.Reference);
        cmd.Parameters.Add(Texte(p.Objet));
        cmd.Parameters.AddWithValue(p.Montant);
        cmd.Parameters.AddWithValue(p.DatePret);
        cmd.Parameters.Add(Texte(p.Preteur));
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)p.Taux ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric });
        cmd.Parameters.Add(Entier(p.DureeMois));
    }

    private static PretResume LirePret(NpgsqlDataReader r) => new(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2), r.GetString(3), Txt(r, 4), r.GetDecimal(5),
        r.GetFieldValue<DateOnly>(6), Txt(r, 7), r.GetDecimal(8), r.GetDecimal(9), r.GetDecimal(10), r.GetInt32(11), r.GetInt32(12), r.GetInt32(13),
        r.IsDBNull(14) ? null : r.GetFieldValue<DateOnly>(14), r.IsDBNull(15) ? null : r.GetDecimal(15), r.GetString(16),
        r.IsDBNull(17) ? null : r.GetDecimal(17), r.IsDBNull(18) ? null : r.GetInt32(18), r.IsDBNull(19) ? null : r.GetDecimal(19),
        !r.IsDBNull(20) && r.GetBoolean(20), r.GetDecimal(21));

    private static EcheancePret LireEcheance(NpgsqlDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt32(4),
        new LigneEcheancier(r.GetFieldValue<DateOnly>(5), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), r.GetDecimal(10), r.GetDecimal(11),
            r.GetDecimal(17)),
        r.IsDBNull(12) ? null : r.GetFieldValue<DateOnly>(12), Txt(r, 13), Txt(r, 14), Txt(r, 15), Txt(r, 16), r.GetDecimal(18), r.GetDecimal(19), r.GetDecimal(20));

    private static string? Txt(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static NpgsqlParameter Texte(string? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };
    private static NpgsqlParameter Long(long? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint };
    private static NpgsqlParameter Entier(int? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer };
}
