using Pharma.Application.Synchronisation;
using Pharma.Shared;

namespace Pharma.Api.Synchronisation;

/// <summary>Adaptateur du contrat sync-v1 (Pharma.Shared) pour la constitution des lots (lot S1, sans envoi).</summary>
public sealed class FormatLotSyncV1 : IFormatLotSync
{
    public string VersionContrat => ContratSync.VersionV1;

    public int TailleOctets(LigneSortieSync ligne) => SerialisationSyncV1.TailleLigne(Dto(ligne));

    public LotFormule Formuler(EnTeteLotSync enTete, IReadOnlyList<LigneSortieSync> lignes)
    {
        var lot = SerialisationSyncV1.AvecEmpreinte(new LotSyncDto(
            new EnTeteLotSyncDto(enTete.VersionContrat, enTete.OfficineId, enTete.Epoque, enTete.NumeroLot, lignes.Count, ""),
            lignes.Select(Dto).ToList()));
        return new LotFormule(SerialisationSyncV1.Serialiser(lot), lot.EnTete.Empreinte);
    }

    private static LigneSyncDto Dto(LigneSortieSync l) => new(l.NumeroEnvoye, l.Table, l.IdLocal, l.Operation, l.SurvenuLeUtc, l.DonneesJson);
}
