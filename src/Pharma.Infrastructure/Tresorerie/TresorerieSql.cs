using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Tresorerie;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Tresorerie;

/// <summary>Trésorerie (030_tresorerie.sql) : comptes, mouvements saisis, remises de chèques, versements des caisses au coffre.</summary>
public sealed class TresorerieSql(NpgsqlDataSource db) : ITresorerie
{
    public async Task<IReadOnlyList<CompteTresorerie>> ComptesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, type, nom, banque, numero_compte, recoit_carte, actif, solde_initial, date_initiale, solde
            FROM v_solde_tresorerie ORDER BY type DESC, NOT actif, nom
            """);
        var liste = new List<CompteTresorerie>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new CompteTresorerie(r.GetInt64(0), r.GetString(1), r.GetString(2), Txt(r, 3), Txt(r, 4), r.GetBoolean(5), r.GetBoolean(6),
                r.GetDecimal(7), DateOnly.FromDateTime(r.GetDateTime(8)), r.GetDecimal(9)));
        return liste;
    }

    public async Task<IReadOnlyList<MouvementTresorerie>> MouvementsAsync(long compteId, DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT date, sens, montant, nature, categorie, libelle, reference, employe, origine
            FROM v_mouvement_tresorerie WHERE compte_id = $1 AND date BETWEEN $2 AND $3
            ORDER BY date DESC, cree_le DESC LIMIT 500
            """);
        cmd.Parameters.AddWithValue(compteId);
        cmd.Parameters.AddWithValue(du);
        cmd.Parameters.AddWithValue(au);
        var liste = new List<MouvementTresorerie>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new MouvementTresorerie(DateOnly.FromDateTime(r.GetDateTime(0)), r.GetString(1), r.GetDecimal(2), r.GetString(3), Txt(r, 4),
                r.GetString(5), Txt(r, 6), Txt(r, 7), r.GetString(8)));
        return liste;
    }

    public async Task<AVerser> AVerserAsync(CancellationToken ct = default)
    {
        var cheques = new List<ChequeARemettre>();
        await using (var cmd = db.CreateCommand("SELECT paiement_id, cree_le, montant, origine, patient FROM v_cheque_a_remettre ORDER BY cree_le"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                cheques.Add(new ChequeARemettre(r.GetInt64(0), r.GetDateTime(1), r.GetDecimal(2), r.GetString(3), Txt(r, 4)));

        var caisses = new List<CaisseAVerser>();
        await using (var cmd = db.CreateCommand("SELECT session_caisse_id, poste, fermee_le, ouverte_par, compte, propose FROM v_caisse_a_verser ORDER BY fermee_le"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                caisses.Add(new CaisseAVerser(r.GetInt64(0), r.GetString(1), r.GetDateTime(2), r.GetString(3), r.GetDecimal(4), r.GetDecimal(5)));

        var bordereaux = new List<BordereauEnvoye>();
        await using (var cmd = db.CreateCommand("""
            SELECT b.id, o.id, o.code, b.numero, b.montant_total FROM bordereau b JOIN organisme o ON o.id = b.organisme_id
            WHERE b.statut = 'envoye' ORDER BY b.date_envoi NULLS LAST, b.id
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                bordereaux.Add(new BordereauEnvoye(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetDecimal(4)));
        return new AVerser(cheques, caisses, bordereaux);
    }

    public async Task<long> EnregistrerCompteAsync(CompteAEcrire c, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        // Une seule banque reçoit les cartes : celle-ci la remplace
        if (c.RecoitCarte)
            await using (var cmd = new NpgsqlCommand("UPDATE compte_tresorerie SET recoit_carte = false WHERE recoit_carte AND id IS DISTINCT FROM $1", cx, tx))
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)c.Id ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
                await cmd.ExecuteNonQueryAsync(ct);
            }
        long id;
        try
        {
            await using var cmd = new NpgsqlCommand(c.Id is null
                ? """
                  INSERT INTO compte_tresorerie (type, nom, banque, numero_compte, recoit_carte, solde_initial, date_initiale, actif)
                  VALUES ('banque', $2, $3, $4, $5, $6, $7, $8) RETURNING id
                  """
                : """
                  UPDATE compte_tresorerie SET nom = $2, banque = $3, numero_compte = $4, recoit_carte = $5, solde_initial = $6, date_initiale = $7, actif = $8
                  WHERE id = $1 RETURNING id
                  """, cx, tx);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)c.Id ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            cmd.Parameters.AddWithValue(c.Nom);
            cmd.Parameters.Add(Texte(c.Banque));
            cmd.Parameters.Add(Texte(c.NumeroCompte));
            cmd.Parameters.AddWithValue(c.RecoitCarte);
            cmd.Parameters.AddWithValue(c.SoldeInitial);
            cmd.Parameters.AddWithValue(c.DateInitiale);
            cmd.Parameters.AddWithValue(c.Actif);
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new RegleMetierException($"Un compte s’appelle déjà « {c.Nom} ».");
        }
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<long> AjouterMouvementAsync(MouvementAEcrire m, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        long id;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO mouvement_tresorerie (compte_id, date_mouvement, sens, montant, nature, categorie, libelle, reference, organisme_id, bordereau_id, utilisateur_id)
            VALUES ($1, $2, $3, $4, $5, $6,
                    COALESCE($7, 'Versement ' || (SELECT code FROM organisme WHERE id = $9) || COALESCE(' · bordereau ' || (SELECT numero FROM bordereau WHERE id = $10), '')),
                    $8, $9, $10, $11)
            RETURNING id
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(m.CompteId);
            cmd.Parameters.AddWithValue(m.Date);
            cmd.Parameters.AddWithValue(m.Sens);
            cmd.Parameters.AddWithValue(m.Montant);
            cmd.Parameters.AddWithValue(m.Nature);
            cmd.Parameters.Add(Texte(m.Categorie));
            cmd.Parameters.Add(Texte(m.Libelle));
            cmd.Parameters.Add(Texte(m.Reference));
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)m.OrganismeId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)m.BordereauId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            cmd.Parameters.AddWithValue(m.UtilisateurId);
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        if (m.BordereauId is { } b)
            await using (var cmd = new NpgsqlCommand("UPDATE bordereau SET statut = $2 WHERE id = $1 AND statut = 'envoye' AND organisme_id = $3", cx, tx))
            {
                cmd.Parameters.AddWithValue(b);
                cmd.Parameters.AddWithValue(m.StatutBordereau!);
                cmd.Parameters.AddWithValue(m.OrganismeId!.Value);
                if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                    throw new RegleMetierException("Ce bordereau n’est plus en attente de paiement pour cet organisme : rechargez la liste.");
            }
        // Factures clients soldées : le reste est relu sous verrou (deux encaissements simultanés ne paient pas deux fois)
        foreach (var (factureId, part) in m.Factures ?? [])
        {
            await using (var verrou = new NpgsqlCommand("SELECT 1 FROM facture_client WHERE id = $1 FOR UPDATE", cx, tx))
            {
                verrou.Parameters.AddWithValue(factureId);
                await verrou.ExecuteNonQueryAsync(ct);
            }
            await using (var reste = new NpgsqlCommand("SELECT reste FROM v_facture_client WHERE id = $1 AND NOT hors_suivi", cx, tx))
            {
                reste.Parameters.AddWithValue(factureId);
                if (await reste.ExecuteScalarAsync(ct) is not decimal r || r < part)
                    throw new RegleMetierException("Une facture vient d’être réglée entre-temps : rechargez la liste.");
            }
            await using var cmd = new NpgsqlCommand("INSERT INTO reglement_facture_client (mouvement_id, facture_id, montant) VALUES ($1, $2, $3)", cx, tx);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(factureId);
            cmd.Parameters.AddWithValue(part);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<long> TransfererAsync(long de, long vers, DateOnly date, decimal montant, string libelle, string? reference, long utilisateurId,
        CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        long sortie;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO mouvement_tresorerie (compte_id, date_mouvement, sens, montant, nature, libelle, reference, utilisateur_id)
            VALUES ($1, $2, 'sortie', $3, 'transfert', $4, $5, $6) RETURNING id
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(de);
            cmd.Parameters.AddWithValue(date);
            cmd.Parameters.AddWithValue(montant);
            cmd.Parameters.AddWithValue(libelle);
            cmd.Parameters.Add(Texte(reference));
            cmd.Parameters.AddWithValue(utilisateurId);
            sortie = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO mouvement_tresorerie (compte_id, date_mouvement, sens, montant, nature, libelle, reference, transfert_de, utilisateur_id)
            VALUES ($1, $2, 'entree', $3, 'transfert', $4, $5, $6, $7)
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(vers);
            cmd.Parameters.AddWithValue(date);
            cmd.Parameters.AddWithValue(montant);
            cmd.Parameters.AddWithValue(libelle);
            cmd.Parameters.Add(Texte(reference));
            cmd.Parameters.AddWithValue(sortie);
            cmd.Parameters.AddWithValue(utilisateurId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return sortie;
    }

    public async Task<long> RemettreChequesAsync(long compteId, DateOnly date, string? reference, IReadOnlyList<long> paiements, long utilisateurId,
        CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        long id;
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO remise_cheques (compte_id, date_remise, reference, utilisateur_id) VALUES ($1, $2, $3, $4) RETURNING id", cx, tx))
        {
            cmd.Parameters.AddWithValue(compteId);
            cmd.Parameters.AddWithValue(date);
            cmd.Parameters.Add(Texte(reference));
            cmd.Parameters.AddWithValue(utilisateurId);
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO remise_cheque_paiement (remise_id, paiement_id)
            SELECT $1, paiement_id FROM v_cheque_a_remettre WHERE paiement_id = ANY($2)
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(paiements.ToArray());
            try
            {
                if (await cmd.ExecuteNonQueryAsync(ct) != paiements.Count)
                    throw new RegleMetierException("Un des chèques n’est plus à remettre : rechargez la liste.");
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new RegleMetierException("Un des chèques vient d’être remis depuis un autre poste : rechargez la liste.");
            }
        }
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<long> VerserCaisseAsync(long sessionId, long coffreId, decimal montant, DateOnly date, bool pendantSession, bool automatique, long utilisateurId,
        CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        // Session verrouillée : une caisse clôturée n'est versée qu'une fois ; une caisse ouverte doit l'être encore
        await using (var cmd = new NpgsqlCommand("""
            SELECT fermee_le IS NULL,
                   EXISTS (SELECT 1 FROM mouvement_tresorerie m WHERE m.session_caisse_id = s.id AND NOT m.pendant_session)
            FROM session_caisse s WHERE id = $1 FOR UPDATE
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(sessionId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new RegleMetierException("Caisse introuvable.");
            var (ouverte, dejaVersee) = (r.GetBoolean(0), r.GetBoolean(1));
            if (pendantSession && !ouverte) throw new RegleMetierException("La caisse vient d’être clôturée : versez son argent depuis la trésorerie.");
            if (!pendantSession && (ouverte || dejaVersee)) throw new RegleMetierException("Cette caisse est déjà versée au coffre (ou pas encore clôturée).");
        }
        long id;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO mouvement_tresorerie (compte_id, date_mouvement, sens, montant, nature, libelle, session_caisse_id, pendant_session, automatique, utilisateur_id)
            SELECT $1, $2, 'entree', $3, 'versement_caisse',
                   CASE WHEN $5 THEN 'Argent de la caisse porté au coffre (' ELSE 'Versement de la caisse clôturée (' END || s.poste || ')'
                   || CASE WHEN $6 THEN ' · automatique' ELSE '' END,
                   s.id, $5, $6, $7
            FROM session_caisse s WHERE s.id = $4 RETURNING id
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(coffreId);
            cmd.Parameters.AddWithValue(date);
            cmd.Parameters.AddWithValue(montant);
            cmd.Parameters.AddWithValue(sessionId);
            cmd.Parameters.AddWithValue(pendantSession);
            cmd.Parameters.AddWithValue(automatique);
            cmd.Parameters.AddWithValue(utilisateurId);
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        await tx.CommitAsync(ct);
        return id;
    }

    private static string? Txt(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static NpgsqlParameter Texte(string? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };
}
