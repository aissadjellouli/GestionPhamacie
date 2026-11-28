using Pharma.Api.Securite;
using Pharma.Application.Offre;
using Pharma.Infrastructure.Offre;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Clé de ce serveur et date plancher de l'offre (décision du titulaire du 2026-11-27, point 5) : commande --creer-cle-serveur idempotente,
/// --empreinte-serveur, fichier de la date plancher, et commandes traitées avant tout accès à la base dans Program.cs.
/// </summary>
public sealed class CleServeurOffreTests
{
    private static string Dossier() => Path.Combine(Path.GetTempPath(), "cle-serveur-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Creer_la_cle_est_idempotent_et_l_empreinte_se_relit()
    {
        var dossier = Dossier();
        try
        {
            var cle = new CleServeurFichier(Path.Combine(dossier, CleServeurFichier.FichierNom));
            var sortie = new StringWriter();
            var erreurs = new StringWriter();
            Assert.Equal(1, ConsoleCleServeur.Executer([ConsoleCleServeur.ArgumentEmpreinte], cle, sortie, erreurs));   // pas encore de clé
            Assert.Contains("--creer-cle-serveur", erreurs.ToString());

            Assert.Equal(0, ConsoleCleServeur.Executer([ConsoleCleServeur.ArgumentCreer], cle, sortie, erreurs));
            var empreinte = cle.Lire()!.Empreinte;
            Assert.Contains("créée", sortie.ToString());
            Assert.Contains("LOGICIELLE", sortie.ToString());                                                           // avertissement sans TPM

            var deuxieme = new StringWriter();
            Assert.Equal(0, ConsoleCleServeur.Executer([ConsoleCleServeur.ArgumentCreer], cle, deuxieme, erreurs));
            Assert.Contains("existe déjà", deuxieme.ToString());
            Assert.Equal(empreinte, cle.Lire()!.Empreinte);                                                            // jamais remplacée
            Assert.Contains(EmpreinteServeur.Afficher(empreinte), deuxieme.ToString());
            Assert.True(PreuveCleServeur.Prouver(cle, [empreinte]).Autorisee);
        }
        finally { if (Directory.Exists(dossier)) Directory.Delete(dossier, true); }
    }

    [Fact]
    public void La_date_plancher_se_garde_dans_un_fichier_et_un_fichier_abime_ne_bloque_rien()
    {
        var dossier = Dossier();
        try
        {
            var fichier = DatePlancherFichier.CheminParDefaut(dossier);
            var p = new DatePlancherFichier(fichier);
            Assert.Null(p.Lire());
            var date = new DateTime(2027, 1, 10, 12, 0, 0, DateTimeKind.Utc);
            p.Ecrire(date);
            Assert.Equal(date, new DatePlancherFichier(fichier).Lire());
            File.WriteAllText(fichier, "abîmé");
            Assert.Null(new DatePlancherFichier(fichier).Lire());
        }
        finally { if (Directory.Exists(dossier)) Directory.Delete(dossier, true); }
    }

    [Fact]
    public void La_date_du_central_se_garde_dans_le_meme_fichier_sans_toucher_la_date_plancher()
    {
        // Correction du 2026-11-27 (point 4) : un fichier d'avant (sans « Centrale ») se relit ; chaque date garde l'autre
        var dossier = Dossier();
        try
        {
            var fichier = DatePlancherFichier.CheminParDefaut(dossier);
            Directory.CreateDirectory(Path.GetDirectoryName(fichier)!);
            File.WriteAllText(fichier, """{"Utc":"2026-11-20T12:00:00Z"}""");
            var p = new DatePlancherFichier(fichier);
            Assert.Equal((new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc), (DateTime?)null), (p.Lire(), p.LireCentrale()));
            var centrale = new DateTime(2026, 11, 27, 8, 30, 0, DateTimeKind.Utc);
            p.NoterCentrale(centrale);
            Assert.Equal((new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc), (DateTime?)centrale), (new DatePlancherFichier(fichier).Lire(), new DatePlancherFichier(fichier).LireCentrale()));
            p.Ecrire(new DateTime(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc));
            Assert.Equal(centrale, new DatePlancherFichier(fichier).LireCentrale());
            Assert.Equal(DateTimeKind.Utc, new DatePlancherFichier(fichier).LireCentrale()!.Value.Kind);
            // Sans fichier : la date du central s'écrit seule
            var neuf = new DatePlancherFichier(Path.Combine(dossier, "autre", DatePlancherFichier.FichierNom));
            neuf.NoterCentrale(centrale);
            Assert.Equal(((DateTime?)null, (DateTime?)centrale), (neuf.Lire(), neuf.LireCentrale()));
            // Dernière heure vue pendant un recul de l'horloge (2026-11-28) : même fichier, les autres dates gardées ; ancien fichier : null
            Assert.Null(neuf.LireVue());
            var vue = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
            neuf.NoterVue(vue);
            var relu = new DatePlancherFichier(Path.Combine(dossier, "autre", DatePlancherFichier.FichierNom));
            Assert.Equal(((DateTime?)vue, (DateTime?)centrale, DateTimeKind.Utc), (relu.LireVue(), relu.LireCentrale(), relu.LireVue()!.Value.Kind));
        }
        finally { if (Directory.Exists(dossier)) Directory.Delete(dossier, true); }
    }

    [Fact]
    public void Une_cle_en_fichier_est_toujours_logicielle()
    {
        var dossier = Dossier();
        try
        {
            var cle = new CleServeurFichier(Path.Combine(dossier, CleServeurFichier.FichierNom));
            Assert.Null(((ICleServeur)cle).Materielle());   // absente
            cle.CreerSiAbsente();
            Assert.False(((ICleServeur)cle).Materielle());
        }
        finally { if (Directory.Exists(dossier)) Directory.Delete(dossier, true); }
    }

    [Fact]
    public void Les_commandes_de_la_cle_du_serveur_passent_avant_la_base()
    {
        var programme = File.ReadAllText(Path.Combine(Racine(), "src", "Pharma.Api", "Program.cs"));
        var commande = programme.IndexOf("ConsoleCleServeur.Concerne(args)", StringComparison.Ordinal);
        Assert.True(commande > 0);
        Assert.True(commande < programme.IndexOf("DemarrageServeur.LireAsync", StringComparison.Ordinal), "La commande doit passer avant la lecture de la base.");
        Assert.True(commande < programme.IndexOf("JetonService.VerifierCle", StringComparison.Ordinal), "La commande ne doit pas dépendre de la clé des jetons.");
    }

    private static string Racine()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "PharmaOfficine.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }
}
