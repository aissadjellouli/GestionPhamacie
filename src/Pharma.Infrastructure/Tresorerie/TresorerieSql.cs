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
        await using (var cmd = db.CreateCommand("""
            SELECT c.paiement_id, c.cree_le, c.montant, c.origine, c.patient, COALESCE(v.patient_id, p.patient_id)
            FROM v_cheque_a_remettre c JOIN paiement p ON p.id = c.paiement_id LEFT JOIN vente v ON v.id = p.vente_id
            ORDER BY c.cree_le
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                cheques.Add(new ChequeARemettre(r.GetInt64(0), r.GetDateTime(1), r.GetDecimal(2), r.GetString(3), Txt(r, 4), r.IsDBNull(5) ? null : r.GetInt64(5)));

        var caisses = new List<CaisseAVerser>();
        await using (var cmd = db.CreateCommand("SELECT session_caisse_id, poste, fermee_le, ouverte_par, compte, propose FROM v_caisse_a_verser ORDER BY fermee_le"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                caisses.Add(new CaisseAVerser(r.GetInt64(0), r.GetString(1), r.GetDateTime(2), r.GetString(3), r.GetDecimal(4), r.GetDecimal(5)));

        var bordereaux = new List<BordereauEnvoye>();
        await using (var cmd = db.CreateCommand("""
            SELECT b.id, b.organisme_id, b.organisme, b.numero, b.reste, b.attendu, b.verse, b.rejete, b.date_depot, cp.code
            FROM v_bordereau b LEFT JOIN centre_payeur cp ON cp.id = b.centre_payeur_id
            WHERE b.statut IN ('depose', 'paye_partiel') AND b.reste > 0 ORDER BY b.date_depot NULLS LAST, b.id
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                bordereaux.Add(new BordereauEnvoye(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetDecimal(4),
                    r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7), r.IsDBNull(8) ? null : DateOnly.FromDateTime(r.GetDateTime(8)),
                    r.IsDBNull(9) ? null : r.GetString(9)));
        return new AVerser(cheques, caisses, bordereaux);
    }

    public async Task<long> EnregistrerCompteAsync(CompteAEcrire c, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        // Doublon selon les majuscules et les espaces autour (« BNA » et « bna ») : refusé avec un message clair
        // (l'index unique sur lower(btrim(nom)) du script 077 couvre deux postes simultanés)
        await using (var doublon = new NpgsqlCommand(
            "SELECT nom FROM compte_tresorerie WHERE lower(btrim(nom)) = lower(btrim($1)) AND id IS DISTINCT FROM $2 LIMIT 1", cx, tx))
        {
            doublon.Parameters.AddWithValue(c.Nom);
            doublon.Parameters.Add(new NpgsqlParameter { Value = (object?)c.Id ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            if (await doublon.ExecuteScalarAsync(ct) is string existant)
                throw new RegleMetierException($"Un compte s’appelle déjà « {existant} » (majuscules et espaces ne comptent pas).", "nom_compte");
        }
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
        // Dépense : solde relu sous verrou (deux dépenses simultanées ne passent pas toutes deux sur un solde qui ne couvre qu'une)
        if (m.Sens == "sortie") await SoldeTresorerie.ExigerAsync(cx, tx, m.CompteId, m.Montant, ct);
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
        // Bordereaux Chifa / MDN payés par ce versement (script 110) : reste relu sous verrou, imputation, statut recalculé
        foreach (var (bordereauId, part) in m.Bordereaux ?? [])
        {
            await using (var verrou = new NpgsqlCommand(
                "SELECT 1 FROM bordereau WHERE id = $1 AND organisme_id = $2 AND statut IN ('depose', 'paye_partiel') FOR UPDATE", cx, tx))
            {
                verrou.Parameters.AddWithValue(bordereauId);
                verrou.Parameters.AddWithValue(m.OrganismeId!.Value);
                if (await verrou.ExecuteScalarAsync(ct) is null)
                    throw new RegleMetierException("Un bordereau n’est plus en attente de paiement pour cet organisme : rechargez la liste.");
            }
            await using (var reste = new NpgsqlCommand("SELECT reste FROM v_bordereau WHERE id = $1", cx, tx))
            {
                reste.Parameters.AddWithValue(bordereauId);
                if (await reste.ExecuteScalarAsync(ct) is not decimal r || r < part)
                    throw new RegleMetierException("Un bordereau vient d’être payé entre-temps : rechargez la liste.");
            }
            await using (var cmd = new NpgsqlCommand("INSERT INTO versement_bordereau (mouvement_id, bordereau_id, montant) VALUES ($1, $2, $3)", cx, tx))
            {
                cmd.Parameters.AddWithValue(id);
                cmd.Parameters.AddWithValue(bordereauId);
                cmd.Parameters.AddWithValue(part);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await Chifa.BordereauxSql.RecalculerStatutAsync(cx, tx, bordereauId, ct);
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
            var partCredit = await SolderDettePatientsAsync(cx, tx, factureId, part, m.UtilisateurId, ct);
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO reglement_facture_client (mouvement_id, facture_id, montant, part_credit) VALUES ($1, $2, $3, $4)", cx, tx);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(factureId);
            cmd.Parameters.AddWithValue(part);
            cmd.Parameters.AddWithValue(partCredit);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return id;
    }

    /// <summary>
    /// Facture client encaissée alors qu'elle couvre des ventes à crédit : la dette des patients concernés diminue du reste encore dû
    /// de ces ventes (v_vente_credit), au plus le montant encaissé sur la facture, ventes les plus anciennes d'abord. Inscrit au relevé
    /// de dette du patient (remboursement, référence de la facture). Renvoie la part de l'encaissement qui a soldé de la dette.
    /// </summary>
    private static async Task<decimal> SolderDettePatientsAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long factureId, decimal encaisse,
        long utilisateurId, CancellationToken ct)
    {
        // Patients verrouillés : un remboursement au comptoir en même temps ne solde pas deux fois la même dette
        await using (var verrou = new NpgsqlCommand("""
            SELECT 1 FROM patient WHERE id IN (
                SELECT v.patient_id FROM facture_client_ligne fl JOIN vente v ON v.id = fl.vente_id
                WHERE fl.facture_id = $1 AND fl.part = 'patient' AND v.patient_id IS NOT NULL)
            ORDER BY id FOR UPDATE
            """, cx, tx))
        {
            verrou.Parameters.AddWithValue(factureId);
            await verrou.ExecuteNonQueryAsync(ct);
        }
        var restes = new List<(long Patient, long Vente, decimal Reste)>();
        string numero;
        await using (var cmd = new NpgsqlCommand("""
            SELECT c.patient_id, c.reste, (SELECT numero FROM facture_client WHERE id = $1), c.vente_id
            FROM v_vente_credit c
            WHERE c.reste > 0 AND c.vente_id IN (SELECT fl.vente_id FROM facture_client_ligne fl
                                                 WHERE fl.facture_id = $1 AND fl.part = 'patient' AND fl.vente_id IS NOT NULL)
            ORDER BY c.date_vente, c.vente_id
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(factureId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            numero = "";
            while (await r.ReadAsync(ct))
            {
                restes.Add((r.GetInt64(0), r.GetInt64(3), r.GetDecimal(1)));
                numero = r.GetString(2);
            }
        }
        var parPatient = new Dictionary<long, decimal>();
        var parVente = new List<(long Patient, long Vente, decimal Part)>();
        var disponible = encaisse;
        foreach (var (patient, vente, reste) in restes)
        {
            if (disponible <= 0) break;
            var part = Math.Min(reste, disponible);
            parPatient[patient] = parPatient.GetValueOrDefault(patient) + part;
            parVente.Add((patient, vente, part));
            disponible -= part;
        }
        foreach (var (patient, montant) in parPatient.OrderBy(p => p.Key))
        {
            await Caisse.ReleveCredit.ContexteAsync(cx, tx, Caisse.ReleveCredit.Remboursement, null, utilisateurId, null,
                $"Facture {numero} encaissée (trésorerie)", ct);
            await using var cmd = new NpgsqlCommand("UPDATE patient SET solde_credit = solde_credit - $2 WHERE id = $1 AND solde_credit >= $2", cx, tx);
            cmd.Parameters.AddWithValue(patient);
            cmd.Parameters.AddWithValue(montant);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                throw new RegleMetierException("La dette d’un patient de cette facture vient de changer : rechargez la liste.");
            // 087 : le règlement solde les ventes de CETTE facture, jamais une dette plus ancienne du patient
            foreach (var (_, vente, part) in parVente.Where(v => v.Patient == patient && v.Part > 0))
                await Caisse.ReleveCredit.ImputerAsync(cx, tx, vente, part, ct);
        }
        return encaisse - disponible;
    }

    public async Task<IReadOnlyList<(string Facture, decimal Montant)>> DettesSoldeesAsync(long mouvementId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT f.numero, r.part_credit FROM reglement_facture_client r JOIN facture_client f ON f.id = r.facture_id
            WHERE r.mouvement_id = $1 AND r.part_credit > 0 ORDER BY f.numero
            """);
        cmd.Parameters.AddWithValue(mouvementId);
        var liste = new List<(string, decimal)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add((r.GetString(0), r.GetDecimal(1)));
        return liste;
    }

    public async Task<long> TransfererAsync(long de, long vers, DateOnly date, decimal montant, string libelle, string? reference, long utilisateurId,
        CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        // Compte de départ verrouillé, solde relu : deux transferts simultanés ne le vident pas deux fois
        await SoldeTresorerie.ExigerAsync(cx, tx, de, montant, ct);
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
