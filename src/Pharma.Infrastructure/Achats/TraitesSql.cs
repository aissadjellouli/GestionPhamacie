using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Achats;
using Pharma.Application.Commun;
using Pharma.Domain.Achats;
using Pharma.Domain.Commun;
using Pharma.Infrastructure.Tresorerie;

namespace Pharma.Infrastructure.Achats;

/// <summary>Traites (effets) remises aux grossistes et leur paiement à l'échéance (074_tresorerie_traites.sql, paiement_effet).</summary>
public sealed class TraitesSql(NpgsqlDataSource db) : ITraites
{
    private const string Select = """
        SELECT g.id, g.numero, g.fournisseur_id, f.raison_sociale, g.reference, g.banque, g.date_reglement, g.echeance, g.montant,
               g.compte_id, c.nom
        FROM reglement_fournisseur g
        JOIN fournisseur f ON f.id = g.fournisseur_id
        LEFT JOIN compte_tresorerie c ON c.id = g.compte_id
        WHERE g.moyen = 'effet' AND NOT EXISTS (SELECT 1 FROM paiement_effet pe WHERE pe.reglement_id = g.id)
        """;

    public async Task<Page<TraiteAPayer>> ListerAsync(FiltreTraites filtre, DemandePage page, CancellationToken ct = default)
    {
        // Payée : paiement confirmé, ou traite remise avec une banque dont l'échéance est passée (débitée sans action)
        var condition = filtre.Etat switch
        {
            "echues" => "t.payee_le IS NULL AND t.echeance < $1",
            "a_venir" => "t.payee_le IS NULL AND t.echeance >= $1",
            "payees" => "t.payee_le IS NOT NULL",
            "toutes" => "true",
            _ => $"t.payee_le IS NULL AND t.echeance <= $1 + {ConsulterTraites.JoursAvantEcheance}"
        };
        var ordre = filtre.Etat == "payees" ? "t.payee_le DESC, t.id DESC" : "t.echeance, t.id";
        await using var cmd = db.CreateCommand($"""
            SELECT t.*, {Lecture.PaginationSql.Total} FROM (
                SELECT g.id, g.numero, g.fournisseur_id, f.raison_sociale, g.reference, g.banque, g.date_reglement, g.echeance, g.montant,
                       g.compte_id, c.nom,
                       COALESCE(pe.date_paiement, CASE WHEN g.compte_id IS NOT NULL AND g.echeance < $1 THEN g.echeance END) AS payee_le,
                       COALESCE(pe.moyen, CASE WHEN g.compte_id IS NOT NULL AND g.echeance < $1 THEN 'debit_echeance' END) AS moyen_paiement
                FROM reglement_fournisseur g
                JOIN fournisseur f ON f.id = g.fournisseur_id
                LEFT JOIN compte_tresorerie c ON c.id = g.compte_id
                LEFT JOIN paiement_effet pe ON pe.reglement_id = g.id
                WHERE g.moyen = 'effet'
                  AND ($2::text IS NULL OR f.raison_sociale ILIKE '%' || $2 || '%' OR g.reference ILIKE '%' || $2 || '%' OR g.numero ILIKE '%' || $2 || '%')
            ) t
            WHERE {condition}
            ORDER BY {ordre}
            {Lecture.PaginationSql.Limite(3)}
            """);
        cmd.Parameters.AddWithValue(filtre.Aujourdhui);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)filtre.Texte ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        Lecture.PaginationSql.Parametres(cmd, page);
        return await Lecture.PaginationSql.LireAsync(cmd, page, 13, r => Lire(r) with
        {
            PayeeLe = r.IsDBNull(11) ? null : DateOnly.FromDateTime(r.GetDateTime(11)),
            MoyenPaiement = r.IsDBNull(12) ? null : r.GetString(12)
        }, ct);
    }

    public async Task<TraiteAPayer?> TraiteAsync(long reglementId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"{Select} AND g.id = $1");
        cmd.Parameters.AddWithValue(reglementId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Lire(r) : null;
    }

    public async Task<long> PayerAsync(PaiementTraiteAEcrire p, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);

        // Traite verrouillée : deux confirmations simultanées ne la paient pas deux fois
        decimal montant;
        string? numero;
        bool debitALEcheance;
        await using (var cmd = new NpgsqlCommand("""
            SELECT g.montant, g.reference, g.compte_id IS NOT NULL, EXISTS (SELECT 1 FROM paiement_effet pe WHERE pe.reglement_id = g.id)
            FROM reglement_fournisseur g WHERE g.id = $1 AND g.moyen = 'effet' FOR UPDATE
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(p.ReglementId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new RegleMetierException("Cette traite est introuvable.");
            if (r.GetBoolean(3)) throw new RegleMetierException("Cette traite vient d’être payée depuis un autre poste : rechargez la liste.");
            (montant, numero, debitALEcheance) = (r.GetDecimal(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetBoolean(2));
        }

        long? versementId = null;
        if (!debitALEcheance && p.Moyen == MoyenReglement.Especes)
        {
            var coffre = await ReglementsFournisseursSql.CoffreAsync(cx, tx, ct);
            // Complément : l'argent du tiroir de la caisse ouverte est d'abord porté au coffre (l'attendu de la caisse diminue d'autant)
            if (p.PartTiroir > 0)
            {
                await using (var verrou = new NpgsqlCommand("SELECT fermee_le IS NULL FROM session_caisse WHERE id = $1 FOR UPDATE", cx, tx))
                {
                    verrou.Parameters.AddWithValue(p.SessionCaisseId!.Value);
                    if (await verrou.ExecuteScalarAsync(ct) is not true)
                        throw new RegleMetierException("La caisse du poste vient d’être clôturée : ouvrez-la et recommencez.", "tiroir");
                }
                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO mouvement_tresorerie (compte_id, date_mouvement, sens, montant, nature, libelle, session_caisse_id, pendant_session, automatique, utilisateur_id)
                    SELECT $1, $2, 'entree', $3, 'versement_caisse',
                           'Argent de la caisse porté au coffre (' || s.poste || ') · paiement de la traite ' || COALESCE($5, ''),
                           s.id, true, false, $6
                    FROM session_caisse s WHERE s.id = $4 RETURNING id
                    """, cx, tx);
                cmd.Parameters.AddWithValue(coffre);
                cmd.Parameters.AddWithValue(p.Date);
                cmd.Parameters.AddWithValue(p.PartTiroir);
                cmd.Parameters.AddWithValue(p.SessionCaisseId!.Value);
                cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)numero ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
                cmd.Parameters.AddWithValue(p.UtilisateurId);
                versementId = (long)(await cmd.ExecuteScalarAsync(ct))!;
            }
            // Le coffre (complément compris) doit couvrir la traite : relu sous verrou
            await SoldeTresorerie.ExigerAsync(cx, tx, coffre, montant, ct, " : complétez avec l’argent du tiroir de la caisse");
        }

        long id;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO paiement_effet (reglement_id, date_paiement, moyen, compte_id, reference, part_tiroir, versement_id, utilisateur_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8) RETURNING id
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(p.ReglementId);
            cmd.Parameters.AddWithValue(p.Date);
            cmd.Parameters.AddWithValue(p.Moyen.Code());
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)p.CompteId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)p.Reference ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.AddWithValue(p.PartTiroir);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)versementId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            cmd.Parameters.AddWithValue(p.UtilisateurId);
            try
            {
                id = (long)(await cmd.ExecuteScalarAsync(ct))!;
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new RegleMetierException("Cette traite vient d’être payée depuis un autre poste : rechargez la liste.");
            }
        }
        await tx.CommitAsync(ct);
        return id;
    }

    private static TraiteAPayer Lire(NpgsqlDataReader r) => new(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), DateOnly.FromDateTime(r.GetDateTime(6)),
        DateOnly.FromDateTime(r.GetDateTime(7)), r.GetDecimal(8), r.IsDBNull(9) ? null : r.GetInt64(9), r.IsDBNull(10) ? null : r.GetString(10));
}
