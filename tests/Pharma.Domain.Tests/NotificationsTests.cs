using Pharma.Domain.Commun;
using Pharma.Domain.Notifications;

namespace Pharma.Domain.Tests;

public class NotificationsTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 60)]
    [InlineData(5, 60)]      // puis toutes les heures
    [InlineData(40, 60)]
    public void Delai_des_nouvelles_tentatives(int echecs, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), RegleNouvelleTentative.Delai(echecs));

    [Fact]
    public void Abandon_au_dela_d_une_semaine()
    {
        var cree = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
        Assert.Equal(cree.AddDays(6).AddHours(1), RegleNouvelleTentative.Prochaine(cree.AddDays(6), cree, 10));
        Assert.Null(RegleNouvelleTentative.Prochaine(cree.AddDays(7), cree, 10));
    }

    private static Destinataire Titulaire(bool valide = true, bool actif = true) =>
        new(1, "Titulaire", "titulaire@pharmacie.dz", "0661000000", true, true,
            new HashSet<TypeNotification> { TypeNotification.RapportQuotidien, TypeNotification.EcartCaisse }, valide, actif);

    [Fact]
    public void Destinataire_non_verifie_ne_recoit_que_le_test()
    {
        var d = Titulaire(valide: false);
        Assert.False(d.Recoit(TypeNotification.EcartCaisse, CanalNotification.Sms));
        Assert.False(d.Recoit(TypeNotification.RapportQuotidien, CanalNotification.Email));
        Assert.True(d.Recoit(TypeNotification.Test, CanalNotification.Email));
    }

    [Fact]
    public void Destinataire_recoit_seulement_ses_abonnements_et_rien_s_il_est_desactive()
    {
        Assert.True(Titulaire().Recoit(TypeNotification.EcartCaisse, CanalNotification.Sms));
        Assert.False(Titulaire().Recoit(TypeNotification.LotBloque, CanalNotification.Sms));
        Assert.False(Titulaire(actif: false).Recoit(TypeNotification.Test, CanalNotification.Email));
    }

    [Theory]
    [InlineData("0661 00 00 00", true)]
    [InlineData("+213661000000", true)]
    [InlineData("021000000", false)]        // fixe : pas de SMS
    [InlineData("06610000", false)]
    public void Numero_de_mobile_algerien(string numero, bool valide)
    {
        void Verifier() => RegleDestinataire.Verifier("Titulaire", null, numero, false, true, [TypeNotification.EcartCaisse]);
        if (valide) Verifier(); else Assert.Throws<RegleMetierException>(Verifier);
    }

    [Fact]
    public void Destinataire_sans_canal_ou_sans_type_refuse()
    {
        Assert.Throws<RegleMetierException>(() => RegleDestinataire.Verifier("T", "t@p.dz", null, false, false, [TypeNotification.EcartCaisse]));
        Assert.Throws<RegleMetierException>(() => RegleDestinataire.Verifier("T", "t@p.dz", null, true, false, []));
        Assert.Throws<RegleMetierException>(() => RegleDestinataire.Verifier("T", "pas-un-email", null, true, false, [TypeNotification.EcartCaisse]));
    }

    [Fact]
    public void Sms_sans_accents_et_limite()
    {
        Assert.Equal("Ecart de caisse : -1 200 DA a la cloture", TexteSms.Preparer("Écart de caisse : −1 200 DA à la clôture".Replace('−', '-')));
        Assert.True(TexteSms.Preparer(new string('x', 500)).Length <= TexteSms.LongueurMax);
    }
}
