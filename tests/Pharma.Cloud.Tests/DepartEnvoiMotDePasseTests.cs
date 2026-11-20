using Pharma.Cloud.Api.Depart;

namespace Pharma.Cloud.Tests;

/// <summary>
/// Mot de passe de l'archive de départ envoyé au titulaire par e-mail (décision du titulaire du 2026-11-20) : envoi factice derrière
/// IEnvoiCourriel ; destinataire = adresse saisie, mot de passe dans le corps seulement ; absent du journal des départs, du compte rendu
/// (hors la ligne d'affichage unique) et des raisons d'échec ; échec non bloquant ; aucun envoi sans --email ou si l'archive échoue.
/// </summary>
public class DepartEnvoiMotDePasseTests
{
    private const string Adresse = "titulaire@exemple.invalid";

    private sealed class EnvoiFactice(Exception? erreur = null) : IEnvoiCourriel
    {
        public List<(string Adresse, string Objet, string Texte)> Envois { get; } = [];

        public Task EnvoyerAsync(string adresse, string objet, string texte, CancellationToken ct)
        {
            Envois.Add((adresse, objet, texte));
            return erreur is null ? Task.CompletedTask : Task.FromException(erreur);
        }
    }

    private static ResultatExport Export(MotDePasseArchive mdp) =>
        new(@"C:\sortie\officine-a.zip", "0123456789abcdef" + new string('c', 48), [new FichierExporte("produit.csv", 2, new string('a', 64))], mdp);

    [Fact]
    public async Task Envoi_au_destinataire_saisi_mot_de_passe_dans_le_corps_seulement()
    {
        var mdp = MotDePasseArchive.Tirer();
        var envoi = new EnvoiFactice();
        var (export, resultat) = await DepartOfficine.ExporterPuisEnvoyerAsync(_ => Task.FromResult(Export(mdp)), Adresse, envoi, null,
            "Pharmacie Essai", CancellationToken.None);

        var (adresse, objet, texte) = Assert.Single(envoi.Envois);
        Assert.Equal(Adresse, adresse);
        Assert.Contains(mdp.Valeur, texte);
        Assert.Contains("Pharmacie Essai", texte);
        Assert.Contains("officine-a.zip", texte);
        Assert.Contains("0123456789abcdef…", texte);
        Assert.DoesNotContain(export.EmpreinteArchive, texte);   // empreinte tronquée seulement
        Assert.DoesNotContain("http", texte, StringComparison.OrdinalIgnoreCase);   // jamais de lien
        Assert.DoesNotContain(mdp.Valeur, objet);
        Assert.True(resultat is { Envoye: true, AdresseMasquee: "t***@exemple.invalid", Raison: null });

        // Journal des départs : adresse masquée et résultat, jamais le mot de passe ni l'adresse complète
        var journal = DepartOfficine.DetailExport(export, resultat);
        Assert.DoesNotContain(mdp.Valeur, journal);
        Assert.DoesNotContain(Adresse, journal);
        Assert.Contains("t***@exemple.invalid", journal);
        Assert.Contains("\"envoye\"", journal);

        // Compte rendu : le mot de passe une seule fois (ligne d'affichage unique), l'envoi sans le mot de passe
        using var console = new StringWriter();
        DepartOfficine.EcrireCompteRendu(console, export, resultat);
        var lignes = console.ToString().Split(Environment.NewLine);
        Assert.Single(lignes, l => l.Contains(mdp.Valeur));
        Assert.StartsWith("Mot de passe de l'archive", lignes.Single(l => l.Contains(mdp.Valeur)));
        Assert.Contains(lignes, l => l.Contains("envoyé à t***@exemple.invalid"));
    }

    [Fact]
    public async Task Echec_d_envoi_non_bloquant_raison_sans_mot_de_passe()
    {
        var mdp = MotDePasseArchive.Tirer();
        // Un serveur qui renverrait le mot de passe dans son message d'erreur : retiré de la raison
        var envoi = new EnvoiFactice(new IOException($"refus du serveur ({mdp.Valeur})"));
        var (export, resultat) = await DepartOfficine.ExporterPuisEnvoyerAsync(_ => Task.FromResult(Export(mdp)), Adresse, envoi, null,
            "Pharmacie Essai", CancellationToken.None);

        Assert.Single(envoi.Envois);
        Assert.NotNull(export);
        Assert.False(resultat!.Envoye);
        Assert.Contains("refus du serveur", resultat.Raison);
        Assert.DoesNotContain(mdp.Valeur, resultat.Raison);
        Assert.DoesNotContain(mdp.Valeur, resultat.ToString());
        Assert.DoesNotContain(mdp.Valeur, DepartOfficine.DetailExport(export, resultat));
        Assert.Contains("\"echec\"", DepartOfficine.DetailExport(export, resultat));
        using var console = new StringWriter();
        DepartOfficine.EcrireCompteRendu(console, export, resultat);
        Assert.Equal(1, Compter(console.ToString(), mdp.Valeur));
        Assert.Contains("ÉCHEC", console.ToString());
        Assert.Contains("L'archive reste valide", console.ToString());
    }

    [Fact]
    public async Task Aucun_envoi_sans_adresse_ni_si_l_archive_echoue()
    {
        var mdp = MotDePasseArchive.Tirer();
        var envoi = new EnvoiFactice();
        var (export, resultat) = await DepartOfficine.ExporterPuisEnvoyerAsync(_ => Task.FromResult(Export(mdp)), null, envoi, null,
            "Pharmacie Essai", CancellationToken.None);
        Assert.Empty(envoi.Envois);
        Assert.Null(resultat);
        Assert.Contains("non demande", DepartOfficine.DetailExport(export, resultat));

        await Assert.ThrowsAsync<IOException>(() => DepartOfficine.ExporterPuisEnvoyerAsync(
            _ => Task.FromException<ResultatExport>(new IOException("disque plein")), Adresse, envoi, null, "Pharmacie Essai", CancellationToken.None));
        Assert.Empty(envoi.Envois);
    }

    [Fact]
    public async Task Reglages_SMTP_incomplets_message_clair_et_pas_d_envoi()
    {
        var variables = new Dictionary<string, string>
        {
            ["PHARMA_CLOUD_SMTP_HOTE"] = "smtp.exemple.invalid", ["PHARMA_CLOUD_SMTP_PORT"] = "587",
            ["PHARMA_CLOUD_SMTP_UTILISATEUR"] = "envoi", ["PHARMA_CLOUD_SMTP_MOT_DE_PASSE"] = "secret-smtp-factice",
            ["PHARMA_CLOUD_SMTP_EXPEDITEUR"] = "envoi@exemple.invalid"
        };
        var (complets, aucun) = ReglagesSmtpCentral.Lire(v => variables.GetValueOrDefault(v));
        Assert.Null(aucun);
        Assert.Equal(MailKit.Security.SecureSocketOptions.StartTls, complets!.Securite);
        Assert.DoesNotContain("secret-smtp-factice", complets.ToString());
        Assert.Equal(MailKit.Security.SecureSocketOptions.SslOnConnect,
            ReglagesSmtpCentral.Lire(v => v == "PHARMA_CLOUD_SMTP_PORT" ? "465" : variables.GetValueOrDefault(v)).Reglages!.Securite);

        var (reglages, manque) = ReglagesSmtpCentral.Lire(v => v == "PHARMA_CLOUD_SMTP_MOT_DE_PASSE" ? null : variables.GetValueOrDefault(v));
        Assert.Null(reglages);
        Assert.Contains("PHARMA_CLOUD_SMTP_MOT_DE_PASSE", manque);
        Assert.Null(ReglagesSmtpCentral.Lire(v => v == "PHARMA_CLOUD_SMTP_PORT" ? "abc" : variables.GetValueOrDefault(v)).Reglages);
        var (_, motDePasseSeul) = ReglagesSmtpCentral.Lire(v => v == "PHARMA_CLOUD_SMTP_PORT" ? "0" : variables.GetValueOrDefault(v));
        Assert.DoesNotContain("secret-smtp-factice", motDePasseSeul);

        // Sans réglages : aucun envoi, échec non bloquant avec la raison claire
        var mdp = MotDePasseArchive.Tirer();
        var (_, resultat) = await DepartOfficine.ExporterPuisEnvoyerAsync(_ => Task.FromResult(Export(mdp)), Adresse, null, manque,
            "Pharmacie Essai", CancellationToken.None);
        Assert.False(resultat!.Envoye);
        Assert.Equal(manque, resultat.Raison);
    }

    [Theory]
    [InlineData("titulaire@exemple.invalid", true)]
    [InlineData("  titulaire@exemple.invalid ", true)]
    [InlineData("titulaire", false)]
    [InlineData("titulaire@exemple", false)]
    [InlineData("ti tulaire@exemple.invalid", false)]
    [InlineData("a@b@exemple.invalid", false)]
    [InlineData("@exemple.invalid", false)]
    [InlineData("--operateur", false)]
    public void Adresse_controlee_avant_tout_export(string adresse, bool valide)
    {
        if (valide) Assert.Equal(adresse.Trim(), AdresseCourriel.Valider(adresse));
        else Assert.Throws<ArgumentException>(() => AdresseCourriel.Valider(adresse));
    }

    [Fact]
    public void Adresse_masquee_pour_le_journal()
    {
        Assert.Equal("a***@exemple.invalid", AdresseCourriel.Masquer("alice@exemple.invalid"));
    }

    private static int Compter(string texte, string motif)
    {
        var n = 0;
        for (var i = texte.IndexOf(motif, StringComparison.Ordinal); i >= 0; i = texte.IndexOf(motif, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }
}
