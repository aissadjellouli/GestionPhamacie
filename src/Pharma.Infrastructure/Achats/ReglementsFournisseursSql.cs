using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Commun;
using Pharma.Application.Achats;
using Pharma.Domain.Achats;
using Pharma.Domain.Commun;
using Pharma.Infrastructure.Tresorerie;

namespace Pharma.Infrastructure.Achats;

/// <summary>Règlements des grossistes (029_reglements.sql).</summary>
public sealed class ReglementsFournisseursSql(NpgsqlDataSource db) : IReglementsFournisseurs, Pharma.Application.Caisse.IAnnulationsReglementTiroir
{
    public async Task<IReadOnlyList<GrossisteAReglement>> GrossistesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT f.id, f.raison_sociale, f.solde,
                   COALESCE((SELECT SUM(reste) FROM v_facture_fournisseur v WHERE v.fournisseur_id = f.id AND v.reste > 0), 0),
                   (SELECT count(*) FROM v_facture_fournisseur v WHERE v.fournisseur_id = f.id AND v.reste > 0)::int
            FROM fournisseur f
            ORDER BY f.solde DESC, f.raison_sociale
            """);
        var liste = new List<GrossisteAReglement>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new GrossisteAReglement(r.GetInt64(0), r.GetString(1), r.GetDecimal(2), r.GetDecimal(3), r.GetInt32(4)));
        return liste;
    }

    public async Task<SituationGrossiste?> SituationAsync(long fournisseurId, CancellationToken ct = default)
    {
        string nom;
        decimal solde;
        await using (var cmd = db.CreateCommand("SELECT raison_sociale, solde FROM fournisseur WHERE id = $1"))
        {
            cmd.Parameters.AddWithValue(fournisseurId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            (nom, solde) = (r.GetString(0), r.GetDecimal(1));
        }

        var factures = new List<FactureFournisseur>();
        await using (var cmd = db.CreateCommand("""
            SELECT reception_id, numero_facture, numero_bl, date_reception, montant, regle, reste
            FROM v_facture_fournisseur WHERE fournisseur_id = $1 AND reste > 0 ORDER BY date_reception, reception_id
            """))
        {
            cmd.Parameters.AddWithValue(fournisseurId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                factures.Add(new FactureFournisseur(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                    r.GetDateTime(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6)));
        }

        var reglements = new List<ReglementPasse>();
        await using (var cmd = db.CreateCommand("""
            SELECT g.id, g.date_reglement, g.montant, g.moyen, g.reference, g.echeance, g.part_tiroir, u.nom_complet, g.utilisateur_id,
                   g.statut = 'annule', g.annule_le, g.motif_annulation
            FROM reglement_fournisseur g JOIN utilisateur u ON u.id = g.utilisateur_id
            WHERE g.fournisseur_id = $1 ORDER BY g.date_reglement DESC, g.id DESC LIMIT 20
            """))
        {
            cmd.Parameters.AddWithValue(fournisseurId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                reglements.Add(new ReglementPasse(r.GetInt64(0), DateOnly.FromDateTime(r.GetDateTime(1)), r.GetDecimal(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : DateOnly.FromDateTime(r.GetDateTime(5)), r.GetDecimal(6), r.GetString(7), r.GetInt64(8),
                    r.GetBoolean(9), r.IsDBNull(10) ? null : r.GetDateTime(10), r.IsDBNull(11) ? null : r.GetString(11)));
        }

        // Relevé de la dette (039) : première page des mouvements, du plus récent au plus ancien
        var mouvements = await MouvementsAsync(fournisseurId, new DemandePage(), null, ct);
        return new SituationGrossiste(fournisseurId, nom, solde, factures, reglements, mouvements.Lignes, mouvements.Total);
    }

    public async Task<Page<MouvementGrossiste>> MouvementsAsync(long fournisseurId, DemandePage page, FiltreReleve? filtre = null, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT m.cree_le, m.type, m.montant, m.solde_apres, u.nom_complet, m.reference, {Lecture.PaginationSql.Total}, m.utilisateur_id
            FROM fournisseur_mouvement m LEFT JOIN utilisateur u ON u.id = m.utilisateur_id
            WHERE m.fournisseur_id = $1
              AND ($2::date IS NULL OR m.cree_le >= $2::date)
              AND ($3::date IS NULL OR m.cree_le < $3::date + 1)
              AND ($4::text IS NULL OR m.type = $4)
            ORDER BY m.id DESC
            {Lecture.PaginationSql.Limite(5)}
            """);
        cmd.Parameters.AddWithValue(fournisseurId);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)filtre?.Du ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)filtre?.Au ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)filtre?.Type ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        Lecture.PaginationSql.Parametres(cmd, page);
        return await Lecture.PaginationSql.LireAsync(cmd, page, 6, r => new MouvementGrossiste(r.GetDateTime(0), r.GetString(1), r.GetDecimal(2),
            r.GetDecimal(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(7) ? null : r.GetInt64(7)), ct);
    }

    public async Task<long> EnregistrerAsync(ReglementAEcrire g, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        var id = await EcrireAsync(cx, tx, g, ct);
        await tx.CommitAsync(ct);
        return id;
    }

    /// <summary>Règlement écrit dans la transaction de l'appelant (aussi le paiement d'un grossiste depuis le tiroir, script 096).</summary>
    internal static async Task<long> EcrireAsync(NpgsqlConnection cx, NpgsqlTransaction tx, ReglementAEcrire g, CancellationToken ct)
    {
        if (g.Moyen == MoyenReglement.Financement && g.Source is null)
            throw new RegleMetierException("Un règlement par prêt ou apport indique sa source.", "source");
        // Grossiste verrouillé : deux règlements simultanés ne paient pas deux fois la même facture
        await using (var cmd = new NpgsqlCommand("SELECT solde FROM fournisseur WHERE id = $1 FOR UPDATE", cx, tx))
        {
            cmd.Parameters.AddWithValue(g.FournisseurId);
            if (await cmd.ExecuteScalarAsync(ct) is not decimal solde || g.Montant > Math.Max(solde, 0) + 0.005m)
                throw new RegleMetierException("La dette envers ce grossiste vient de changer : rechargez sa situation.");
        }
        // Espèces : la part du coffre doit y être (solde relu sous verrou) ; sinon le complément se prend dans le tiroir de la caisse
        if (g.Moyen == MoyenReglement.Especes && g.Montant > g.PartTiroir)
            await SoldeTresorerie.ExigerAsync(cx, tx, await CoffreAsync(cx, tx, ct), g.Montant - g.PartTiroir, ct,
                " : prenez le complément dans le tiroir de la caisse (« Compléter avec le tiroir »)");
        foreach (var (receptionId, montant) in g.Imputations)
        {
            await using var cmd = new NpgsqlCommand("SELECT reste FROM v_facture_fournisseur WHERE reception_id = $1 AND fournisseur_id = $2", cx, tx);
            cmd.Parameters.AddWithValue(receptionId);
            cmd.Parameters.AddWithValue(g.FournisseurId);
            if (await cmd.ExecuteScalarAsync(ct) is not decimal reste || reste < montant)
                throw new RegleMetierException("Une facture vient d’être réglée entre-temps : rechargez la situation du grossiste.");
        }

        long id;
        string numero;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO reglement_fournisseur (fournisseur_id, date_reglement, montant, moyen, reference, banque, echeance, part_tiroir,
                                               session_caisse_id, utilisateur_id, commentaire, compte_id, numero)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, 'RG-' || lpad(nextval('seq_numero_reglement_fournisseur')::text, 6, '0'))
            RETURNING id, numero
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(g.FournisseurId);
            cmd.Parameters.AddWithValue(g.Date);
            cmd.Parameters.AddWithValue(g.Montant);
            cmd.Parameters.AddWithValue(g.Moyen.Code());
            cmd.Parameters.Add(Texte(g.Reference));
            cmd.Parameters.Add(Texte(g.Banque));
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)g.Echeance ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
            cmd.Parameters.AddWithValue(g.PartTiroir);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)g.SessionCaisseId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            cmd.Parameters.AddWithValue(g.UtilisateurId);
            cmd.Parameters.Add(Texte(g.Commentaire));
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)g.CompteId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            await using var lu = await cmd.ExecuteReaderAsync(ct);
            await lu.ReadAsync(ct);
            (id, numero) = (lu.GetInt64(0), lu.GetString(1));
        }
        foreach (var (receptionId, montant) in g.Imputations)
        {
            await using var cmd = new NpgsqlCommand("INSERT INTO reglement_facture (reglement_id, reception_id, montant) VALUES ($1, $2, $3)", cx, tx);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(receptionId);
            cmd.Parameters.AddWithValue(montant);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await ReleveGrossiste.ContexteAsync(cx, tx, ReleveGrossiste.Reglement, g.UtilisateurId,
            $"Règlement {numero} ({MoyenEnClair(g.Moyen.Code())}{(g.Reference is null ? "" : " " + g.Reference)})", ct);
        await using (var cmd = new NpgsqlCommand("UPDATE fournisseur SET solde = solde - $2 WHERE id = $1", cx, tx))
        {
            cmd.Parameters.AddWithValue(g.FournisseurId);
            cmd.Parameters.AddWithValue(g.Montant);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        // Payé par un prêt ou un apport du titulaire : source tracée (prêt verrouillé, jamais employé au-delà de son montant)
        if (g.Source is { } source)
            await Financements.FinancementsSql.EnregistrerUtilisationAsync(cx, tx, source, "reglement", g.Date, g.Montant, $"Règlement {numero}",
                null, null, id, null, g.UtilisateurId, ct);
        return id;
    }

    public async Task<Pharma.Application.Caisse.ReglementTiroir?> LireAsync(long reglementId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT g.id, COALESCE(g.numero, 'RG-' || g.id), g.fournisseur_id, f.raison_sociale, g.montant, g.part_tiroir, g.moyen, g.statut = 'annule',
                   g.session_caisse_id, COALESCE(s.fermee_le IS NULL, false)
            FROM reglement_fournisseur g JOIN fournisseur f ON f.id = g.fournisseur_id
            LEFT JOIN session_caisse s ON s.id = g.session_caisse_id
            WHERE g.id = $1
            """);
        cmd.Parameters.AddWithValue(reglementId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new Pharma.Application.Caisse.ReglementTiroir(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetDecimal(4), r.GetDecimal(5),
                r.GetString(6), r.GetBoolean(7), r.IsDBNull(8) ? null : r.GetInt64(8), r.GetBoolean(9))
            : null;
    }

    /// <summary>
    /// Annulation d'un règlement payé depuis le tiroir (script 098), une transaction : règlement verrouillé et passé à « annulé » (jamais
    /// supprimé ; ses imputations restent, v_facture_fournisseur ne les compte plus : factures rouvertes), dette du grossiste rétablie
    /// (relevé « annulation_reglement »), entrée de correction au tiroir dans la caisse d'origine (liée au mouvement d'origine).
    /// </summary>
    public async Task<Pharma.Application.Caisse.MouvementTiroirEcrit> AnnulerAsync(Pharma.Application.Caisse.AnnulationReglementAEcrire a, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        long fournisseur, session;
        decimal montant;
        string numero;
        await using (var cmd = new NpgsqlCommand("""
            SELECT g.fournisseur_id, g.montant, COALESCE(g.numero, 'RG-' || g.id), g.session_caisse_id
            FROM reglement_fournisseur g WHERE g.id = $1 AND g.statut = 'valide' AND g.moyen = 'especes' AND g.part_tiroir = g.montant
              AND g.session_caisse_id IS NOT NULL
            FOR UPDATE
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(a.ReglementId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new RegleMetierException("Ce règlement vient d’être annulé ou ne s’annule pas depuis le tiroir.");
            (fournisseur, montant, numero, session) = (r.GetInt64(0), r.GetDecimal(1), r.GetString(2), r.GetInt64(3));
        }
        // Caisse d'origine verrouillée : refus si elle vient d'être clôturée (une clôture en cours attend)
        await using (var cmd = new NpgsqlCommand("SELECT 1 FROM session_caisse WHERE id = $1 AND fermee_le IS NULL FOR UPDATE", cx, tx))
        {
            cmd.Parameters.AddWithValue(session);
            if (await cmd.ExecuteScalarAsync(ct) is null)
                throw new RegleMetierException("La caisse d’origine vient d’être clôturée : le titulaire corrige ce règlement en trésorerie.");
        }
        await using (var cmd = new NpgsqlCommand("""
            UPDATE reglement_fournisseur SET statut = 'annule', annule_le = now(), annule_par = $2, motif_annulation = $3, annulation_valide_par = $4
            WHERE id = $1
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(a.ReglementId);
            cmd.Parameters.AddWithValue(a.UtilisateurId);
            cmd.Parameters.AddWithValue(a.Motif);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)a.ValidePar ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await ReleveGrossiste.ContexteAsync(cx, tx, ReleveGrossiste.AnnulationReglement, a.UtilisateurId, $"Annulation du règlement {numero} : {a.Motif}", ct);
        await using (var cmd = new NpgsqlCommand("UPDATE fournisseur SET solde = solde + $2 WHERE id = $1", cx, tx))
        {
            cmd.Parameters.AddWithValue(fournisseur);
            cmd.Parameters.AddWithValue(montant);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var o = a.Mouvement;
        Pharma.Application.Caisse.MouvementTiroirEcrit ecrit;
        try
        {
            ecrit = await Caisse.TiroirSql.InsererAsync(cx, tx, null, session, a.UtilisateurId, a.Poste, o.MotifCode, o.Motif, Pharma.Domain.Caisse.RegleTiroir.Entree,
                o.Detail, montant, null, null, fournisseur, null, null, null, null, null, null, a.ValidePar, o.Id,
                $"Annulation du règlement {numero} : {a.Motif}", ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new RegleMetierException("Ce paiement vient d’être annulé depuis un autre poste.");
        }
        await tx.CommitAsync(ct);
        return ecrit;
    }

    internal static async Task<long> CoffreAsync(NpgsqlConnection cx, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT id FROM compte_tresorerie WHERE type = 'coffre' ORDER BY id LIMIT 1", cx, tx);
        return await cmd.ExecuteScalarAsync(ct) as long? ?? throw new RegleMetierException("Le coffre de la trésorerie est introuvable.");
    }

    private static NpgsqlParameter Texte(string? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    /// <summary>Moyen de règlement tel qu'il s'écrit dans le relevé du grossiste.</summary>
    private static string MoyenEnClair(string code) => code switch
    {
        "especes" => "Espèces", "cheque" => "Chèque", "virement" => "Virement", "effet" => "Traite", "prelevement" => "Prélèvement",
        "financement" => "Prêt ou apport du titulaire", _ => code
    };
}
