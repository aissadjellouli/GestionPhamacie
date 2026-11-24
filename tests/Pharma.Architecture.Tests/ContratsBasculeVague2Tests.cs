using System.Text.Json;
using Pharma.Api.Securite;
using Pharma.Shared;

namespace Pharma.Architecture.Tests;

/// <summary>Bascule du 2e serveur, vague 2, lot V2-0 : fige les noms JSON et les valeurs de fil des contrats (lots parallèles).</summary>
public class ContratsBasculeVague2Tests
{
    private static string[] Champs(object dto) =>
        JsonDocument.Parse(JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .RootElement.EnumerateObject().Select(p => p.Name).ToArray();

    [Fact]
    public void Champs_json_des_dto_figes()
    {
        var maintenant = DateTime.UtcNow;
        Assert.Equal(["poste", "serveur1Joint", "echecDepuisUtc", "versionPoste", "adresseServeur1"],
            Champs(new RapportPosteDto("P1", true, null, "1.0", "https://s1:5443")));
        Assert.Equal(["role", "consigne", "message"], Champs(new ConsigneVigieDto("veille", "rester", null)));
        Assert.Equal(["serveur", "role", "timeline", "enRecovery", "versionApi", "empreinteJeton", "reglages"],
            Champs(new EtatInterServeursDto("SRV-1", "principal", 3, false, "1.0", "ab12")));
        Assert.Equal(["ecarte", "lsnFinal"], Champs(new RetraitInterServeursDto(true, "0/16B3748")));
        Assert.Equal(["type", "poste", "message", "survenueUtc"], Champs(new AlerteInterServeursDto("reseau_poste", "P1", "m", maintenant)));
    }

    [Fact]
    public void Valeurs_de_fil_figees()
    {
        Assert.Equal(["principal", "veille", "ecarte"], [RoleServeur.Principal, RoleServeur.Veille, RoleServeur.Ecarte]);
        Assert.Equal(["rester", "attendre", "relais", "basculer"],
            [ConsigneVigie.Rester, ConsigneVigie.Attendre, ConsigneVigie.Relais, ConsigneVigie.Basculer]);
        Assert.Equal(["bascule", "timelines_egales", "isole"],
            [MotifEcartServeur.Bascule, MotifEcartServeur.TimelinesEgales, MotifEcartServeur.Isole]);
        Assert.Equal(MotifEcartServeur.Bascule, BarrageServeurEcarte.MotifBascule);
        Assert.Equal(MotifEcartServeur.TimelinesEgales, BarrageServeurEcarte.MotifTimelinesEgales);
        Assert.Equal(MotifEcartServeur.Isole, BarrageServeurEcarte.MotifIsole);
        Assert.Equal("serveur_en_veille", CodesBasculeVague2.ServeurEnVeille);
        Assert.Equal(["X-Pharma-Serveur", "X-Pharma-Horodatage", "X-Pharma-Nonce", "X-Pharma-Signature", "X-Pharma-Poste"],
            [EnTetesInterServeurs.Serveur, EnTetesInterServeurs.Horodatage, EnTetesInterServeurs.Nonce, EnTetesInterServeurs.Signature, EnTetesInterServeurs.Poste]);
    }

    [Fact]
    public void Seuils_par_defaut_figes_par_les_decisions_du_titulaire()
    {
        Assert.Equal(30, ReglagesBasculeParDefaut.PertePosteSecondes);
        Assert.Equal(30, ReglagesBasculeParDefaut.InjoignableServeur1Secondes);
        Assert.Equal(15, ReglagesBasculeParDefaut.PlancherReglableSecondes);
        Assert.Equal(300, ReglagesBasculeParDefaut.PlafondReglableSecondes);
        Assert.Equal(20, ReglagesBasculeParDefaut.IsolementServeur1Secondes);
        Assert.Equal(60, ReglagesBasculeParDefaut.PromotionMinimumSecondes);
        Assert.Equal(120, ReglagesBasculeParDefaut.RetourVoieDirecteSecondes);
        Assert.StartsWith("Ce poste est coupé du réseau de la pharmacie.", MessagesBasculeVague2.PosteIsole);
    }
}
