using System.Text;
using Pharma.Desktop.Services;
using Pharma.Shared;

namespace Pharma.Application.Tests;

/// <summary>
/// Facture et retour fournisseur laissés au « Changer d'utilisateur » : gardés dans un fichier du poste (chiffré DPAPI utilisateur),
/// relus au redémarrage, purgés une fois repris ou périmés ; un fichier abîmé est mis de côté sans arrêter le poste.
/// </summary>
public sealed class FichierTachesLaisseesTests : IDisposable
{
    private readonly string dossier = Path.Combine(Path.GetTempPath(), "pharma-taches-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> journal = [];
    private DateTime maintenant = new(2026, 11, 21, 10, 0, 0);

    public void Dispose()
    {
        if (Directory.Exists(dossier)) Directory.Delete(dossier, recursive: true);
    }

    private FichierTachesLaissees Fichier(string? mode = ReglageTachesLaisseesDto.Chiffre) => new(dossier, journal.Add, () => maintenant, mode);

    private TacheLaissee Facture(string patient = "Benali Karima", DateTime? le = null) =>
        new("facture", $"Nouvelle facture · {patient} · 2 vente(s), 0 ligne(s) libre(s)", "Employé A", "factures.gerer",
            System.Text.Json.JsonSerializer.Serialize(new { Mode = "nouvelle", Preparation = System.Text.Json.JsonSerializer.Serialize(new { Patient = new { Nom = patient, NumAssure = "123456789012" } }) }),
            le ?? maintenant);

    private TacheLaissee Retour(DateTime? le = null) =>
        new("retour", "Retour fournisseur en préparation · 1 lot(s)", "Employé B", "achats.retour_fournisseur",
            """{"Lots":[{"Lot":{"Id":5,"NumeroLot":"L123"},"Quantite":2,"Codes":[]}],"Motif":"perime"}""", le ?? maintenant);

    [Fact]
    public void Aller_retour_ecriture_relecture()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI : Windows seulement
        var facture = Facture();
        var retour = Retour();
        Fichier().Ecrire([facture, retour]);

        var relues = Fichier().Lire();

        Assert.Equal([facture, retour], relues);
        Assert.False(File.Exists(Fichier().Chemin + ".tmp"));
        Assert.Empty(journal);
    }

    [Fact]
    public void Aucune_donnee_en_clair_dans_le_fichier()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI : Windows seulement
        Fichier().Ecrire([Facture("Zerrouki Samira"), Retour()]);

        var octets = File.ReadAllBytes(Fichier().Chemin);
        foreach (var texte in new[] { "Zerrouki", "Samira", "123456789012", "Employé A", "facture", "L123", "factures.gerer" })
        {
            Assert.DoesNotContain(texte, Encoding.UTF8.GetString(octets));
            Assert.DoesNotContain(texte, Encoding.Unicode.GetString(octets));
            Assert.DoesNotContain(texte, Encoding.Latin1.GetString(octets));
        }
    }

    [Fact]
    public void Fichier_corrompu_renomme_ignore_et_note_au_journal()
    {
        Directory.CreateDirectory(dossier);
        File.WriteAllText(Path.Combine(dossier, FichierTachesLaissees.NomFichier), "pas un fichier chiffré");

        var relues = Fichier().Lire();

        Assert.Empty(relues);
        Assert.False(File.Exists(Path.Combine(dossier, FichierTachesLaissees.NomFichier)));
        Assert.True(File.Exists(Path.Combine(dossier, "taches-laissees.illisible-20261121-100000.dat")));
        var ligne = Assert.Single(journal);
        Assert.Contains("illisible", ligne);
        Assert.Contains("taches-laissees.illisible-20261121-100000.dat", ligne);
    }

    [Fact]
    public void Contenu_dechiffre_mais_json_abime_mis_de_cote()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI : Windows seulement
        Directory.CreateDirectory(dossier);
        var protege = System.Security.Cryptography.ProtectedData.Protect("{ pas du json"u8.ToArray(),
            "PharmaOfficine.TachesLaissees"u8.ToArray(), System.Security.Cryptography.DataProtectionScope.CurrentUser);
        File.WriteAllBytes(Path.Combine(dossier, FichierTachesLaissees.NomFichier), protege);

        Assert.Empty(Fichier().Lire());
        Assert.Single(journal);
        Assert.Single(Directory.GetFiles(dossier, "taches-laissees.illisible-*.dat"));
    }

    [Fact]
    public void Plus_aucune_tache_le_fichier_est_supprime()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI : Windows seulement
        Fichier().Ecrire([Facture()]);
        Assert.True(File.Exists(Fichier().Chemin));

        Fichier().Ecrire([]);

        Assert.False(File.Exists(Fichier().Chemin));
        Assert.Empty(Fichier().Lire());
    }

    [Fact]
    public void Reception_non_gardee_dans_le_fichier()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI : Windows seulement
        var reception = new TacheLaissee("reception", "Réception", "Employé A", "achats.receptionner", "42", maintenant);
        Fichier().Ecrire([reception, Retour()]);

        Assert.Equal(["retour"], Fichier().Lire().Select(t => t.Type));
    }

    [Fact]
    public void Tache_perimee_ecartee_a_la_relecture()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI : Windows seulement
        var ancienne = Facture(le: maintenant.AddDays(-6));
        var recente = Retour(le: maintenant.AddDays(-1));
        Fichier().Ecrire([ancienne, recente]);

        // Deux jours plus tard : la facture a plus de 7 jours, le retour 3 jours
        maintenant = maintenant.AddDays(2);
        var relues = Fichier().Lire();

        Assert.Equal([recente], relues);
        Assert.Equal(TimeSpan.FromDays(7), FichierTachesLaissees.DureeDeGarde);
    }

    [Fact]
    public void Tache_perimee_non_ecrite()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI : Windows seulement
        Fichier().Ecrire([Facture(le: maintenant.AddDays(-8))]);

        Assert.False(File.Exists(Fichier().Chemin));
    }

    [Fact]
    public void Fichier_mis_de_cote_purge_apres_la_duree_de_garde()
    {
        Directory.CreateDirectory(dossier);
        var vieux = Path.Combine(dossier, "taches-laissees.illisible-20261101-080000.dat");
        var recent = Path.Combine(dossier, "taches-laissees.illisible-20261120-080000.dat");
        File.WriteAllText(vieux, "x");
        File.WriteAllText(recent, "x");
        File.SetLastWriteTime(vieux, maintenant.AddDays(-20));
        File.SetLastWriteTime(recent, maintenant.AddDays(-1));

        Fichier().Lire();

        Assert.False(File.Exists(vieux));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void Fichier_absent_liste_vide_sans_journal()
    {
        Assert.Empty(Fichier().Lire());
        Assert.Empty(journal);
    }

    // ---- Réglage de l'officine (script 156) : « chiffre » ou « desactive » ----

    [Fact]
    public void Desactive_aucune_ecriture_sur_le_poste()
    {
        var fichier = Fichier(ReglageTachesLaisseesDto.Desactive);

        fichier.Ecrire([Facture(), Retour()]);

        Assert.False(File.Exists(fichier.Chemin));
        Assert.False(File.Exists(fichier.Chemin + ".tmp"));
        Assert.False(Directory.Exists(dossier) && Directory.EnumerateFileSystemEntries(dossier).Any());
        Assert.Empty(journal);
    }

    [Fact]
    public void Passage_a_desactive_supprime_le_fichier_et_les_copies_mises_de_cote()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI : Windows seulement
        Fichier().Ecrire([Facture()]);
        var misDeCote = Path.Combine(dossier, "taches-laissees.illisible-20261120-080000.dat");
        File.WriteAllText(misDeCote, "x");
        var fichier = Fichier(mode: null);

        fichier.DefinirMode(ReglageTachesLaisseesDto.Desactive);

        Assert.False(File.Exists(fichier.Chemin));
        Assert.False(File.Exists(misDeCote));
        Assert.Contains("désactivé", Assert.Single(journal));
        Assert.Equal(ReglageTachesLaisseesDto.Desactive, fichier.Mode);
    }

    [Fact]
    public void Desactive_ne_relit_pas_le_fichier_et_le_supprime()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI : Windows seulement
        Fichier().Ecrire([Facture()]);

        var relues = Fichier(mode: null).Appliquer(ReglageTachesLaisseesDto.Desactive, []);

        Assert.Empty(relues);
        Assert.False(File.Exists(Fichier().Chemin));
    }

    [Fact]
    public void Fichier_reste_supprime_a_la_prochaine_ecriture_en_desactive()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI : Windows seulement
        var fichier = Fichier(ReglageTachesLaisseesDto.Desactive);
        // Fichier réapparu (ex. ancienne version du poste) : supprimé à la prochaine écriture
        Fichier().Ecrire([Retour()]);

        fichier.Ecrire([Facture()]);

        Assert.False(File.Exists(fichier.Chemin));
    }

    [Fact]
    public void Reglage_pas_encore_lu_rien_n_est_lu_ecrit_ni_supprime()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI : Windows seulement
        var facture = Facture();
        Fichier().Ecrire([facture]);
        var avant = File.ReadAllBytes(Fichier().Chemin);
        var inconnu = Fichier(mode: null);

        Assert.Empty(inconnu.Lire());
        inconnu.Ecrire([Retour()]);
        inconnu.Ecrire([]);

        Assert.Null(inconnu.Mode);
        Assert.Equal(avant, File.ReadAllBytes(inconnu.Chemin));
        Assert.Equal([facture], Fichier().Lire());
    }

    [Fact]
    public void Valeur_de_reglage_inconnue_ignoree_et_notee()
    {
        var fichier = Fichier(mode: null);

        fichier.DefinirMode("clair");
        fichier.Ecrire([Facture()]);

        Assert.Null(fichier.Mode);
        Assert.False(File.Exists(fichier.Chemin));
        Assert.Contains("inconnu", Assert.Single(journal));
    }

    [Fact]
    public void Chiffre_relit_une_seule_fois_et_garde_aussi_les_taches_laissees_avant()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI : Windows seulement
        var facture = Facture();
        Fichier().Ecrire([facture]);
        // Démarrage : réglage pas encore lu ; un retour est laissé sur le poste avant la première lecture
        var fichier = Fichier(mode: null);
        var retour = Retour();

        var ajout = fichier.Appliquer(ReglageTachesLaisseesDto.Chiffre, [retour]);

        Assert.Equal([facture], ajout);
        Assert.Equal([facture, retour], Fichier().Lire().OrderBy(t => t.Type).ToList());
        // Connexion suivante : le fichier n'est plus relu (la liste du poste fait foi), rien n'est ajouté deux fois
        Assert.Empty(fichier.Appliquer(ReglageTachesLaisseesDto.Chiffre, [retour, facture]));
    }

    [Fact]
    public void Retour_a_chiffre_apres_desactive_ecrit_de_nouveau_le_fichier_chiffre()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI : Windows seulement
        var fichier = Fichier(ReglageTachesLaisseesDto.Desactive);
        var facture = Facture("Zerrouki Samira");

        Assert.Empty(fichier.Appliquer(ReglageTachesLaisseesDto.Chiffre, [facture]));

        Assert.Equal([facture], Fichier().Lire());
        Assert.DoesNotContain("Zerrouki", Encoding.UTF8.GetString(File.ReadAllBytes(fichier.Chemin)));
    }
}
