using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Commun;
using Pharma.Application.Achats;
using Pharma.Domain.Achats;
using Pharma.Domain.Commun;
using Pharma.Infrastructure.Tresorerie;

namespace Pharma.Infrastructure.Achats;

/// <summary>Règlements des grossistes (029_reglements.sql).</summary>
public sealed class ReglementsFournisseursSql(NpgsqlDataSource db) : IReglementsFournisseurs
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
            SELECT g.id, g.date_reglement, g.montant, g.moyen, g.reference, g.echeance, g.part_tiroir, u.nom_complet, g.utilisateur_id
            FROM reglement_fournisseur g JOIN utilisateur u ON u.id = g.utilisateur_id
            WHERE g.fournisseur_id = $1 ORDER BY g.date_reglement DESC, g.id DESC LIMIT 20
            """))
        {
            cmd.Parameters.AddWithValue(fournisseurId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                reglements.Add(new ReglementPasse(r.GetInt64(0), DateOnly.FromDateTime(r.GetDateTime(1)), r.GetDecimal(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : DateOnly.FromDateTime(r.GetDateTime(5)), r.GetDecimal(6), r.GetString(7), r.GetInt64(8)));
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
        await tx.CommitAsync(ct);
        return id;
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
