namespace Pharma.Architecture.Tests;

/// <summary>
/// Domaine Patients (2026-11-18, docs/navigation.md) : Fichier des patients en premier, Droits expirés ou bientôt expirés, Patients en
/// attente (déplacés d'Achats). Mêmes droits que la fiche patient et que l'ancien onglet : droit de vendre.
/// </summary>
public class DomainePatientsTests
{
    private static string Lire(params string[] chemin) => File.ReadAllText(Path.Combine([RacineDepot(), "src", "Pharma.Desktop", .. chemin]));

    [Fact]
    public void Le_domaine_patients_liste_ses_trois_ecrans_dans_l_ordre_avec_le_droit_de_vendre()
    {
        var domaines = Lire("Services", "Domaines.cs");
        var fichier = domaines.IndexOf("new(\"Fichier des patients\", \"patients\", [\"patients\"], [\"caisse.vendre\"])", StringComparison.Ordinal);
        var droits = domaines.IndexOf("new(\"Droits expirés ou bientôt expirés\", \"patients/droits-chifa\", [\"patients/droits-chifa\"], [\"caisse.vendre\"])", StringComparison.Ordinal);
        var attente = domaines.IndexOf("new(\"Patients en attente\", \"patients/attente\", [\"patients/attente\"], [\"caisse.vendre\"])", StringComparison.Ordinal);
        Assert.True(fichier > 0 && droits > fichier && attente > droits, "Domaine Patients : Fichier, Droits Chifa, Patients en attente, dans cet ordre");
    }

    [Fact]
    public void Les_ecrans_du_domaine_existent_avec_leur_route()
    {
        Assert.StartsWith("@page \"/patients\"", Lire("Composants", "Pages", "FichierPatients.razor"));
        var attente = Lire("Composants", "Pages", "PatientsAttente.razor");
        Assert.StartsWith("@page \"/patients/attente\"", attente);
        Assert.Contains("<OngletPatientsAttente />", attente);
    }

    [Fact]
    public void Achats_n_a_plus_l_onglet_patients_en_attente_et_l_ancienne_adresse_y_renvoie()
    {
        var achats = Lire("Composants", "Pages", "Achats.razor");
        Assert.DoesNotContain("<OngletPatientsAttente", achats);
        Assert.DoesNotContain("new(\"patients-attente\"", achats);
        Assert.Contains("if (Onglet == \"patients-attente\") Nav.NavigateTo(\"patients/attente\", replace: true);", achats);
        // Achats › Ruptures garde le nombre de patients en attente
        Assert.Contains("Patients en attente", Lire("Composants", "Achats", "OngletRuptures.razor"));
    }

    [Fact]
    public void Le_domaine_est_reserve_au_droit_de_vendre_comme_l_api()
    {
        // Liste des écrans réservés : AccesEcrans (2026-11-21), lue par la garde de la mise en page et par les tuiles navigables
        Assert.Contains("(\"patients\", [\"caisse.vendre\"])", Lire("Composants", "Mise", "AccesEcrans.cs"));
        var api = File.ReadAllText(Path.Combine(RacineDepot(), "src", "Pharma.Api", "Endpoints", "PatientsEndpoints.cs"));
        var route = api[api.IndexOf("\"/patients/fichier\"", StringComparison.Ordinal)..];
        Assert.Contains(".ExigerPermission(Permissions.CaisseVendre)", route[..route.IndexOf("api.Map", StringComparison.Ordinal)]);
    }

    private static string RacineDepot()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } racine) return racine;
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }
}
