using Npgsql;
using Pharma.Application.Caisse;
using Pharma.Domain.Caisse;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Caisse;

public sealed class SessionsCaisseSql(NpgsqlDataSource db) : ISessionsCaisse
{
    /// <summary>Caisse ouverte du poste (une au plus : index ux_session_poste_ouverte).</summary>
    public async Task<SessionCaisse?> SessionDuPosteAsync(string poste, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT s.id, s.utilisateur_id, s.poste, s.ouverte_le, s.fond_caisse, u.nom_complet
            FROM session_caisse s JOIN utilisateur u ON u.id = s.utilisateur_id
            WHERE s.poste = $1 AND s.fermee_le IS NULL
            """);
        cmd.Parameters.AddWithValue(poste);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new SessionCaisse
        {
            Id = r.GetInt64(0), UtilisateurId = r.GetInt64(1), Poste = r.GetString(2),
            OuverteLe = r.GetDateTime(3), FondCaisse = r.GetDecimal(4), OuvertePar = r.GetString(5)
        };
    }

    public async Task<long> OuvrirAsync(long utilisateurId, string poste, decimal fondCaisse, long? releveDe = null, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "INSERT INTO session_caisse (utilisateur_id, poste, fond_caisse, releve_de) VALUES ($1, $2, $3, $4) RETURNING id");
        cmd.Parameters.AddWithValue(utilisateurId);
        cmd.Parameters.AddWithValue(poste);
        cmd.Parameters.AddWithValue(fondCaisse);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)releveDe ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
        try
        {
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new RegleMetierException("La caisse de ce poste est déjà ouverte : vendez dessus, ou faites une relève.");
        }
    }

    public async Task<IReadOnlyList<Vendeur>> VendeursAsync(long sessionId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT DISTINCT u.id, u.nom_complet FROM vente v JOIN utilisateur u ON u.id = v.utilisateur_id
            WHERE v.session_caisse_id = $1 ORDER BY u.nom_complet
            """);
        cmd.Parameters.AddWithValue(sessionId);
        var liste = new List<Vendeur>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(new Vendeur(r.GetInt64(0), r.GetString(1)));
        return liste;
    }

    public async Task<decimal> VentesEspecesAsync(long sessionId, CancellationToken ct = default)
    {
        // Espèces encaissées, moins l'argent rendu depuis cette caisse : vente annulée (027), retour client (028), complément pris pour payer un grossiste (029), argent porté au coffre pendant la journée (030) ; les remboursements de crédit (paiements sans vente) y sont comptés
        await using var cmd = db.CreateCommand("""
            SELECT COALESCE((SELECT SUM(montant) FROM paiement WHERE session_caisse_id = $1 AND mode = 'especes'), 0)
                 - COALESCE((SELECT SUM(montant_rembourse) FROM annulation_vente WHERE session_caisse_id = $1 AND mode = 'especes'), 0)
                 - COALESCE((SELECT SUM(montant_rembourse) FROM retour_client WHERE session_caisse_id = $1 AND mode = 'especes'), 0)
                 - COALESCE((SELECT SUM(part_tiroir) FROM reglement_fournisseur WHERE session_caisse_id = $1), 0)
                 - COALESCE((SELECT SUM(montant) FROM mouvement_tresorerie WHERE session_caisse_id = $1 AND pendant_session), 0)
            """);
        cmd.Parameters.AddWithValue(sessionId);
        return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task CloturerAsync(long sessionId, Comptage comptage, ResultatCloture resultat, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);

        foreach (var l in comptage.Lignes)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO comptage_caisse (session_id, coupure, nombre, montant) VALUES ($1, $2, $3, $4)", cx, tx);
            cmd.Parameters.AddWithValue(sessionId);
            cmd.Parameters.AddWithValue(l.Coupure);
            cmd.Parameters.AddWithValue(l.Nombre);
            cmd.Parameters.AddWithValue(l.Montant);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = new NpgsqlCommand("""
            UPDATE session_caisse
            SET fermee_le = now(), ventes_especes = $2, montant_attendu = $3, montant_compte = $4, ecart = $5
            WHERE id = $1 AND fermee_le IS NULL
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(sessionId);
            cmd.Parameters.AddWithValue(resultat.VentesEspeces);
            cmd.Parameters.AddWithValue(resultat.Attendu);
            cmd.Parameters.AddWithValue(resultat.Compte);
            cmd.Parameters.AddWithValue(resultat.Ecart);
            if (await cmd.ExecuteNonQueryAsync(ct) != 1)
                throw new RegleMetierException("Cette session de caisse est déjà clôturée.");
        }
        await tx.CommitAsync(ct);
    }

    public async Task<DetailSession?> DetailAsync(long sessionId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT s.id, s.utilisateur_id, u.nom_complet, s.poste, s.ouverte_le, s.fond_caisse, s.fermee_le,
                   s.ventes_especes, s.montant_attendu, s.montant_compte, s.ecart,
                   (SELECT count(*) FROM vente v WHERE v.session_caisse_id = s.id AND v.statut = 'validee')::int,
                   (SELECT COALESCE(SUM(v.montant_total), 0) FROM vente v WHERE v.session_caisse_id = s.id AND v.statut = 'validee')
            FROM session_caisse s JOIN utilisateur u ON u.id = s.utilisateur_id
            WHERE s.id = $1
            """);
        cmd.Parameters.AddWithValue(sessionId);
        DetailSession detail;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            decimal? Dec(int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);
            detail = new DetailSession(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetDateTime(4),
                r.GetDecimal(5), r.IsDBNull(6) ? null : r.GetDateTime(6), Dec(7), Dec(8), Dec(9), Dec(10),
                r.GetInt32(11), r.GetDecimal(12), []);
        }

        await using var cmdComptage = db.CreateCommand(
            "SELECT coupure, nombre, montant FROM comptage_caisse WHERE session_id = $1");
        cmdComptage.Parameters.AddWithValue(sessionId);
        await using var rc = await cmdComptage.ExecuteReaderAsync(ct);
        var lignes = new List<LigneComptage>();
        while (await rc.ReadAsync(ct)) lignes.Add(new LigneComptage(rc.GetString(0), rc.GetInt32(1), rc.GetDecimal(2)));
        var ordre = Coupures.Toutes.Select(c => c.Code).ToList();
        return detail with { Comptage = lignes.OrderBy(l => ordre.IndexOf(l.Coupure)).ToList() };
    }

    public async Task<ParametresCaisse> ParametresAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT comptage_aveugle, fond_caisse_defaut, seuil_ecart, deconnexion_minutes, versement_cloture FROM parametre_caisse WHERE id = 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new ParametresCaisse(r.GetBoolean(0), r.GetDecimal(1), r.GetDecimal(2), r.GetInt32(3), r.GetString(4))
            : new ParametresCaisse(true, 0, 0, 0);
    }

    public async Task ModifierParametresAsync(ParametresCaisse p, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE parametre_caisse
            SET comptage_aveugle = $1, fond_caisse_defaut = $2, seuil_ecart = $3, deconnexion_minutes = $4, versement_cloture = $5
            WHERE id = 1
            """);
        cmd.Parameters.AddWithValue(p.ComptageAveugle);
        cmd.Parameters.AddWithValue(p.FondCaisseDefaut);
        cmd.Parameters.AddWithValue(p.SeuilEcart);
        cmd.Parameters.AddWithValue(p.DeconnexionMinutes);
        cmd.Parameters.AddWithValue(p.VersementCloture);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
