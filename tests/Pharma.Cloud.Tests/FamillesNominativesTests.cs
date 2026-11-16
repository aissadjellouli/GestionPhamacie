using System.Text.RegularExpressions;
using Pharma.Shared;

namespace Pharma.Cloud.Tests;

/// <summary>
/// Familles de données nominatives (2026-11-16) : même liste de colonnes dans Pharma.Shared, dans le script officine 130
/// (sync_famille_colonne) et dans le script central 004 (cloud_famille_colonne), mêmes défauts au central, colonnes envoyées.
/// </summary>
public class FamillesNominativesTests
{
    private static readonly Regex Ligne = new(@"\('(?<f>[a-z_]+)',\s*'(?<t>[a-z_]+)',\s*'(?<c>[a-z_]+)'\)", RegexOptions.Compiled);

    private static SortedSet<string> Colonnes(string script, string table)
    {
        // Toutes les insertions (un script peut compléter les familles d'un précédent : 130 puis 133, 004 puis 007)
        var blocs = Regex.Matches(script, $@"INSERT INTO {table} \(famille, nom_table, colonne\) VALUES(?<v>.*?)ON CONFLICT", RegexOptions.Singleline);
        Assert.True(blocs.Count > 0, $"insertion dans {table} introuvable");
        return new(blocs.SelectMany(b => Ligne.Matches(b.Groups["v"].Value)).Select(m => $"{m.Groups["f"].Value}:{m.Groups["t"].Value}.{m.Groups["c"].Value}"), StringComparer.Ordinal);
    }

    private static SortedSet<string> Attendues() =>
        new(FamillesNominativesSync.Familles.SelectMany(f => f.Value.Colonnes.Select(c => $"{f.Key}:{c}")), StringComparer.Ordinal);

    [Fact]
    public void Meme_liste_dans_l_officine_le_central_et_le_code()
    {
        var racine = Depot.Racine();
        var officine = Lire(racine, "migration", "130_synchronisation_familles_nominatives.sql", "133_synchronisation_familles_banque_clients.sql", "135_synchronisation_famille_banque_elargie.sql");
        var central = Lire(racine, "cloud", "004_reglages.sql", "007_familles_banque_clients.sql", "008_famille_banque_elargie.sql");
        Assert.Equal(Attendues(), Colonnes(officine, "sync_famille_colonne"));
        Assert.Equal(Attendues(), Colonnes(central, "cloud_famille_colonne"));
        var seed = File.ReadAllText(Path.Combine(racine, "database", "init", "seed_referentiel.sql"));
        Assert.Equal(Attendues(), Colonnes(seed, "sync_famille_colonne"));
    }

    [Fact]
    public void Defauts_du_titulaire_employes_non_les_autres_oui()
    {
        var central = Lire(Depot.Racine(), "cloud", "004_reglages.sql", "007_familles_banque_clients.sql", "008_famille_banque_elargie.sql");
        foreach (var (famille, f) in FamillesNominativesSync.Familles)
        {
            Assert.Matches($@"\('{famille}',\s*'[^']+',\s*{(f.Defaut ? "true" : "false")}\)", central);
        }
        Assert.False(FamillesNominativesSync.Familles[FamillesNominativesSync.Employes].Defaut);
        Assert.False(FamillesNominativesSync.Familles[FamillesNominativesSync.Banque].Defaut);
        Assert.False(FamillesNominativesSync.Familles[FamillesNominativesSync.ClientsFactures].Defaut);
    }

    private static string Lire(string racine, string dossier, params string[] fichiers) =>
        string.Join("\n", fichiers.Select(f => File.ReadAllText(Path.Combine(racine, "database", dossier, f))));

    [Fact]
    public void Chaque_colonne_de_famille_est_une_colonne_envoyee_au_central()
    {
        var miroir = File.ReadAllText(Path.Combine(Depot.Racine(), "database", "cloud", "002_tables_miroir.sql"));
        foreach (var c in FamillesNominativesSync.Familles.SelectMany(f => f.Value.Colonnes))
        {
            var (table, colonne) = (c.Split('.')[0], c.Split('.')[1]);
            Assert.Contains($"ALTER TABLE public.\"{table}\" ADD COLUMN IF NOT EXISTS \"{colonne}\"", miroir);
        }
    }
}
