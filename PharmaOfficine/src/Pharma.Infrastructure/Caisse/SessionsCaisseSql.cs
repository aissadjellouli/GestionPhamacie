using Npgsql;
using Pharma.Application.Caisse;
using Pharma.Domain.Caisse;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Caisse;

public sealed class SessionsCaisseSql(NpgsqlDataSource db) : ISessionsCaisse
{
    public async Task<SessionCaisse?> SessionOuverteAsync(long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, utilisateur_id, poste, ouverte_le, fond_caisse
            FROM session_caisse WHERE utilisateur_id = $1 AND fermee_le IS NULL
            """);
        cmd.Parameters.AddWithValue(utilisateurId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new SessionCaisse
        {
            Id = r.GetInt64(0), UtilisateurId = r.GetInt64(1), Poste = r.GetString(2),
            OuverteLe = r.GetDateTime(3), FondCaisse = r.GetDecimal(4)
        };
    }

    public async Task<long> OuvrirAsync(long utilisateurId, string poste, decimal fondCaisse, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "INSERT INTO session_caisse (utilisateur_id, poste, fond_caisse) VALUES ($1, $2, $3) RETURNING id");
        cmd.Parameters.AddWithValue(utilisateurId);
        cmd.Parameters.AddWithValue(poste);
        cmd.Parameters.AddWithValue(fondCaisse);
        try
        {
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new RegleMetierException("Votre caisse est déjà ouverte (sur ce poste ou un autre).");
        }
    }

    public async Task<decimal> VentesEspecesAsync(long sessionId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT COALESCE(SUM(montant), 0) FROM paiement WHERE session_caisse_id = $1 AND mode = 'especes'");
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
            "SELECT comptage_aveugle, fond_caisse_defaut, seuil_ecart, deconnexion_minutes FROM parametre_caisse WHERE id = 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new ParametresCaisse(r.GetBoolean(0), r.GetDecimal(1), r.GetDecimal(2), r.GetInt32(3))
            : new ParametresCaisse(true, 0, 0, 0);
    }

    public async Task ModifierParametresAsync(ParametresCaisse p, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE parametre_caisse
            SET comptage_aveugle = $1, fond_caisse_defaut = $2, seuil_ecart = $3, deconnexion_minutes = $4
            WHERE id = 1
            """);
        cmd.Parameters.AddWithValue(p.ComptageAveugle);
        cmd.Parameters.AddWithValue(p.FondCaisseDefaut);
        cmd.Parameters.AddWithValue(p.SeuilEcart);
        cmd.Parameters.AddWithValue(p.DeconnexionMinutes);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
