using Npgsql;
using Pharma.Application.Tracabilite;

namespace Pharma.Infrastructure.Lecture;

public sealed class TracabiliteLecture(NpgsqlDataSource db) : ITracabiliteLecture
{
    public async Task<ParcoursBoite?> ParcoursAsync(string codeOuSerie, CancellationToken ct = default)
    {
        const string sqlBoite = """
            SELECT p.boite_id, p.identifiant, p.nom_commercial, p.numero_lot, p.date_peremption,
                   p.fournisseur, p.numero_bl, p.date_reception, p.statut, p.bloque,
                   p.ticket, p.date_vente, p.chifa_facture, p.patient, l.produit_id,
                   pr.classe IN ('psychotrope', 'stupefiant')
            FROM v_parcours_boite p
            JOIN boite b ON b.id = p.boite_id
            JOIN lot l   ON l.id = b.lot_id
            JOIN produit pr ON pr.id = l.produit_id
            WHERE b.code_etiquette = $1 OR b.numero_serie = $1
            LIMIT 1
            """;
        await using var cmd = db.CreateCommand(sqlBoite);
        cmd.Parameters.AddWithValue(codeOuSerie);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        var boiteId = r.GetInt64(0);
        string? Texte(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        var parcours = new ParcoursBoite(
            boiteId, r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateOnly>(4),
            r.GetString(5), Texte(6), r.GetDateTime(7), r.GetString(8), r.GetBoolean(9),
            Texte(10), r.IsDBNull(11) ? null : r.GetDateTime(11), Texte(12), Texte(13), [], r.GetInt64(14), r.GetBoolean(15));
        await r.CloseAsync();

        await using var cmdEtapes = db.CreateCommand(
            "SELECT type, cree_le, commentaire FROM boite_evenement WHERE boite_id = $1 ORDER BY cree_le");
        cmdEtapes.Parameters.AddWithValue(boiteId);
        await using var re = await cmdEtapes.ExecuteReaderAsync(ct);
        var etapes = new List<EtapeParcours>();
        while (await re.ReadAsync(ct))
            etapes.Add(new EtapeParcours(re.GetString(0), re.GetDateTime(1), re.IsDBNull(2) ? null : re.GetString(2)));

        return parcours with { Etapes = etapes };
    }
}
