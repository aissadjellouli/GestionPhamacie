using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Surveillance;

namespace Pharma.Infrastructure.Surveillance;

/// <summary>Événements de caisse, anomalies et réglages de la surveillance (database/054_surveillance_caisse.sql).</summary>
public sealed class SurveillanceSql(NpgsqlDataSource db) : ISurveillance
{
    private static NpgsqlParameter Valeur(object? v, NpgsqlDbType type) => new() { Value = v ?? DBNull.Value, NpgsqlDbType = type };

    public async Task EnregistrerAsync(EvenementCaisse e, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO evenement_caisse (type, utilisateur_id, poste, session_caisse_id, produit_id, lot_id, quantite, montant, detail)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            """);
        cmd.Parameters.AddWithValue(e.Type);
        cmd.Parameters.AddWithValue(e.UtilisateurId);
        cmd.Parameters.Add(Valeur(e.Poste, NpgsqlDbType.Text));
        cmd.Parameters.Add(Valeur(e.SessionCaisseId, NpgsqlDbType.Bigint));
        cmd.Parameters.Add(Valeur(e.ProduitId, NpgsqlDbType.Bigint));
        cmd.Parameters.Add(Valeur(e.LotId, NpgsqlDbType.Bigint));
        cmd.Parameters.Add(Valeur(e.Quantite, NpgsqlDbType.Integer));
        cmd.Parameters.Add(Valeur(e.Montant, NpgsqlDbType.Numeric));
        cmd.Parameters.Add(Valeur(e.Detail, NpgsqlDbType.Text));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AnomalieNouvelle>> SurveillerAsync(DateOnly jour, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT o_id, o_niveau, o_notification FROM surveiller($1)");
        cmd.Parameters.AddWithValue(jour);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<AnomalieNouvelle>();
        while (await r.ReadAsync(ct)) liste.Add(new AnomalieNouvelle(r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return liste;
    }

    public async Task<IReadOnlyList<AlerteSurveillance>> ListerAsync(bool traitees, int limite, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT a.id, a.type, a.niveau, a.utilisateur_id, u.nom_complet, a.jour, a.message, a.valeur, a.statut, a.mise_a_jour_le,
                   t.nom_complet, a.traitee_le, a.action_prise
            FROM alerte_surveillance a
            LEFT JOIN utilisateur u ON u.id = a.utilisateur_id
            LEFT JOIN utilisateur t ON t.id = a.traitee_par
            WHERE (a.statut = 'traitee') = $1
            ORDER BY CASE a.niveau WHEN 'urgent' THEN 0 ELSE 1 END, a.jour DESC, a.mise_a_jour_le DESC
            LIMIT $2
            """);
        cmd.Parameters.AddWithValue(traitees);
        cmd.Parameters.AddWithValue(limite);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<AlerteSurveillance>();
        while (await r.ReadAsync(ct))
            liste.Add(new AlerteSurveillance(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetString(4), DateOnly.FromDateTime(r.GetDateTime(5)), r.GetString(6), r.IsDBNull(7) ? null : r.GetDecimal(7),
                r.GetString(8), r.GetDateTime(9), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetDateTime(11),
                r.IsDBNull(12) ? null : r.GetString(12)));
        return liste;
    }

    public async Task<bool> TraiterAsync(long id, string? actionPrise, long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE alerte_surveillance SET statut = 'traitee', action_prise = $2, traitee_par = $3, traitee_le = now(), mise_a_jour_le = now()
            WHERE id = $1 AND statut = 'nouvelle'
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.Add(Valeur(actionPrise, NpgsqlDbType.Text));
        cmd.Parameters.AddWithValue(utilisateurId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<ParametresSurveillance> ParametresAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT seuil_lignes_retirees, seuil_tiroir, seuil_attentes_abandonnees, seuil_remises, seuil_remises_patient, heure_ouverture, heure_fermeture,
                   seuil_ajustements, seuil_casses, seuil_echantillons, jours_comptage_sensibles, reception_aveugle, jours_avoir_retour
            FROM parametre_surveillance WHERE id = 1
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Réglages de surveillance absents : exécutez database/054_surveillance_caisse.sql.");
        return new ParametresSurveillance(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4),
            r.IsDBNull(5) ? null : TimeOnly.FromTimeSpan(r.GetTimeSpan(5)), r.IsDBNull(6) ? null : TimeOnly.FromTimeSpan(r.GetTimeSpan(6)),
            r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10), r.GetBoolean(11), r.GetInt32(12));
    }

    public async Task EnregistrerParametresAsync(ParametresSurveillance p, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE parametre_surveillance SET seuil_lignes_retirees = $1, seuil_tiroir = $2, seuil_attentes_abandonnees = $3, seuil_remises = $4,
                   seuil_remises_patient = $5, heure_ouverture = $6, heure_fermeture = $7, seuil_ajustements = $8, seuil_casses = $9,
                   seuil_echantillons = $10, jours_comptage_sensibles = $11, reception_aveugle = $12, jours_avoir_retour = $13
            WHERE id = 1
            """);
        foreach (var v in new[] { p.SeuilLignesRetirees, p.SeuilTiroir, p.SeuilAttentesAbandonnees, p.SeuilRemises, p.SeuilRemisesPatient })
            cmd.Parameters.AddWithValue(v);
        cmd.Parameters.Add(Valeur(p.HeureOuverture, NpgsqlDbType.Time));
        cmd.Parameters.Add(Valeur(p.HeureFermeture, NpgsqlDbType.Time));
        foreach (var v in new[] { p.SeuilAjustements, p.SeuilCasses, p.SeuilEchantillons, p.JoursComptageSensibles })
            cmd.Parameters.AddWithValue(v);
        cmd.Parameters.AddWithValue(p.ReceptionAveugle);
        cmd.Parameters.AddWithValue(p.JoursAvoirRetour);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ProduitSensible>> SensiblesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT p.id, p.nom_commercial, COALESCE((SELECT sum(l.quantite_disponible) FROM lot l WHERE l.produit_id = p.id), 0)::int
            FROM produit p WHERE p.sensible ORDER BY p.nom_commercial
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<ProduitSensible>();
        while (await r.ReadAsync(ct)) liste.Add(new ProduitSensible(r.GetInt64(0), r.GetString(1), r.GetInt32(2)));
        return liste;
    }

    public async Task<string?> MarquerSensibleAsync(long produitId, bool sensible, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE produit SET sensible = $2 WHERE id = $1 RETURNING nom_commercial");
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.AddWithValue(sensible);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
}
