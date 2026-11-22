using Npgsql;
using Pharma.Cloud.Api.Depart;
using BaseCentraleJetable = Pharma.Cloud.Tests.DescendantsMutuV1Tests.BaseCentraleJetable;

namespace Pharma.Cloud.Tests;

/// <summary>
/// Commande --envoi-codes de la console du central (2026-11-21) : compte e-mail des codes réglé sur le serveur, chiffré avec les clés du
/// dossier du central (relu à l'identique par les écrans et la console), journal de sécurité sans identifiant ni mot de passe.
/// </summary>
public class EnvoiCodesConsoleTests
{
    private const string MotDePasse = "abcdefghijklmnop";

    [Theory]
    [InlineData("smtp.gmail.com", true)]
    [InlineData("mail-1.exemple.org", true)]
    [InlineData("https://smtp.gmail.com", false)]
    [InlineData("smtp.gmail.com:587", false)]
    [InlineData("smtp gmail.com", false)]
    [InlineData("", false)]
    public void Serveur_nom_d_hote_seul(string serveur, bool attendu) => Assert.Equal(attendu, ReglageSmtpBase.ServeurValide(serveur));

    [Fact]
    public void Refus_de_l_identifiant_reconnu_meme_rendu_en_deconnexion()
    {
        Assert.True(ErreursSmtp.RefusIdentifiants(new MailKit.Net.Smtp.SmtpProtocolException(
            "The SMTP server has unexpectedly disconnected: 5.7.8 Username and Password not accepted.")));
        Assert.True(ErreursSmtp.RefusIdentifiants(new MailKit.Security.AuthenticationException("refus")));
        Assert.False(ErreursSmtp.RefusIdentifiants(new MailKit.Net.Smtp.SmtpProtocolException("The SMTP server has unexpectedly disconnected.")));
        Assert.False(ErreursSmtp.RefusIdentifiants(new TimeoutException()));
    }

    [Fact]
    public async Task Domaine_reserve_invalid_jamais_envoye()
    {
        Assert.True(AdresseCourriel.Reservee("mesure@exemple.invalid"));
        Assert.True(AdresseCourriel.Reservee("A@B.INVALID "));
        Assert.False(AdresseCourriel.Reservee("titulaire@gmail.com"));
        Assert.False(AdresseCourriel.Reservee("x@invalid.fr"));
        // Serveur injoignable volontairement : l'exception doit venir AVANT toute connexion
        var (smtp, _) = ReglagesSmtpCentral.Creer("serveur.injoignable.invalid", 587, "starttls", "a@exemple.org", MotDePasse, "a@exemple.org");
        await Assert.ThrowsAsync<AdresseReserveeException>(() => new EnvoiCourrielSmtp(smtp!).EnvoyerAsync("mesure@exemple.invalid", "objet", "texte", CancellationToken.None));
        Assert.IsAssignableFrom<NotSupportedException>(new AdresseReserveeException());
    }

    [Fact]
    public async Task Enregistre_chiffre_relu_a_l_identique_et_journal_sans_identifiant()
    {
        using var base_ = BaseCentraleJetable.Creer();
        if (base_ is null) return;   // pas de serveur, de psql ou de droit CREATEDB : rien à vérifier ici
        var dossier = Path.Combine(Path.GetTempPath(), "pharma_central_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await using var db = NpgsqlDataSource.Create(base_.Connexion);
            var protection = ProtectionSmtpCentral.Depuis(ProtectionSmtpCentral.DossierDuCentral(dossier))!;
            var (smtp, _) = ReglagesSmtpCentral.Creer("smtp.gmail.com", 587, "starttls", "envoi@exemple.org", MotDePasse, "envoi@exemple.org");

            await ReglageSmtpBase.EnregistrerAsync(db, protection, smtp!, "starttls", "installateur", CancellationToken.None);

            // Relu par la console (et les écrans : mêmes clés du dossier du central), mot de passe jamais en clair en base
            var (relu, manque) = await ReglageSmtpBase.LireAsync(db, dossier, CancellationToken.None);
            Assert.Null(manque);
            Assert.Equal(("smtp.gmail.com", 587, "envoi@exemple.org", MotDePasse), (relu!.Hote, relu.Port, relu.Utilisateur, relu.MotDePasse));
            await using (var cmd = db.CreateCommand("SELECT canal || '|' || smtp_mot_de_passe_chiffre FROM parametre_envoi_codes"))
            {
                var ligne = (string)(await cmd.ExecuteScalarAsync())!;
                Assert.StartsWith("courriel|", ligne);
                Assert.DoesNotContain(MotDePasse, ligne);
            }
            await using (var cmd = db.CreateCommand("SELECT detail FROM journal_securite WHERE evenement = 'envoi_codes_modifie' ORDER BY id DESC LIMIT 1"))
            {
                var detail = (string)(await cmd.ExecuteScalarAsync())!;
                Assert.Contains("console (opérateur installateur)", detail);
                Assert.Contains("smtp.gmail.com:587 starttls", detail);
                Assert.DoesNotContain("@", detail);
                Assert.DoesNotContain(MotDePasse, detail);
            }

            // Serveur invalide ou opérateur absent : rien n'est enregistré
            var (autre, _) = ReglagesSmtpCentral.Creer("https://x", 587, "starttls", "a@exemple.org", MotDePasse, "a@exemple.org");
            await Assert.ThrowsAsync<ArgumentException>(() => ReglageSmtpBase.EnregistrerAsync(db, protection, autre!, "starttls", "installateur", CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => ReglageSmtpBase.EnregistrerAsync(db, protection, smtp!, "starttls", " ", CancellationToken.None));
            Assert.Equal("smtp.gmail.com", (await ReglageSmtpBase.LireAsync(db, dossier, CancellationToken.None)).Reglages!.Hote);
        }
        finally
        {
            try { Directory.Delete(dossier, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
