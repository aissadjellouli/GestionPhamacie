using Microsoft.Extensions.Logging;
using Npgsql;
using Pharma.Application.Serialisation;

namespace Pharma.Infrastructure.Serialisation;


/// <summary>
/// Adaptateur FACTICE de la base nationale de sérialisation, pour les tests et la démonstration, en attendant l'API du MIPH
/// [À VÉRIFIER]. Réponses déterministes : la première règle de serialisation_reponse_factice qui correspond (ordre des id) ;
/// à défaut, « expirée » si la péremption est passée, sinon « authentique ». parametre_serialisation.factice_hors_ligne simule
/// une panne (ConnecteurIndisponibleException). Chaque réponse est journalisée (journal du serveur) et tracée dans la file.
/// </summary>
public sealed class SerialisationFactice(NpgsqlDataSource db, ILogger<SerialisationFactice> logger, TimeProvider? horloge = null) : ISerialisationConnecteur
{
    public string Nom => "Base nationale (factice)";

    public Task<ReponseSerialisation> VerifierAsync(BoiteSerialisee boite, CancellationToken ct = default) =>
        RepondreAsync(OperationSerialisation.Verifier, boite, ct);

    public Task<ReponseSerialisation> DecommissionnerAsync(BoiteSerialisee boite, CancellationToken ct = default) =>
        RepondreAsync(OperationSerialisation.Decommissionner, boite, ct);

    public Task<ReponseSerialisation> ReactiverAsync(BoiteSerialisee boite, CancellationToken ct = default) =>
        RepondreAsync(OperationSerialisation.Reactiver, boite, ct);

    private async Task<ReponseSerialisation> RepondreAsync(OperationSerialisation operation, BoiteSerialisee boite, CancellationToken ct)
    {
        bool horsLigne;
        var regles = new List<RegleReponseFactice>();
        await using (var cmd = db.CreateCommand("SELECT COALESCE((SELECT factice_hors_ligne FROM parametre_serialisation WHERE id = 1), false)"))
            horsLigne = (bool)(await cmd.ExecuteScalarAsync(ct))!;
        if (horsLigne)
        {
            logger.LogInformation("Sérialisation factice : hors ligne simulé, {Operation} de la boîte {Gtin} reportée", operation, boite.Gtin);
            throw new ConnecteurIndisponibleException("hors ligne (panne simulée)");
        }
        await using (var cmd = db.CreateCommand(
            "SELECT gtin, numero_serie, numero_lot, operation, resultat FROM serialisation_reponse_factice ORDER BY id"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                if (LibellesSerialisation.DepuisCode(r.GetString(4)) is { } res)
                    regles.Add(new RegleReponseFactice(r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                        r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : LibellesSerialisation.OperationDepuisCode(r.GetString(3)), res));

        var aujourdhui = DateOnly.FromDateTime((horloge ?? TimeProvider.System).GetLocalNow().Date);
        var reponse = ReponsesFactices.Repondre(regles, operation, boite, aujourdhui);
        logger.LogInformation("Sérialisation factice : {Operation} de la boîte {Gtin} : {Resultat}", operation, boite.Gtin, reponse.Statut);
        return reponse;
    }
}
