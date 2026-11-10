using Npgsql;
using Pharma.Application.Pilotage;

namespace Pharma.Infrastructure.Lecture;

/// <summary>Tableau de bord du titulaire : lectures seules, journée du serveur.</summary>
public sealed class PilotageSql(NpgsqlDataSource db) : IPilotage
{
    public async Task<TableauPilotage> TableauAsync(CancellationToken ct = default)
    {
        decimal ca, organismes, credits, caHier;
        int ventes;
        await using (var cmd = db.CreateCommand("""
            SELECT COALESCE(sum(montant_total) FILTER (WHERE date_vente::date = current_date), 0),
                   count(*) FILTER (WHERE date_vente::date = current_date)::int,
                   COALESCE(sum(part_organisme) FILTER (WHERE date_vente::date = current_date), 0),
                   COALESCE((SELECT sum(p.montant) FROM paiement p JOIN vente v ON v.id = p.vente_id
                             WHERE p.mode = 'credit' AND v.statut = 'validee' AND v.date_vente >= current_date AND v.date_vente < current_date + 1), 0),
                   COALESCE(sum(montant_total) FILTER (WHERE date_vente::date = current_date - 1), 0)
            FROM vente WHERE statut = 'validee' AND date_vente >= current_date - 1
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            (ca, ventes, organismes, credits, caHier) = (r.GetDecimal(0), r.GetInt32(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4));
        }

        // Espèces attendues : même calcul que la clôture (encaissées - rendues - complément grossiste - portées au coffre)
        var caisses = new List<CaisseOuverte>();
        await using (var cmd = db.CreateCommand("""
            SELECT s.poste, u.nom_complet, s.ouverte_le, s.fond_caisse,
                   s.fond_caisse
                   + COALESCE((SELECT sum(montant) FROM paiement WHERE session_caisse_id = s.id AND mode = 'especes'), 0)
                   - COALESCE((SELECT sum(montant_rembourse) FROM annulation_vente WHERE session_caisse_id = s.id AND mode = 'especes'), 0)
                   - COALESCE((SELECT sum(montant_rembourse) FROM retour_client WHERE session_caisse_id = s.id AND mode = 'especes'), 0)
                   - COALESCE((SELECT sum(part_tiroir) FROM reglement_fournisseur WHERE session_caisse_id = s.id), 0)
                   - COALESCE((SELECT sum(montant) FROM mouvement_tresorerie WHERE session_caisse_id = s.id AND pendant_session), 0),
                   (SELECT count(*) FROM vente v WHERE v.session_caisse_id = s.id AND v.statut = 'validee')::int
            FROM session_caisse s JOIN utilisateur u ON u.id = s.utilisateur_id
            WHERE s.fermee_le IS NULL ORDER BY s.poste
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                caisses.Add(new CaisseOuverte(r.GetString(0), r.GetString(1), r.GetDateTime(2), r.GetDecimal(3), r.GetDecimal(4), r.GetInt32(5)));

        var clotures = new List<ClotureDuJour>();
        await using (var cmd = db.CreateCommand("""
            SELECT poste, fermee_le, COALESCE(montant_attendu, 0), montant_compte, ecart
            FROM session_caisse WHERE fermee_le >= current_date AND fermee_le < current_date + 1 ORDER BY fermee_le DESC
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                clotures.Add(new ClotureDuJour(r.GetString(0), r.GetDateTime(1), r.GetDecimal(2), r.IsDBNull(3) ? null : r.GetDecimal(3), r.IsDBNull(4) ? null : r.GetDecimal(4)));

        var alertes = new List<AlerteResume>();
        int urgentes;
        await using (var cmd = db.CreateCommand("SELECT count(*)::int FROM alerte WHERE statut = 'nouvelle' AND niveau = 'urgent'"))
            urgentes = (int)(await cmd.ExecuteScalarAsync(ct))!;
        await using (var cmd = db.CreateCommand("""
            SELECT type, niveau, message FROM alerte WHERE statut = 'nouvelle'
            ORDER BY CASE niveau WHEN 'urgent' THEN 0 WHEN 'attention' THEN 1 ELSE 2 END, cree_le DESC LIMIT 8
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) alertes.Add(new AlerteResume(r.GetString(0), r.GetString(1), r.GetString(2)));

        int ecarts;
        await using (var cmd = db.CreateCommand("SELECT count(*)::int FROM ligne_inventaire WHERE statut = 'a_valider'"))
            ecarts = (int)(await cmd.ExecuteScalarAsync(ct))!;

        var comptes = new List<SoldeCompte>();
        await using (var cmd = db.CreateCommand("SELECT nom, solde FROM v_solde_tresorerie WHERE actif ORDER BY type DESC, nom"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) comptes.Add(new SoldeCompte(r.GetString(0), r.GetDecimal(1)));

        decimal aEncaisser;
        await using (var cmd = db.CreateCommand("""
            SELECT COALESCE(sum(reste), 0) FROM v_facture_client WHERE type = 'facture' AND statut <> 'remplacee' AND NOT hors_suivi
            """))
            aEncaisser = (decimal)(await cmd.ExecuteScalarAsync(ct))!;

        string? inventaire;
        await using (var cmd = db.CreateCommand("""
            SELECT CASE mode WHEN 'fermee' THEN 'Inventaire complet en cours (pharmacie fermée)' ELSE 'Inventaire complet en cours (pharmacie ouverte)' END
            FROM campagne_inventaire WHERE statut = 'en_cours'
            """))
            inventaire = await cmd.ExecuteScalarAsync(ct) as string;

        return new TableauPilotage(DateTime.UtcNow, ca, ventes, organismes, credits, caHier, caisses, clotures, urgentes, alertes, ecarts, comptes,
            aEncaisser, inventaire);
    }
}
