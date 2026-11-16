using System.Text.RegularExpressions;
using Pharma.Infrastructure.Sauvegardes;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Mutualisation des réglages, lot M4 : les outils qui rejouent toute la suite des scripts (restauration depuis l'application, mise à jour d'une
/// officine, rejeu complet) doivent poser le marqueur de session « pharma.reglage_central = on » (PGOPTIONS), sinon la garde zz_reglage_garde du
/// script 141 refuse leurs écritures dès qu'une table de réglages est imposée par le serveur central (docs/scripts-base-de-donnees.md, règle 10).
/// </summary>
public partial class MarqueurRejeuReglagesTests(BaseJetableM4 jetable) : IClassFixture<BaseJetableM4>
{
    private const string Marqueur = "pharma.reglage_central=on";

    private static string Fichier(params string[] chemin) => Path.Combine([BaseJetableM4.RacineDepot(), .. chemin]);

    [Fact]
    public void Les_outils_qui_rejouent_la_suite_posent_le_marqueur_de_la_mutualisation()
    {
        Assert.Contains(Marqueur, RestaurationPostgres.OptionsScripts);
        Assert.Contains("client_min_messages=warning", RestaurationPostgres.OptionsScripts);
        foreach (var fichier in new[]
                 {
                     Fichier("scripts", "mep", "outils_mep.ps1"),             // mettre_a_jour_base_officine.ps1 passe par Invoke-OutilPg
                     Fichier("database", "tests", "rejeu_complet.ps1"),
                     Fichier("scripts", "initialiser_base_locale.ps1"),
                     Fichier("scripts", "paquet", "installer.ps1")
                 })
        {
            var lignes = File.ReadAllLines(fichier).Where(l => AffectationPgOptions().IsMatch(l)).ToList();
            Assert.True(lignes.Count > 0, $"{Path.GetFileName(fichier)} : aucune affectation de PGOPTIONS trouvée (le test ne vérifie plus rien).");
            Assert.Contains(lignes, l => l.Contains(Marqueur));
        }
        // mettre_a_jour_base_officine.ps1 lance psql par l'outil commun (aucune affectation propre de PGOPTIONS qui écraserait le marqueur)
        var mise = File.ReadAllLines(Fichier("scripts", "mep", "mettre_a_jour_base_officine.ps1"));
        Assert.Contains(mise, l => l.Contains("outils_mep.ps1"));
        Assert.DoesNotContain(mise, l => AffectationPgOptions().IsMatch(l));
    }

    [Fact]
    public void Rejeu_de_toute_la_suite_sur_une_copie_dont_une_table_est_imposee_reussit_avec_le_marqueur_et_echoue_sans()
    {
        if (!jetable.Disponible) return;
        // Copie neuve (database/init) : synchronisation active et officine inscrite ; les 11 tables imposées par le serveur central
        jetable.Executer(["-c", """
            SELECT set_config('pharma.reglage_central', 'on', false);
            UPDATE sync_parametre SET active = true, inscrite_le = now(), adresse_serveur = 'https://central.exemple:5090' WHERE id = 1;
            UPDATE reglage_mutualise SET mode = 'impose', mode_recu = 'impose', version_modele_recue = 1, version_modele_appliquee = 1, etat = 'applique', origine = 'central';
            """]);
        Assert.Equal("impose", ModeEffectif());

        var migration = Path.Combine(BaseJetableM4.RacineDepot(), "database", "migration");
        var scripts = Directory.GetFiles(migration, "*.sql").Where(f => NumeroScript().IsMatch(Path.GetFileName(f)))
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToList();
        int Numero(string f) => int.Parse(Path.GetFileName(f)[..3]);
        var lots = new (List<string> Fichiers, bool Transaction)[]
        {
            (scripts.Where(f => Numero(f) <= 32).ToList(), true),
            (scripts.Where(f => Numero(f) == 33).ToList(), false),
            (scripts.Where(f => Numero(f) >= 34).ToList(), true)
        }.Where(l => l.Fichiers.Count > 0).ToList();
        var temporaires = new List<string>();
        try
        {
            string Lot((List<string> Fichiers, bool Transaction) l)
            {
                var lignes = l.Fichiers.Select(f => $"\\i '{BaseJetableM4.Chemin(f).Replace("'", "''")}'").ToList();
                if (l.Transaction) { lignes.Insert(0, "BEGIN;"); lignes.Add("COMMIT;"); }
                var t = Path.Combine(Path.GetTempPath(), $"pharma_marqueur_{Guid.NewGuid():N}.sql");
                temporaires.Add(t);
                File.WriteAllLines(t, lignes);
                return t;
            }

            // Sans le marqueur : la garde refuse les scripts qui écrivent dans une table imposée
            var echec = lots.Select(l => jetable.Lancer(["-f", Lot(l)], "-c client_min_messages=warning")).FirstOrDefault(r => r.Code != 0);
            Assert.NotEqual(0, echec.Code);
            Assert.Contains("Réglage fixé par le serveur central", echec.Erreurs);

            // Avec les options des outils (RestaurationPostgres.OptionsScripts) : toute la suite passe, les tables restent imposées
            foreach (var l in lots)
            {
                var (code, erreurs) = jetable.Lancer(["-f", Lot(l)], RestaurationPostgres.OptionsScripts);
                Assert.True(code == 0, $"Rejeu refusé malgré le marqueur : {erreurs}");
            }
            Assert.Equal("impose", ModeEffectif());
        }
        finally
        {
            foreach (var t in temporaires) { try { File.Delete(t); } catch (IOException) { } }
        }
    }

    private string ModeEffectif()
    {
        using var cx = jetable.Source!.OpenConnection();
        using var cmd = new Npgsql.NpgsqlCommand("SELECT reglage_mode_effectif('parametre_inventaire')", cx);
        return (string)cmd.ExecuteScalar()!;
    }

    [GeneratedRegex(@"^\s*\$env:PGOPTIONS\s*=")]
    private static partial Regex AffectationPgOptions();

    [GeneratedRegex(@"^(\d{3})_.+\.sql$")]
    private static partial Regex NumeroScript();
}
