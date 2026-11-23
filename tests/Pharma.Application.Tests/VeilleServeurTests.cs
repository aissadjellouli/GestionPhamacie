using Pharma.Application.SecondServeur;

namespace Pharma.Application.Tests;

/// <summary>Vague 2, lot V2-1 : API en veille (démarrage sur une base en recovery seulement si un 2e serveur est déclaré).</summary>
public class VeilleServeurTests
{
    [Theory]
    [InlineData(true, true, false, true)]       // 2e serveur déclaré + recovery : veille
    [InlineData(false, true, false, false)]     // officine à un seul serveur : rien ne change (refus de démarrage conservé)
    [InlineData(true, false, false, false)]     // base en écriture : démarrage normal
    [InlineData(true, null, false, false)]      // base illisible : pas de veille
    [InlineData(true, true, true, false)]       // démonstration : jamais de veille
    public void Veille_au_demarrage_seulement_si_2e_serveur_declare_et_base_en_recovery(bool declare, bool? recovery, bool demo, bool attendu) =>
        Assert.Equal(attendu, ModeVeille.AuDemarrage(declare, recovery, demo));

    [Fact]
    public void Un_seul_serveur_declare_la_base_en_lecture_seule_est_toujours_refusee()
    {
        var veille = ModeVeille.AuDemarrage(DeuxServeursDeclares.Est(null), baseEnRecovery: true, demoActive: false);
        Assert.False(veille);
        Assert.NotNull(ReplicationOfficine.RefusDemarrage(true, null, null, veillePermise: veille));
        Assert.NotNull(ReplicationOfficine.RefusDemarrage(true, null, null));
    }

    [Fact]
    public void Deux_serveurs_declares_la_base_en_lecture_seule_n_est_plus_refusee()
    {
        var veille = ModeVeille.AuDemarrage(DeuxServeursDeclares.Est("SRV-1"), baseEnRecovery: true, demoActive: false);
        Assert.True(veille);
        Assert.Null(ReplicationOfficine.RefusDemarrage(true, null, "SRV-1", veillePermise: veille));
        Assert.Null(ReplicationOfficine.RefusDemarrage(true, true, "SRV-1", veillePermise: veille));
    }

    [Fact]
    public void Veille_permise_ne_change_pas_les_autres_refus()
    {
        Assert.NotNull(ReplicationOfficine.RefusDemarrage(false, true, "SRV-2", timeline: 1, timelineAutre: 2, veillePermise: true));
        Assert.Null(ReplicationOfficine.RefusDemarrage(false, false, "SRV-2", veillePermise: true));
    }

    [Fact]
    public void Promotion_vue_apres_deux_releves_consecutifs_en_ecriture()
    {
        var suivi = new SuiviPromotionVeille();
        Assert.False(suivi.Observer(new ReleveServeur(true, null, null)));
        Assert.False(suivi.Observer(new ReleveServeur(false, 2, 1)));   // un seul relevé : jamais une décision
        Assert.False(suivi.Observer(null));                              // illisible : on repart de zéro
        Assert.False(suivi.Observer(new ReleveServeur(false, 2, 1)));
        Assert.True(suivi.Observer(new ReleveServeur(false, 2, 1)));
    }

    [Fact]
    public void Redemarrage_demande_une_seule_fois_et_seulement_en_veille()
    {
        var veille = new VeilleServeur(true);
        var appels = new List<string>();
        veille.RedemarrageDemandeLeve += appels.Add;
        veille.DemanderRedemarrage("base promue");
        veille.DemanderRedemarrage("autre");
        Assert.True(veille.RedemarrageDemande);
        Assert.Equal("base promue", veille.MotifRedemarrage);
        Assert.Equal(["base promue"], appels);

        var principal = new VeilleServeur(false);
        principal.DemanderRedemarrage("x");
        Assert.False(principal.RedemarrageDemande);
    }
}
