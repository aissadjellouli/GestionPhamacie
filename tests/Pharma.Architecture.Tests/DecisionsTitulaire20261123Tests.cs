using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>Décisions du titulaire du 2026-11-23 : déconnexion après inactivité à 15 minutes par défaut, écran de connexion sans route ouverte.</summary>
public class DecisionsTitulaire20261123Tests
{
    private static string Racine()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx"))) dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    private static string Lire(params string[] chemin) => File.ReadAllText(Path.Combine([Racine(), .. chemin]));

    [Fact]
    public void Deconnexion_apres_inactivite_15_minutes_par_defaut_pour_une_installation_neuve()
    {
        var schema = Lire("database", "init", "schema.sql");
        Assert.Matches(new Regex(@"deconnexion_minutes integer DEFAULT 15 NOT NULL"), schema);
        Assert.Contains("SET DEFAULT 15", Lire("database", "migration", "162_deconnexion_inactivite_defaut_15_minutes.sql"));
    }

    [Fact]
    public void Retours_arriere_hors_de_la_suite_numerotee()
    {
        var migration = Path.Combine(Racine(), "database", "migration");
        Assert.True(File.Exists(Path.Combine(migration, "retour", "162_deconnexion_inactivite_defaut_15_minutes_retour.sql")));
        // Les outils (Get-ScriptsNumerotes) lisent seulement le dossier lui-même : aucun script de retour n'y est déposé
        Assert.DoesNotContain(Directory.GetFiles(migration, "*.sql").Select(Path.GetFileName),
            n => int.Parse(n![..3].TrimEnd('b'), System.Globalization.CultureInfo.InvariantCulture) >= 162 && n.EndsWith("_retour.sql", StringComparison.Ordinal));
    }

    [Fact]
    public void Connexion_sans_route_ouverte_deux_onglets_et_dernier_choix_retenu_sur_le_poste()
    {
        Assert.DoesNotContain("/api/auth/comptoir", Lire("src", "Pharma.Api", "Endpoints", "AuthEndpoints.cs"));
        Assert.DoesNotContain("ComptoirAsync()", Lire("src", "Pharma.Desktop", "Services", "ApiClient.cs"));
        Assert.DoesNotContain("record ComptoirDto(", Lire("src", "Pharma.Shared", "Dtos.cs"));

        var page = Lire("src", "Pharma.Desktop", "Composants", "Pages", "Connexion.razor");
        Assert.Contains("Code comptoir</button>", page);
        Assert.Contains("Mot de passe</button>", page);
        Assert.DoesNotContain("caisseOuverte", page);
        Assert.Contains("Reglages.Actuelles.OngletConnexion == \"code\"", page);   // « Mot de passe » sinon
        Assert.Contains("Reglages.Enregistrer(", page);                              // préférence du poste (poste.json), pas la base
    }
}
