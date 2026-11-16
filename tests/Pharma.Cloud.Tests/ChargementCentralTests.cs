using Pharma.Cloud.Api.Reception;
using Pharma.Shared;

namespace Pharma.Cloud.Tests;

/// <summary>Lot S6 : chargement complet côté serveur central (contrôles indépendants de la base ; SQL : database/cloud/tests/003).</summary>
public class ChargementCentralTests
{
    private static readonly Guid Officine = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    [Fact]
    public void Un_lot_portant_des_lignes_d_instantane_est_accepte()
    {
        var lot = SerialisationSyncV1.AvecEmpreinte(new LotSyncDto(new EnTeteLotSyncDto(ContratSync.VersionV1, Officine, 1, 1, 2, ""),
            [new LigneSyncDto(40, "vente", "1", ContratSync.Instantane, new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc), """{"id":1}"""),
             new LigneSyncDto(41, "vente", "1", ContratSync.Modification, new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc), """{"id":1}""")]));
        var c = ControleLot.Controler(SerialisationSyncV1.Serialiser(lot), Officine);
        Assert.True(c.Valide, c.Refus);
    }

    [Theory]
    [InlineData(1, 1, 0, 0, "activation", "vente", true)]
    [InlineData(1, 1, 10, 4, "lots_manquants", "vente,patient", true)]
    [InlineData(0, 1, 0, 0, "demande", "vente", false)]
    [InlineData(1, 0, 0, 0, "demande", "vente", false)]
    [InlineData(1, 1, -1, 0, "demande", "vente", false)]
    [InlineData(1, 1, 0, 0, "autre", "vente", false)]
    [InlineData(1, 1, 0, 0, "demande", "", false)]
    [InlineData(1, 1, 0, 0, "demande", "Vente", false)]
    [InlineData(1, 1, 0, 0, "demande", "vente;drop", false)]
    public void Le_debut_d_un_chargement_est_controle(int numero, int epoque, long repere, long reprendre, string motif, string tables, bool valide)
    {
        var d = new DebutChargementDto(numero, epoque, repere, reprendre, motif,
            tables.Length == 0 ? [] : tables.Split(','));
        Assert.Equal(valide, d.Refus() is null);
    }
}
