using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Npgsql;
using Pharma.Domain.Reglages;

namespace Pharma.Cloud.Tests;

/// <summary>
/// Garantie du défaut D-M8-1 : chaque élément « mutu_&lt;table&gt; » que cloud_descendants produit sur un modèle publié doit passer la validation du
/// domaine (ValidationModele.ValiderElement), c'est-à-dire être accepté par l'officine. Base centrale JETABLE « pharmacie_cloud_jetable_m11_… »
/// créée avec les scripts database/cloud (001 à la dernière), supprimée à la fin. Sans serveur PostgreSQL joignable, sans psql ou sans droit
/// CREATEDB, le test ne fait rien (comme les tests de base du projet d'architecture).
/// </summary>
public class DescendantsMutuV1Tests
{
    private static readonly (string Table, string Valeur)[] Modeles =
    [
        (TablesMutualisables.Fiscal, """{"conservation_annees":10,"format_export_dgi":"csv-v1","separateur_csv":";","fuseau":"Africa/Algiers"}"""),
        (TablesMutualisables.AlertesPsychotropes, """
            {"regles":[
              {"code":"patient_repete","active":true,"seuil":3,"periode_jours":30,"exiger_valideur":false,"unites_par_jour":null},
              {"code":"ordonnance_reutilisee","active":true,"seuil":2,"periode_jours":30,"exiger_valideur":true,"unites_par_jour":null},
              {"code":"quantite_anormale","active":true,"seuil":1,"periode_jours":1,"exiger_valideur":false,"unites_par_jour":4},
              {"code":"prescripteur_inhabituel","active":false,"seuil":5,"periode_jours":90,"exiger_valideur":false,"unites_par_jour":null},
              {"code":"prescripteur_hors_zone","active":true,"seuil":1,"periode_jours":1,"exiger_valideur":false,"unites_par_jour":null}]}
            """),
        (TablesMutualisables.Tracabilite, """{"boite_obligatoire":true,"patient_obligatoire":"psychotrope"}"""),
        (TablesMutualisables.Serialisation, """{"actif":true,"factice_hors_ligne":false}"""),
        (TablesMutualisables.Substitution, """{"equivalent_exact_seulement":true}"""),
        (TablesMutualisables.Motifs, """
            {"motifs":[
              {"liste":"tiroir","code":"c_000000000001","libelle":"Fonds de caisse","actif":true,"ordre":10,"effet":"entree","detail":"fond"},
              {"liste":"sortie_stock","code":"casse","libelle":"Casse","actif":true,"ordre":20,"effet":"aucun","detail":"aucun"}]}
            """),
        (TablesMutualisables.FormulaireSms, """{"texte":"Mon texte"}"""),
        (TablesMutualisables.Surveillance, """
            {"seuil_lignes_retirees":5,"seuil_tiroir":3,"seuil_attentes_abandonnees":3,"seuil_remises":5,"seuil_remises_patient":3,"seuil_ajustements":5,
             "seuil_casses":3,"seuil_echantillons":3,"jours_comptage_sensibles":7,"reception_aveugle":true,"jours_avoir_retour":30,
             "seuil_credit_nouveau_patient":5000,"jours_patient_recent":7,"seuil_credits_jour":20000,"jours_credit_sans_remboursement":60,
             "seuil_autorisations_valideur":10,"seuil_autorisations_employe":5,"minutes_postes_simultanes":10,"seuil_changements_poste":3,
             "seuil_baisses_prix":1,"seuil_sorties_tiroir":5}
            """),
        (TablesMutualisables.Alertes, """
            {"jours_orange":180,"jours_rouge":90,"horizon_risque_jours":365,"delai_fournisseur_jours":2,"jours_securite":7,
             "jours_cible_commande":30,"jours_dormant":90,"seuil_lot_delaisse":5,"seuil_lot_delaisse_collectif":10}
            """),
        (TablesMutualisables.Inventaire, """{"seuil_boites":2,"seuil_montant":2000.50}"""),
        (TablesMutualisables.Remises, """
            {"remises":[
              {"cle":"c_a1b2c3d4e5f6","categorie":"otc","pourcentage":10,"date_debut":"2026-12-01","date_fin":"2026-12-31","motif":"Fêtes"},
              {"cle":"c_a1b2c3d4e5f8","categorie":"parapharmacie","pourcentage":5,"date_debut":"2026-12-15","date_fin":"2027-01-15","motif":null}]}
            """)
    ];

    [Fact]
    public async Task Chaque_element_mutu_produit_par_cloud_descendants_est_accepte_par_la_validation_du_domaine()
    {
        Assert.Equal(TablesMutualisables.Toutes.Count, Modeles.Length);
        using var base_ = BaseCentraleJetable.Creer();
        if (base_ is null) return;   // pas de serveur, de psql ou de droit CREATEDB : rien à vérifier ici

        await using var db = NpgsqlDataSource.Create(base_.Connexion);
        var officine = Guid.NewGuid();
        await using (var ins = db.CreateCommand("INSERT INTO officine (id, libelle) VALUES ($1, 'Officine essai M8')"))
        {
            ins.Parameters.AddWithValue(officine);
            await ins.ExecuteNonQueryAsync();
        }

        foreach (var mode in new[] { "propose", "impose" })
        {
            foreach (var (table, valeur) in Modeles)
            {
                await using var pub = db.CreateCommand("SELECT cloud_publier_reglage($1, $2::jsonb, 'admin-test', $3)");
                pub.Parameters.AddWithValue(table);
                pub.Parameters.AddWithValue(valeur);
                pub.Parameters.AddWithValue(mode);
                await pub.ExecuteScalarAsync();
            }

            var elements = await Elements(db, officine);
            Assert.Equal(Modeles.Length, elements.Count);
            foreach (var (cle, json) in elements)
            {
                var table = cle[TablesMutualisables.PrefixeCle.Length..];
                var erreurs = ValidationModele.ValiderElement(table, json);
                Assert.True(erreurs.Count == 0, $"{cle} ({mode}) refusé par le domaine : {string.Join(" | ", erreurs)} ; élément : {json}");
                using var doc = JsonDocument.Parse(json);
                Assert.Equal(ValidationModele.PorteAjoutsLocaux(table), doc.RootElement.TryGetProperty("ajouts_locaux", out _));
            }
        }

        // Officine libérée : éléments en mode « local », eux aussi acceptés
        await using (var lib = db.CreateCommand("SELECT cloud_liberer_reglages($1, 'admin-test')"))
        {
            lib.Parameters.AddWithValue(officine);
            await lib.ExecuteScalarAsync();
        }
        var locaux = await Elements(db, officine);
        Assert.Equal(Modeles.Length, locaux.Count);
        foreach (var (cle, json) in locaux)
            Assert.Empty(ValidationModele.ValiderElement(cle[TablesMutualisables.PrefixeCle.Length..], json));
    }

    /// <summary>Éléments « mutu_* » que cloud_descendants renvoie à l'officine (appel avec le compte propriétaire, officine posée).</summary>
    private static async Task<List<(string Cle, string Json)>> Elements(NpgsqlDataSource db, Guid officine)
    {
        await using var cn = await db.OpenConnectionAsync();
        await using var tx = await cn.BeginTransactionAsync();
        await using (var pose = new NpgsqlCommand("SELECT set_config('app.officine_id', $1, true)", cn, tx))
        {
            pose.Parameters.AddWithValue(officine.ToString());
            await pose.ExecuteScalarAsync();
        }
        var liste = new List<(string, string)>();
        await using (var cmd = new NpgsqlCommand("SELECT cle, donnees::text FROM cloud_descendants(0) WHERE cle LIKE 'mutu\\_%' ORDER BY cle", cn, tx))
        await using (var lecteur = await cmd.ExecuteReaderAsync())
            while (await lecteur.ReadAsync()) liste.Add((lecteur.GetString(0), lecteur.GetString(1)));
        await tx.RollbackAsync();
        return liste;
    }

    /// <summary>
    /// Base centrale « pharmacie_cloud_jetable_m11_… » : scripts database/cloud passés par psql, supprimée à la fin. Partagée avec les
    /// tests du départ d'une officine (DepartOfficineTests).
    /// </summary>
    internal sealed class BaseCentraleJetable : IDisposable
    {
        private readonly string administration;
        private readonly string nom;
        public string Connexion { get; }

        private readonly string psql;
        private readonly NpgsqlConnectionStringBuilder modele;

        private BaseCentraleJetable(string administration, string nom, string connexion, string psql, NpgsqlConnectionStringBuilder modele)
            => (this.administration, this.nom, Connexion, this.psql, this.modele) = (administration, nom, connexion, psql, modele);

        /// <summary>Passe un script SQL sur cette base jetable (psql, ON_ERROR_STOP), comme à la création.</summary>
        public void Rejouer(string script) => Psql(psql, modele, nom, script);

        public static BaseCentraleJetable? Creer()
        {
            var psql = Outil("psql.exe");
            if (psql is null) return null;
            var racine = Depot.Racine();
            var modele = new NpgsqlConnectionStringBuilder(ChaineConnexion(racine));
            var nom = "pharmacie_cloud_jetable_m11_" + Guid.NewGuid().ToString("N")[..8];
            var administration = new NpgsqlConnectionStringBuilder(modele.ConnectionString) { Database = "postgres", Pooling = false }.ConnectionString;
            try
            {
                using var cx = new NpgsqlConnection(administration);
                cx.Open();
                using var creer = new NpgsqlCommand($"CREATE DATABASE \"{nom}\" ENCODING 'UTF8' TEMPLATE template0", cx);
                creer.ExecuteNonQuery();
            }
            catch (NpgsqlException) { return null; }
            var base_ = new BaseCentraleJetable(administration, nom, new NpgsqlConnectionStringBuilder(modele.ConnectionString) { Database = nom }.ConnectionString, psql, modele);
            try
            {
                foreach (var script in Directory.GetFiles(Path.Combine(racine, "database", "cloud"), "0*.sql").Order(StringComparer.Ordinal))
                    Psql(psql, modele, nom, script);
            }
            catch
            {
                base_.Dispose();
                throw;
            }
            return base_;
        }

        private static void Psql(string psql, NpgsqlConnectionStringBuilder cnx, string nom, string script)
        {
            var info = new ProcessStartInfo(psql)
            {
                UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var a in new[] { "--no-password", $"--host={cnx.Host}", $"--port={cnx.Port}", $"--username={cnx.Username}", $"--dbname={nom}",
                         "-v", "ON_ERROR_STOP=1", "-q", "-f", script.Replace('\\', '/') })
                info.ArgumentList.Add(a);
            info.Environment["PGPASSWORD"] = cnx.Password ?? "";
            info.Environment["PGCLIENTENCODING"] = "UTF8";
            info.Environment["PGOPTIONS"] = "-c client_min_messages=warning";
            using var p = Process.Start(info) ?? throw new InvalidOperationException("psql n'a pas pu démarrer.");
            var sortie = p.StandardOutput.ReadToEndAsync();
            var erreurs = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            _ = sortie.Result;
            Assert.True(p.ExitCode == 0, $"psql a échoué sur {Path.GetFileName(script)} : {erreurs.Result.Trim()}");
        }

        public void Dispose()
        {
            NpgsqlConnection.ClearAllPools();
            try
            {
                using var cx = new NpgsqlConnection(administration);
                cx.Open();
                using var supprimer = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{nom}\" WITH (FORCE)", cx);
                supprimer.ExecuteNonQuery();
            }
            catch (NpgsqlException) { /* base restée : supprimable à la main (préfixe pharmacie_cloud_jetable_m11_) */ }
        }

        private static string? Outil(string fichier)
        {
            if (Environment.GetEnvironmentVariable("PHARMA_PG_BIN") is { Length: > 0 } bin && File.Exists(Path.Combine(bin, fichier))) return Path.Combine(bin, fichier);
            var racine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
            return Directory.Exists(racine)
                ? Directory.GetDirectories(racine).Select(d => Path.Combine(d, "bin", fichier)).Where(File.Exists)
                    .OrderByDescending(f => int.TryParse(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f))), out var v) ? v : 0)
                    .FirstOrDefault()
                : null;
        }

        private static string ChaineConnexion(string racine)
        {
            if (Environment.GetEnvironmentVariable("PHARMA_TEST_CNX") is { Length: > 0 } cnx) return cnx;
            var dossierApi = Path.Combine(racine, "src", "Pharma.Api");
            var appsettings = Path.Combine(dossierApi, "appsettings.Development.json");
            if (!File.Exists(appsettings)) appsettings = Path.Combine(dossierApi, "appsettings.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(appsettings), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.GetProperty("ConnectionStrings").GetProperty("Pharmacie").GetString()!;
        }
    }
}
