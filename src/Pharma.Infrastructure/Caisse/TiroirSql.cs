using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Domain.Caisse;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Caisse;

/// <summary>
/// Sorties et entrées d'argent par le tiroir (database/migration/096_sorties_tiroir.sql) : événement d'ouverture du tiroir, mouvement
/// du tiroir et écriture (règlement du grossiste, mouvement du coffre) dans une seule transaction ; session verrouillée et espèces
/// relues (deux sorties simultanées ne vident pas deux fois le tiroir, une clôture en cours attend).
/// </summary>
public sealed class TiroirSql(NpgsqlDataSource db) : ITiroir
{
    private static NpgsqlParameter V(object? v, NpgsqlDbType type) => new() { Value = v ?? DBNull.Value, NpgsqlDbType = type };

    public async Task<MouvementTiroirEcrit> EnregistrerAsync(MouvementTiroirAEcrire m, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        if (m.Sens == RegleTiroir.Sortie) await ExigerDansTiroirAsync(cx, tx, m.SessionId, m.Montant, ct);
        else await VerrouillerSessionAsync(cx, tx, m.SessionId, ct);

        long evenement;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO evenement_caisse (type, utilisateur_id, poste, session_caisse_id, montant, detail)
            VALUES ('tiroir_ouvert', $1, $2, $3, $4, $5) RETURNING id
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(m.UtilisateurId);
            cmd.Parameters.AddWithValue(m.Poste);
            cmd.Parameters.AddWithValue(m.SessionId);
            cmd.Parameters.AddWithValue(m.Montant);
            cmd.Parameters.AddWithValue(m.MotifLibelle);
            evenement = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        long? reglement = m.Reglement is { } g ? await Achats.ReglementsFournisseursSql.EcrireAsync(cx, tx, g, ct) : null;
        long? tresorerie = m.Detail switch
        {
            RegleTiroir.Coffre => await CoffreAsync(cx, tx, m.SessionId, "entree", m.Montant, $"Retrait du tiroir vers le coffre ({m.Poste})", m.UtilisateurId, ct),
            RegleTiroir.Fond => await CoffreAsync(cx, tx, m.SessionId, "sortie", m.Montant, $"Fonds de caisse remis au tiroir ({m.Poste})", m.UtilisateurId, ct),
            _ => null
        };

        var s = m.Saisie;
        long? regularisation = s.RegularisationVenteId is { } venteRegularisee ? await RegularisationNonRendueAsync(cx, tx, venteRegularisee, ct) : null;
        var ecrit = await InsererAsync(cx, tx, evenement, m.SessionId, m.UtilisateurId, m.Poste, m.MotifCode, m.MotifLibelle, m.Sens, m.Detail, m.Montant,
            s.Categorie, s.Libelle, s.FournisseurId, s.Beneficiaire, s.Reference, s.PatientId, s.Raison, reglement, tresorerie, m.ValidePar, null, null, ct,
            regularisation);
        await tx.CommitAsync(ct);
        return ecrit;
    }

    /// <summary>Bon actif (non corrigé) qui a rendu le trop-payé d'une régularisation (script 099).</summary>
    private const string BonRendu = """
        (SELECT t.numero FROM mouvement_tiroir t
          WHERE t.regularisation_id = g.id AND t.corrige_id IS NULL
            AND NOT EXISTS (SELECT 1 FROM mouvement_tiroir c WHERE c.corrige_id = t.id)
          ORDER BY t.id DESC LIMIT 1)
        """;

    public async Task<RegularisationARendre?> RegularisationARendreAsync(long venteId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT g.id, v.id, v.numero, v.patient_id, trim(concat_ws(' ', p.nom, p.prenom)), g.montant_a_rendre,
                   g.statut = 'active' AND g.sort = 'avoir_patient' AND v.statut <> 'annulee', {BonRendu}
            FROM regularisation_part_organisme g
            JOIN vente v ON v.id = g.vente_id
            LEFT JOIN patient p ON p.id = v.patient_id
            WHERE g.vente_id = $1
            """);
        cmd.Parameters.AddWithValue(venteId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new RegularisationARendre(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt64(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.GetDecimal(5), r.GetBoolean(6), r.IsDBNull(7) ? null : r.GetString(7));
    }

    /// <summary>
    /// Régularisation verrouillée et relue dans la transaction de la sortie : deux postes ne rendent pas deux fois le même trop-payé
    /// (vérifié avant par le cas d'usage, revérifié ici sous verrou).
    /// </summary>
    private static async Task<long> RegularisationNonRendueAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long venteId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"""
            SELECT g.id, {BonRendu} FROM regularisation_part_organisme g WHERE g.vente_id = $1 FOR UPDATE OF g
            """, cx, tx);
        cmd.Parameters.AddWithValue(venteId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new RegleMetierException("Aucune régularisation Chifa sur cette vente.", "regularisation");
        if (!r.IsDBNull(1)) throw new RegleMetierException($"Ce trop-payé vient d’être rendu (bon {r.GetString(1)}).", "regularisation");
        return r.GetInt64(0);
    }

    public async Task<MouvementTiroirEcrit> CorrigerAsync(MouvementTiroir o, long sessionId, string poste, string motif, long utilisateurId,
        CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        // Mouvement verrouillé : deux corrections simultanées ne passent pas (index unique sur corrige_id en dernier recours)
        await using (var cmd = new NpgsqlCommand(
            "SELECT 1 FROM mouvement_tiroir WHERE id = $1 AND NOT EXISTS (SELECT 1 FROM mouvement_tiroir c WHERE c.corrige_id = $1) FOR UPDATE", cx, tx))
        {
            cmd.Parameters.AddWithValue(o.Id);
            if (await cmd.ExecuteScalarAsync(ct) is null) throw new RegleMetierException("Ce mouvement est déjà corrigé.");
        }
        var sens = o.Sens == RegleTiroir.Sortie ? RegleTiroir.Entree : RegleTiroir.Sortie;
        if (sens == RegleTiroir.Sortie) await ExigerDansTiroirAsync(cx, tx, sessionId, o.Montant, ct);
        else await VerrouillerSessionAsync(cx, tx, sessionId, ct);
        long? tresorerie = o.Detail switch
        {
            // Inverse d'un retrait vers le coffre : l'argent revient du coffre ; inverse d'une remise de fonds : il y retourne
            RegleTiroir.Coffre => await CoffreAsync(cx, tx, sessionId, "sortie", o.Montant, $"Correction du bon {o.Numero} : retour du coffre au tiroir ({poste})", utilisateurId, ct),
            RegleTiroir.Fond => await CoffreAsync(cx, tx, sessionId, "entree", o.Montant, $"Correction du bon {o.Numero} : fonds rendu au coffre ({poste})", utilisateurId, ct),
            _ => null
        };
        MouvementTiroirEcrit ecrit;
        try
        {
            ecrit = await InsererAsync(cx, tx, null, sessionId, utilisateurId, poste, o.MotifCode, o.Motif, sens, o.Detail, o.Montant,
                null, null, null, null, null, null, null, null, tresorerie, null, o.Id, motif, ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new RegleMetierException("Ce mouvement vient d’être corrigé depuis un autre poste.");
        }
        await tx.CommitAsync(ct);
        return ecrit;
    }

    internal static async Task<MouvementTiroirEcrit> InsererAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long? evenement, long sessionId, long utilisateurId,
        string poste, string motifCode, string motifLibelle, string sens, string detail, decimal montant, string? categorie, string? libelle, long? fournisseurId,
        string? beneficiaire, string? reference, long? patientId, string? raison, long? reglement, long? tresorerie, long? validePar, long? corrige,
        string? motifCorrection, CancellationToken ct, long? regularisation = null)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO mouvement_tiroir (numero, evenement_caisse_id, session_caisse_id, utilisateur_id, poste, motif_code, motif_libelle, sens, detail,
                montant, categorie, libelle, fournisseur_id, beneficiaire, reference, patient_id, raison, reglement_fournisseur_id, mouvement_tresorerie_id,
                valide_par, corrige_id, motif_correction, regularisation_id)
            VALUES ('BS-' || lpad(nextval('seq_numero_bon_sortie')::text, 6, '0'), $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16,
                $17, $18, $19, $20, $21, $22)
            RETURNING id, numero
            """, cx, tx);
        cmd.Parameters.Add(V(evenement, NpgsqlDbType.Bigint));
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(utilisateurId);
        cmd.Parameters.AddWithValue(poste);
        cmd.Parameters.AddWithValue(motifCode);
        cmd.Parameters.AddWithValue(motifLibelle);
        cmd.Parameters.AddWithValue(sens);
        cmd.Parameters.AddWithValue(detail);
        cmd.Parameters.AddWithValue(montant);
        cmd.Parameters.Add(V(categorie, NpgsqlDbType.Text));
        cmd.Parameters.Add(V(libelle, NpgsqlDbType.Text));
        cmd.Parameters.Add(V(fournisseurId, NpgsqlDbType.Bigint));
        cmd.Parameters.Add(V(beneficiaire, NpgsqlDbType.Text));
        cmd.Parameters.Add(V(reference, NpgsqlDbType.Text));
        cmd.Parameters.Add(V(patientId, NpgsqlDbType.Bigint));
        cmd.Parameters.Add(V(raison, NpgsqlDbType.Text));
        cmd.Parameters.Add(V(reglement, NpgsqlDbType.Bigint));
        cmd.Parameters.Add(V(tresorerie, NpgsqlDbType.Bigint));
        cmd.Parameters.Add(V(validePar, NpgsqlDbType.Bigint));
        cmd.Parameters.Add(V(corrige, NpgsqlDbType.Bigint));
        cmd.Parameters.Add(V(motifCorrection, NpgsqlDbType.Text));
        cmd.Parameters.Add(V(regularisation, NpgsqlDbType.Bigint));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new MouvementTiroirEcrit(r.GetInt64(0), r.GetString(1));
    }

    /// <summary>Session ouverte verrouillée (une clôture en cours attend, puis relit l'attendu) ; refus si elle vient d'être clôturée.</summary>
    private static async Task<decimal> VerrouillerSessionAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long sessionId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT fond_caisse FROM session_caisse WHERE id = $1 AND fermee_le IS NULL FOR UPDATE", cx, tx);
        cmd.Parameters.AddWithValue(sessionId);
        return await cmd.ExecuteScalarAsync(ct) as decimal?
               ?? throw new RegleMetierException("Cette caisse vient d’être clôturée : ouvrez la caisse du poste et recommencez.");
    }

    /// <summary>Sortie : au plus les espèces attendues dans le tiroir, relues sous verrou de la session.</summary>
    internal static async Task ExigerDansTiroirAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long sessionId, decimal montant, CancellationToken ct)
    {
        var fond = await VerrouillerSessionAsync(cx, tx, sessionId, ct);
        await using var cmd = new NpgsqlCommand(SessionsCaisseSql.SqlVentesEspeces, cx, tx);
        cmd.Parameters.AddWithValue(sessionId);
        RegleTiroir.VerifierDansTiroir(montant, fond + (decimal)(await cmd.ExecuteScalarAsync(ct))!);
    }

    /// <summary>
    /// Échange avec le coffre pendant la session : entrée au coffre (versement de la caisse, l'attendu baisse) ou sortie du coffre
    /// (transfert vers le tiroir, l'attendu monte ; solde du coffre relu sous verrou).
    /// </summary>
    private static async Task<long> CoffreAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long sessionId, string sens, decimal montant, string libelle,
        long utilisateurId, CancellationToken ct)
    {
        var coffre = await Achats.ReglementsFournisseursSql.CoffreAsync(cx, tx, ct);
        if (sens == "sortie") await Tresorerie.SoldeTresorerie.ExigerAsync(cx, tx, coffre, montant, ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO mouvement_tresorerie (compte_id, date_mouvement, sens, montant, nature, libelle, session_caisse_id, pendant_session, utilisateur_id)
            VALUES ($1, current_date, $2, $3, CASE WHEN $2 = 'entree' THEN 'versement_caisse' ELSE 'transfert' END, $4, $5, true, $6)
            RETURNING id
            """, cx, tx);
        cmd.Parameters.AddWithValue(coffre);
        cmd.Parameters.AddWithValue(sens);
        cmd.Parameters.AddWithValue(montant);
        cmd.Parameters.AddWithValue(libelle);
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(utilisateurId);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private const string Colonnes = """
        SELECT m.id, m.numero, m.cree_le, m.utilisateur_id, u.nom_complet, m.poste, m.session_caisse_id, m.motif_code, m.motif_libelle, m.sens, m.detail,
               m.montant, COALESCE(c.libelle, m.categorie), m.libelle, COALESCE(f.raison_sociale, m.beneficiaire), m.reference, NULLIF(btrim(concat_ws(' ', p.nom, p.prenom)), ''), m.raison, v.nom_complet,
               m.corrige_id, m.motif_correction, EXISTS (SELECT 1 FROM mouvement_tiroir x WHERE x.corrige_id = m.id), m.reglement_fournisseur_id
        FROM mouvement_tiroir m
        JOIN utilisateur u ON u.id = m.utilisateur_id
        LEFT JOIN utilisateur v ON v.id = m.valide_par
        LEFT JOIN fournisseur f ON f.id = m.fournisseur_id
        LEFT JOIN patient p ON p.id = m.patient_id
        LEFT JOIN motif_reglable c ON c.liste = 'charge' AND c.code = m.categorie
        """;

    private static MouvementTiroir Lire(NpgsqlDataReader r)
    {
        string? T(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new MouvementTiroir(r.GetInt64(0), r.GetString(1), r.GetDateTime(2), r.GetInt64(3), r.GetString(4), T(5), r.GetInt64(6), r.GetString(7),
            r.GetString(8), r.GetString(9), r.GetString(10), r.GetDecimal(11), T(12), T(13), T(14), T(15), T(16), T(17), T(18),
            r.IsDBNull(19) ? null : r.GetInt64(19), T(20), r.GetBoolean(21), r.IsDBNull(22) ? null : r.GetInt64(22));
    }

    public async Task<MouvementTiroir?> LireAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(Colonnes + " WHERE m.id = $1");
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Lire(r) : null;
    }

    public async Task<PageMouvementsTiroir> ListerAsync(FiltreMouvementsTiroir f, DemandePage page, CancellationToken ct = default)
    {
        const string filtre = """
             WHERE ($1::date IS NULL OR m.cree_le >= $1::date) AND ($2::date IS NULL OR m.cree_le < $2::date + 1)
               AND ($3::bigint IS NULL OR m.utilisateur_id = $3) AND ($4::text IS NULL OR m.motif_code = $4) AND ($5::text IS NULL OR m.sens = $5)
            """;
        void Parametres(NpgsqlCommand c)
        {
            c.Parameters.Add(V(f.Du, NpgsqlDbType.Date));
            c.Parameters.Add(V(f.Au, NpgsqlDbType.Date));
            c.Parameters.Add(V(f.EmployeId, NpgsqlDbType.Bigint));
            c.Parameters.Add(V(f.Motif, NpgsqlDbType.Text));
            c.Parameters.Add(V(f.Sens, NpgsqlDbType.Text));
        }
        int total;
        decimal sorties, entrees;
        await using (var cmd = db.CreateCommand($"""
            SELECT count(*)::int,
                   COALESCE(sum(CASE WHEN m.corrige_id IS NULL AND m.sens = 'sortie' THEN m.montant
                                     WHEN m.corrige_id IS NOT NULL AND m.sens = 'entree' THEN -m.montant ELSE 0 END), 0),
                   COALESCE(sum(CASE WHEN m.corrige_id IS NULL AND m.sens = 'entree' THEN m.montant
                                     WHEN m.corrige_id IS NOT NULL AND m.sens = 'sortie' THEN -m.montant ELSE 0 END), 0)
            FROM mouvement_tiroir m {filtre}
            """))
        {
            Parametres(cmd);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            (total, sorties, entrees) = (r.GetInt32(0), r.GetDecimal(1), r.GetDecimal(2));
        }
        var lignes = new List<MouvementTiroir>();
        await using (var cmd = db.CreateCommand($"{Colonnes} {filtre} ORDER BY m.id DESC LIMIT $6 OFFSET $7"))
        {
            Parametres(cmd);
            cmd.Parameters.AddWithValue(page.Taille);
            cmd.Parameters.AddWithValue(page.Decalage);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) lignes.Add(Lire(r));
        }
        // Totaux nets : une écriture inverse (sens opposé) annule le mouvement corrigé
        return new PageMouvementsTiroir(new Page<MouvementTiroir>(lignes, total, page.Page, page.Taille), sorties, entrees);
    }
}
