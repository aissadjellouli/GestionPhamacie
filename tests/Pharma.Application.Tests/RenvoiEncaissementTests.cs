using Pharma.Desktop.Services;
using Pharma.Shared;

namespace Pharma.Application.Tests;

/// <summary>
/// Encaissement interrompu par la panne du serveur (Q25, corrigé le 2026-11-25) : à l'essai réel, la promotion du 2e serveur est arrivée
/// 108 s après la panne, alors que le poste ne tentait la bascule qu'une fois, aussitôt l'échec : la vente n'était jamais renvoyée.
/// Désormais la commande est gardée et renvoyée une fois dès la bascule ou le retour du serveur, au plus pendant 5 minutes.
/// </summary>
public class RenvoiEncaissementTests
{
    private static readonly DateTime Echec = new(2026, 11, 25, 21, 49, 31, DateTimeKind.Utc);

    private static RenvoiEncaissement Suspens(int generation = 3) =>
        new(new CommandeVenteDto([new LigneCommandeDto(81725, 128571, 1)], false, null, null, null, null, 0, "especes", 1172.50m,
            CleEncaissement: Guid.Parse("6f1d7c1e-0000-4000-8000-000000000001")), "especes", generation, Echec);

    [Fact]
    public void Mis_en_suspens_seulement_apres_une_panne_sur_un_poste_a_deux_serveurs()
    {
        Assert.True(RenvoiEncaissement.APlacer(basculeActive: true, erreurDePanne: true));
        Assert.False(RenvoiEncaissement.APlacer(basculeActive: false, erreurDePanne: true));   // un seul serveur : inchangé
        Assert.False(RenvoiEncaissement.APlacer(basculeActive: true, erreurDePanne: false));   // refus du serveur : affiché tel quel
    }

    [Fact]
    public void Attend_la_promotion_puis_renvoie_des_la_bascule_meme_apres_108_secondes()
    {
        var r = Suspens();
        Assert.Equal(EtapeRenvoi.Attendre, r.Etape(Echec.AddSeconds(5), 3, serveurJoint: false, employeConnecte: true, ventesSuspendues: false));
        Assert.Equal(EtapeRenvoi.Attendre, r.Etape(Echec.AddSeconds(100), 3, false, true, false));
        Assert.Equal(EtapeRenvoi.Renvoyer, r.Etape(Echec.AddSeconds(129), 4, false, true, false));
    }

    [Fact]
    public void Renvoie_aussi_quand_le_serveur_revient_sans_bascule()
    {
        Assert.Equal(EtapeRenvoi.Renvoyer, Suspens().Etape(Echec.AddSeconds(40), 3, serveurJoint: true, employeConnecte: true, ventesSuspendues: false));
    }

    [Fact]
    public void Jamais_de_renvoi_sur_un_poste_isole_ni_sans_employe_connecte()
    {
        var r = Suspens();
        Assert.Equal(EtapeRenvoi.Attendre, r.Etape(Echec.AddSeconds(60), 4, true, employeConnecte: true, ventesSuspendues: true));
        Assert.Equal(EtapeRenvoi.Attendre, r.Etape(Echec.AddSeconds(60), 4, true, employeConnecte: false, ventesSuspendues: false));
    }

    [Fact]
    public void Abandon_du_renvoi_automatique_apres_5_minutes_sans_bascule()
    {
        var r = Suspens();
        Assert.Equal(EtapeRenvoi.Attendre, r.Etape(Echec.AddMinutes(5).AddSeconds(-1), 3, false, true, false));
        Assert.Equal(EtapeRenvoi.Expire, r.Etape(Echec.AddMinutes(5), 3, false, true, false));
        // Bascule constatée au même moment : le renvoi l'emporte (la vente part encore une fois)
        Assert.Equal(EtapeRenvoi.Renvoyer, r.Etape(Echec.AddMinutes(6), 4, false, true, false));
    }

    [Fact]
    public void La_commande_gardee_est_celle_du_premier_envoi_avec_sa_cle()
    {
        var r = Suspens();
        Assert.Equal(Guid.Parse("6f1d7c1e-0000-4000-8000-000000000001"), r.Commande.CleEncaissement);
        Assert.Equal("especes", r.Mode);
    }
}
