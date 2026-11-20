using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Reglages;
using Pharma.Domain.Commun;
using Pharma.Domain.Referentiels;
using Pharma.Domain.Reglages;

namespace Pharma.Infrastructure.Reglages;

/// <summary>
/// Mutualisation des réglages, lot M4 (script 141) : état des 11 tables (reglage_mutualise) et application d'un modèle du serveur central dans UNE
/// transaction par table : verrou de la ligne d'état (SELECT … FOR UPDATE), marqueur de session « pharma.reglage_central = on » (SET LOCAL, seul
/// endroit qui le pose : il lève la garde zz_reglage_garde), écriture de la valeur, état, journal d'audit. Les noms de tables et de colonnes viennent de
/// listes fermées de ce fichier (jamais d'une donnée reçue) ; les valeurs passent toujours par des paramètres.
/// </summary>
public sealed class ReglagesMutualisesSql(NpgsqlDataSource db) : IReglagesMutualisesDepot, ILectureValeurReglage
{
    private const string ColonnesEtat = """
        nom_table, mode, ajouts_locaux, mode_recu, version_modele_recue, modele::text, version_modele_appliquee, applique_le, etat, motif_rejet,
        version_modele_rejetee, origine, modifie_localement_le, recu_le
        """;

    internal static EtatReglage LireEtat(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetBoolean(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetInt64(6), r.IsDBNull(7) ? null : r.GetDateTime(7), r.GetString(8),
        r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetInt64(10), r.GetString(11), r.IsDBNull(12) ? null : r.GetDateTime(12),
        r.IsDBNull(13) ? null : r.GetDateTime(13));

    public async Task<bool> ApplicationPossibleAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT coalesce(p.active AND p.inscrite_le IS NOT NULL AND NOT pg_is_in_recovery(), false) FROM sync_parametre p WHERE p.id = 1");
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    public async Task<(string Mode, bool AjoutsLocaux)> ModeEffectifAsync(string table, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT reglage_mode_effectif($1), coalesce((SELECT r.ajouts_locaux FROM reglage_mutualise r WHERE r.nom_table = $1), true)
            """);
        cmd.Parameters.AddWithValue(table);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetString(0), r.GetBoolean(1));
    }

    public async Task<IReadOnlyList<EtatReglage>> EtatsAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {ColonnesEtat} FROM reglage_mutualise ORDER BY nom_table");
        var liste = new List<EtatReglage>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(LireEtat(r));
        return liste;
    }

    public async Task<ITransactionReglage> OuvrirAsync(string table, CancellationToken ct = default)
    {
        if (!TablesMutualisables.Connue(table)) throw new ArgumentException("Table non mutualisable.", nameof(table));
        var cx = await db.OpenConnectionAsync(ct);
        try
        {
            var tx = await cx.BeginTransactionAsync(ct);
            return new Transaction(table, cx, tx);
        }
        catch
        {
            await cx.DisposeAsync();
            throw;
        }
    }

    /// <summary>Valeur actuelle de l'officine (lecture seule, sans verrou ni marqueur : la transaction est annulée à la fermeture).</summary>
    public async Task<string> ValeurLocaleAsync(string table, string valeurModeleJson, CancellationToken ct = default)
    {
        if (!TablesMutualisables.Connue(table)) throw new ArgumentException("Table non mutualisable.", nameof(table));
        var cx = await db.OpenConnectionAsync(ct);
        try
        {
            var tx = await cx.BeginTransactionAsync(ct);
            await using var t = new Transaction(table, cx, tx);
            return await t.LireValeurAsync(valeurModeleJson, ct);
        }
        catch
        {
            await cx.DisposeAsync();
            throw;
        }
    }

    public async Task JournaliserAsync(string action, string table, string modeAvant, string modeApres, long? version, string? auteur, long? utilisateurId,
        CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await Transaction.JournaliserSqlAsync(cx, null, action, table, modeAvant, modeApres, version, auteur, null, null, utilisateurId, ct);
    }

    // ---- Colonnes propres à chaque table à ligne unique (noms de la base ; liste fermée) ----

    private static readonly Dictionary<string, string[]> ColonnesLigneUnique = new(StringComparer.Ordinal)
    {
        [TablesMutualisables.Fiscal] = ["conservation_annees", "format_export_dgi", "separateur_csv", "fuseau"],
        [TablesMutualisables.Tracabilite] = ["boite_obligatoire", "patient_obligatoire"],
        [TablesMutualisables.Serialisation] = ["actif", "factice_hors_ligne"],
        [TablesMutualisables.Substitution] = ["equivalent_exact_seulement"],
        [TablesMutualisables.FormulaireSms] = ["texte"],
        // Les horaires d'ouverture (heure_ouverture, heure_fermeture) restent locaux (D7) : hors modèle
        [TablesMutualisables.Surveillance] =
        [
            "seuil_lignes_retirees", "seuil_tiroir", "seuil_attentes_abandonnees", "seuil_remises", "seuil_remises_patient", "seuil_ajustements",
            "seuil_casses", "seuil_echantillons", "jours_comptage_sensibles", "reception_aveugle", "jours_avoir_retour", "seuil_credit_nouveau_patient",
            "jours_patient_recent", "seuil_credits_jour", "jours_credit_sans_remboursement", "seuil_autorisations_valideur", "seuil_autorisations_employe",
            "minutes_postes_simultanes", "seuil_changements_poste", "seuil_baisses_prix", "seuil_sorties_tiroir"
        ],
        [TablesMutualisables.Alertes] =
        [
            "jours_orange", "jours_rouge", "horizon_risque_jours", "delai_fournisseur_jours", "jours_securite", "jours_cible_commande", "jours_dormant",
            "seuil_lot_delaisse", "seuil_lot_delaisse_collectif"
        ],
        [TablesMutualisables.Inventaire] = ["seuil_boites", "seuil_montant"]
    };

    /// <summary>Tables à ligne unique qui ont une colonne modifie_le.</summary>
    private static readonly HashSet<string> AvecModifieLe = [TablesMutualisables.Serialisation, TablesMutualisables.Substitution, TablesMutualisables.FormulaireSms];

    private sealed class Transaction(string table, NpgsqlConnection cx, NpgsqlTransaction tx) : ITransactionReglage
    {
        private bool marqueurPose;

        private NpgsqlCommand Commande(string sql) => new(sql, cx, tx);

        private static NpgsqlParameter Jsonb(string json) => new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = json };

        private async Task PoserMarqueurAsync(CancellationToken ct)
        {
            if (marqueurPose) return;
            // Marqueur de session limité à cette transaction : il lève la garde zz_reglage_garde (script 141) pour les écritures de ce lot
            await using var cmd = Commande("SELECT set_config('pharma.reglage_central', 'on', true)");
            await cmd.ExecuteScalarAsync(ct);
            marqueurPose = true;
        }

        public async Task<EtatReglage> EtatAsync(CancellationToken ct = default)
        {
            await using var cmd = Commande($"SELECT {ColonnesEtat} FROM reglage_mutualise WHERE nom_table = $1 FOR UPDATE");
            cmd.Parameters.AddWithValue(table);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new InvalidOperationException($"État de la table {table} absent de reglage_mutualise (script 141).");
            return LireEtat(r);
        }

        // ---- Lecture de la valeur de l'officine ----

        public async Task<string> LireValeurAsync(string valeurModeleJson, CancellationToken ct = default)
        {
            using var modele = JsonDocument.Parse(valeurModeleJson);
            JsonNode valeur = table switch
            {
                TablesMutualisables.AlertesPsychotropes => await ReglesPsychotropesAsync(ct),
                TablesMutualisables.Motifs => await MotifsAsync(modele.RootElement, ct),
                TablesMutualisables.Remises => await RemisesCentralesAsync(ct),
                _ => await LigneUniqueAsync(ct)
            };
            return valeur.ToJsonString();
        }

        private async Task<JsonNode> LigneUniqueAsync(CancellationToken ct)
        {
            await using var cmd = Commande($"SELECT to_jsonb(t)::text FROM {table} t WHERE t.id = 1");
            var json = await cmd.ExecuteScalarAsync(ct) as string;
            var resultat = new JsonObject();
            if (json is null) return resultat;
            var ligne = JsonNode.Parse(json)!.AsObject();
            foreach (var c in ColonnesLigneUnique[table]) resultat[c] = ligne[c]?.DeepClone();
            return resultat;
        }

        private async Task<JsonNode> ReglesPsychotropesAsync(CancellationToken ct)
        {
            await using var cmd = Commande("""
                SELECT code, active, seuil, periode_jours, exiger_valideur, unites_par_jour FROM regle_alerte_psychotrope ORDER BY code
                """);
            var regles = new JsonArray();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                regles.Add(new JsonObject
                {
                    ["code"] = r.GetString(0), ["active"] = r.GetBoolean(1), ["seuil"] = r.GetInt32(2), ["periode_jours"] = r.IsDBNull(3) ? null : r.GetInt32(3),
                    ["exiger_valideur"] = r.GetBoolean(4), ["unites_par_jour"] = r.IsDBNull(5) ? null : r.GetInt32(5)
                });
            return new JsonObject { ["regles"] = regles };
        }

        private async Task<IReadOnlyList<(MotifReglable Motif, bool Central)>> TousLesMotifsAsync(CancellationToken ct)
        {
            await using var cmd = Commande("SELECT id, liste, code, libelle, actif, systeme, ordre, effet, detail, modele_code_central FROM motif_reglable ORDER BY liste, code");
            var liste = new List<(MotifReglable, bool)>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                liste.Add((new MotifReglable(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5), r.GetInt32(6),
                    r.GetString(7), r.GetString(8), r.GetBoolean(9)), r.GetBoolean(9)));
            return liste;
        }

        private static HashSet<(string Liste, string Code)> ClesMotifs(JsonElement modele) =>
            modele.GetProperty("motifs").EnumerateArray().Select(m => (m.GetProperty("liste").GetString()!, m.GetProperty("code").GetString()!)).ToHashSet();

        /// <summary>Les motifs de l'officine qui correspondent à ceux du modèle (même liste et même code) : les autres sont locaux et ne comptent pas.</summary>
        private async Task<JsonNode> MotifsAsync(JsonElement modele, CancellationToken ct)
        {
            var cles = ClesMotifs(modele);
            var motifs = new JsonArray();
            foreach (var (m, _) in await TousLesMotifsAsync(ct))
                if (cles.Contains((m.Liste, m.Code)))
                    motifs.Add(new JsonObject
                    {
                        ["liste"] = m.Liste, ["code"] = m.Code, ["libelle"] = m.Libelle, ["actif"] = m.Actif, ["ordre"] = m.Ordre, ["effet"] = m.Effet, ["detail"] = m.Detail
                    });
            return new JsonObject { ["motifs"] = motifs };
        }

        /// <summary>Remises du serveur central en vigueur (non arrêtées).</summary>
        private async Task<JsonNode> RemisesCentralesAsync(CancellationToken ct)
        {
            await using var cmd = Commande("""
                SELECT modele_cle, categorie, pourcentage, date_debut, date_fin, motif FROM remise_automatique
                WHERE modele_cle IS NOT NULL AND arretee_le IS NULL ORDER BY modele_cle
                """);
            var remises = new JsonArray();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                remises.Add(new JsonObject
                {
                    ["cle"] = r.GetString(0), ["categorie"] = r.GetString(1), ["pourcentage"] = r.GetDecimal(2),
                    ["date_debut"] = r.GetFieldValue<DateOnly>(3).ToString("yyyy-MM-dd"), ["date_fin"] = r.GetFieldValue<DateOnly>(4).ToString("yyyy-MM-dd"),
                    ["motif"] = r.IsDBNull(5) ? null : r.GetString(5)
                });
            return new JsonObject { ["remises"] = remises };
        }

        // ---- Contrôles qui demandent la base ----

        public async Task<IReadOnlyList<string>> ControlerAsync(string valeurModeleJson, bool impose, CancellationToken ct = default)
        {
            var erreurs = new List<string>();
            using var doc = JsonDocument.Parse(valeurModeleJson);
            if (table == TablesMutualisables.Motifs) await ControlerMotifsAsync(doc.RootElement, erreurs, ct);
            else if (table == TablesMutualisables.Remises) await ControlerRemisesAsync(valeurModeleJson, impose, erreurs, ct);
            return erreurs;
        }

        private async Task ControlerMotifsAsync(JsonElement modele, List<string> erreurs, CancellationToken ct)
        {
            var existants = await TousLesMotifsAsync(ct);
            var cles = ClesMotifs(modele);
            foreach (var m in modele.GetProperty("motifs").EnumerateArray())
            {
                var liste = m.GetProperty("liste").GetString()!;
                var code = m.GetProperty("code").GetString()!;
                var libelle = m.GetProperty("libelle").GetString()!.Trim();
                var actif = m.GetProperty("actif").GetBoolean();
                var nom = $"Motif « {code} » ({liste})";
                var existant = existants.Select(e => e.Motif).FirstOrDefault(e => e.Liste == liste && e.Code == code);
                if (existant is not null)
                {
                    // Un motif système ne se désactive jamais ; l'effet du tiroir est fixé à la création
                    try { RegleMotifs.VerifierModification(existant, actif); }
                    catch (RegleMetierException e) { erreurs.Add($"{nom} : {e.Message}"); }
                    if (existant.Effet != m.GetProperty("effet").GetString() || existant.Detail != m.GetProperty("detail").GetString())
                        erreurs.Add($"{nom} : l’effet sur le tiroir est fixé à la création du motif, il ne se change pas.");
                }
                else if (!RegleMotifs.EstCodeCentral(code))
                    erreurs.Add($"{nom} : ce code n’existe pas dans l’officine (seuls les motifs nouveaux du serveur central, de code « c_… », sont créés).");

                // Libellé déjà pris par un motif de l'officine qui n'est pas dans le modèle (motif local)
                var locaux = existants.Select(e => e.Motif).Where(e => e.Liste == liste && !cles.Contains((e.Liste, e.Code))).Select(e => e.Libelle);
                try { RegleMotifs.ExigerLibelleUnique(libelle, locaux); }
                catch (RegleMetierException) { erreurs.Add($"{nom} : le libellé « {libelle} » est déjà pris par un motif de l’officine dans cette liste."); }
            }
        }

        private async Task ControlerRemisesAsync(string valeur, bool impose, List<string> erreurs, CancellationToken ct)
        {
            await using (var cmd = Commande("""
                SELECT x.cle, x.categorie FROM jsonb_to_recordset($1::jsonb -> 'remises') AS x(cle text, categorie text)
                WHERE NOT EXISTS (SELECT 1 FROM categorie_produit c WHERE c.code = x.categorie) ORDER BY x.cle
                """))
            {
                cmd.Parameters.Add(Jsonb(valeur));
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) erreurs.Add($"Remise « {r.GetString(0)} » : la catégorie « {r.GetString(1)} » n’existe pas dans l’officine.");
            }
            // Mode imposé : les remises locales en cours sont arrêtées (D6), il n'y a donc rien à comparer ; mode proposé : un chevauchement refuse la table
            if (impose) return;
            await using var chevauche = Commande("""
                SELECT DISTINCT x.cle, x.categorie
                FROM jsonb_to_recordset($1::jsonb -> 'remises') AS x(cle text, categorie text, date_debut date, date_fin date)
                JOIN remise_automatique l ON l.modele_cle IS NULL AND l.arretee_le IS NULL AND l.categorie = x.categorie
                                          AND l.date_debut <= x.date_fin AND l.date_fin >= x.date_debut AND l.date_fin >= current_date
                WHERE x.date_fin >= current_date ORDER BY x.cle
                """);
            chevauche.Parameters.Add(Jsonb(valeur));
            await using var rc = await chevauche.ExecuteReaderAsync(ct);
            while (await rc.ReadAsync(ct))
                erreurs.Add($"Remise « {rc.GetString(0)} » : une remise locale de la catégorie « {rc.GetString(1)} » couvre déjà une partie de cette période (arrêtez-la d’abord).");
        }

        // ---- Écriture ----

        public async Task<ResultatEcriture> EcrireAsync(string valeurModeleJson, bool impose, bool identique, CancellationToken ct = default)
        {
            await PoserMarqueurAsync(ct);
            switch (table)
            {
                case TablesMutualisables.AlertesPsychotropes:
                    if (!identique) await EcrirePsychotropesAsync(valeurModeleJson, ct);
                    return new ResultatEcriture(0, null);
                case TablesMutualisables.Motifs:
                    await EcrireMotifsAsync(valeurModeleJson, ct);
                    return new ResultatEcriture(0, null);
                case TablesMutualisables.Remises:
                    return await EcrireRemisesAsync(valeurModeleJson, impose, ct);
                default:
                    if (!identique) await EcrireLigneUniqueAsync(valeurModeleJson, ct);
                    return new ResultatEcriture(0, null);
            }
        }

        private async Task EcrireLigneUniqueAsync(string valeur, CancellationToken ct)
        {
            var permises = ColonnesLigneUnique[table];
            using var doc = JsonDocument.Parse(valeur);
            var colonnes = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
            var inconnues = colonnes.Where(c => !permises.Contains(c)).ToList();
            if (inconnues.Count > 0) throw new InvalidOperationException($"Colonne inattendue pour {table} : {inconnues[0]}.");
            if (colonnes.Count == 0) return;
            var affectations = string.Join(", ", colonnes.Select(c => $"{c} = x.{c}")) + (AvecModifieLe.Contains(table) ? ", modifie_le = now()" : "");
            await using (var cmd = Commande($"UPDATE {table} u SET {affectations} FROM jsonb_populate_record(NULL::{table}, $1::jsonb) x WHERE u.id = 1"))
            {
                cmd.Parameters.Add(Jsonb(valeur));
                if (await cmd.ExecuteNonQueryAsync(ct) > 0) return;
            }
            // Ligne absente (base ancienne) : créée avec les colonnes du modèle, les autres gardent leur valeur par défaut
            await using var ajout = Commande($"INSERT INTO {table} (id, {string.Join(", ", colonnes)}) SELECT 1, {string.Join(", ", colonnes.Select(c => "x." + c))} FROM jsonb_populate_record(NULL::{table}, $1::jsonb) x");
            ajout.Parameters.Add(Jsonb(valeur));
            await ajout.ExecuteNonQueryAsync(ct);
        }

        private async Task EcrirePsychotropesAsync(string valeur, CancellationToken ct)
        {
            await using var cmd = Commande("""
                UPDATE regle_alerte_psychotrope r SET active = x.active, seuil = x.seuil, periode_jours = x.periode_jours,
                       exiger_valideur = x.exiger_valideur, unites_par_jour = x.unites_par_jour, modifie_le = now(), modifie_par = NULL
                FROM jsonb_to_recordset($1::jsonb -> 'regles') AS x(code text, active boolean, seuil int, periode_jours int, exiger_valideur boolean, unites_par_jour int)
                WHERE r.code = x.code
                """);
            cmd.Parameters.Add(Jsonb(valeur));
            await cmd.ExecuteNonQueryAsync(ct);
            // Règle absente de la base (installation neuve depuis un init antérieur au 2026-11-20, qui n'en semait qu'une) : créée avec son libellé et son rang d'affichage
            using var doc = JsonDocument.Parse(valeur);
            foreach (var r in doc.RootElement.GetProperty("regles").EnumerateArray())
            {
                var code = r.GetProperty("code").GetString()!;
                await using var ajout = Commande("""
                    INSERT INTO regle_alerte_psychotrope (code, libelle, active, seuil, periode_jours, exiger_valideur, unites_par_jour, ordre)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8) ON CONFLICT (code) DO NOTHING
                    """);
                ajout.Parameters.AddWithValue(code);
                ajout.Parameters.AddWithValue(RegleAlertesPsychotropes.Nom(code));
                ajout.Parameters.AddWithValue(r.GetProperty("active").GetBoolean());
                ajout.Parameters.AddWithValue(r.GetProperty("seuil").GetInt32());
                ajout.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = r.GetProperty("periode_jours").ValueKind == JsonValueKind.Null ? DBNull.Value : r.GetProperty("periode_jours").GetInt32() });
                ajout.Parameters.AddWithValue(r.GetProperty("exiger_valideur").GetBoolean());
                ajout.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = r.GetProperty("unites_par_jour").ValueKind == JsonValueKind.Null ? DBNull.Value : r.GetProperty("unites_par_jour").GetInt32() });
                ajout.Parameters.AddWithValue((short)(Array.IndexOf(OrdreRegles, code) + 1));
                await ajout.ExecuteNonQueryAsync(ct);
            }
        }

        /// <summary>Rang d'affichage des 6 règles (comme les scripts 117, 118 puis 153).</summary>
        private static readonly string[] OrdreRegles = ["patient_repete", "ordonnance_reutilisee", "quantite_anormale", "prescripteur_inhabituel", "prescripteur_hors_zone", "traitement_en_cours"];

        private const string RecordsetMotifs = "jsonb_to_recordset($1::jsonb -> 'motifs') AS x(liste text, code text, libelle text, actif boolean, ordre int, effet text, detail text)";

        private async Task EcrireMotifsAsync(string valeur, CancellationToken ct)
        {
            // 1. Motifs du modèle déjà présents (codes fixes des motifs système, motifs « c_ » déjà créés) : libellé, activation, ordre ; repérés « du serveur central »
            await using (var cmd = Commande($"""
                UPDATE motif_reglable m SET libelle = btrim(x.libelle), actif = x.actif OR m.systeme, ordre = x.ordre, modele_code_central = true, modifie_le = now()
                FROM {RecordsetMotifs}
                WHERE m.liste = x.liste AND m.code = x.code
                  AND (m.libelle, m.actif, m.ordre, m.modele_code_central) IS DISTINCT FROM (btrim(x.libelle), x.actif OR m.systeme, x.ordre, true)
                """))
            {
                cmd.Parameters.Add(Jsonb(valeur));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            // 2. Motifs nouveaux du serveur central (code « c_… »)
            await using (var cmd = Commande($"""
                INSERT INTO motif_reglable (liste, code, libelle, actif, ordre, effet, detail, modele_code_central)
                SELECT x.liste, x.code, btrim(x.libelle), x.actif, x.ordre, x.effet, x.detail, true FROM {RecordsetMotifs}
                WHERE NOT EXISTS (SELECT 1 FROM motif_reglable m WHERE m.liste = x.liste AND m.code = x.code)
                """))
            {
                cmd.Parameters.Add(Jsonb(valeur));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            // 3. Motif retiré du modèle : jamais supprimé ni désactivé, il redevient local, inchangé (D5)
            await using (var cmd = Commande($"""
                UPDATE motif_reglable m SET modele_code_central = false
                WHERE m.modele_code_central AND NOT EXISTS (SELECT 1 FROM {RecordsetMotifs} WHERE m.liste = x.liste AND m.code = x.code)
                """))
            {
                cmd.Parameters.Add(Jsonb(valeur));
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        private const string RecordsetRemises = "jsonb_to_recordset($1::jsonb -> 'remises') AS x(cle text, categorie text, pourcentage numeric, date_debut date, date_fin date, motif text)";

        private async Task<ResultatEcriture> EcrireRemisesAsync(string valeur, bool impose, CancellationToken ct)
        {
            // 1. Remise du serveur central retirée du modèle : arrêtée (jamais supprimée)
            await using (var cmd = Commande($"""
                UPDATE remise_automatique r SET arretee_le = now(), arretee_par = NULL
                WHERE r.modele_cle IS NOT NULL AND r.arretee_le IS NULL AND NOT EXISTS (SELECT 1 FROM {RecordsetRemises} WHERE x.cle = r.modele_cle)
                """))
            {
                cmd.Parameters.Add(Jsonb(valeur));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            // 2. Remises du modèle déjà présentes (une remise arrêtée parce que retirée, puis republiée, reprend)
            await using (var cmd = Commande($"""
                UPDATE remise_automatique r SET categorie = x.categorie, pourcentage = x.pourcentage, date_debut = x.date_debut, date_fin = x.date_fin,
                       motif = x.motif, arretee_le = NULL, arretee_par = NULL, modifiee_le = now(), modifiee_par = NULL
                FROM {RecordsetRemises}
                WHERE r.modele_cle = x.cle
                  AND (r.categorie, r.pourcentage, r.date_debut, r.date_fin, r.motif, r.arretee_le IS NULL)
                      IS DISTINCT FROM (x.categorie, x.pourcentage, x.date_debut, x.date_fin, x.motif, true)
                """))
            {
                cmd.Parameters.Add(Jsonb(valeur));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            // 3. Remises nouvelles (pas d'auteur local : cree_par vide, autorisé par ck_remise_auto_createur pour une remise du serveur central)
            await using (var cmd = Commande($"""
                INSERT INTO remise_automatique (categorie, pourcentage, date_debut, date_fin, motif, cree_par, modele_cle)
                SELECT x.categorie, x.pourcentage, x.date_debut, x.date_fin, x.motif, NULL, x.cle FROM {RecordsetRemises}
                WHERE NOT EXISTS (SELECT 1 FROM remise_automatique r WHERE r.modele_cle = x.cle)
                """))
            {
                cmd.Parameters.Add(Jsonb(valeur));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            if (!impose) return new ResultatEcriture(0, null);

            // 4. Mode imposé : les remises LOCALES en cours ou à venir sont arrêtées (jamais supprimées, D6)
            await using var arret = Commande("""
                WITH arretees AS (
                    UPDATE remise_automatique SET arretee_le = now(), arretee_par = NULL
                    WHERE modele_cle IS NULL AND arretee_le IS NULL AND date_fin >= current_date
                    RETURNING id, produit_id, categorie, pourcentage, date_debut, date_fin)
                SELECT coalesce(jsonb_agg(jsonb_build_object('id', id, 'produit_id', produit_id, 'categorie', categorie, 'pourcentage', pourcentage,
                                                             'date_debut', date_debut, 'date_fin', date_fin) ORDER BY id), '[]'::jsonb)::text, count(*)
                FROM arretees
                """);
            await using var r = await arret.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var n = (int)r.GetInt64(1);
            return n == 0 ? new ResultatEcriture(0, null)
                : new ResultatEcriture(n, new JsonObject { ["remises_locales_arretees"] = JsonNode.Parse(r.GetString(0)) }.ToJsonString());
        }

        // ---- État, journal, validation ----

        public async Task EnregistrerEtatAsync(MiseAJourEtat e, CancellationToken ct = default)
        {
            await using var cmd = Commande("""
                UPDATE reglage_mutualise SET mode = coalesce($2, mode), ajouts_locaux = coalesce($3, ajouts_locaux), etat = $4,
                       version_modele_appliquee = coalesce($5, version_modele_appliquee), applique_le = CASE WHEN $6 THEN now() ELSE applique_le END,
                       motif_rejet = $7, version_modele_rejetee = coalesce($8, version_modele_rejetee), origine = coalesce($9, origine)
                WHERE nom_table = $1
                """);
            cmd.Parameters.AddWithValue(table);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)e.Mode ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = (object?)e.AjoutsLocaux ?? DBNull.Value });
            cmd.Parameters.AddWithValue(e.Etat);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)e.VersionAppliquee ?? DBNull.Value });
            cmd.Parameters.AddWithValue(e.Applique);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)e.MotifRejet ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)e.VersionRejetee ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)e.Origine ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public Task JournaliserAsync(string action, string modeAvant, string modeApres, long? version, string? auteur, string? valeurAvantJson,
            string? valeurApresJson, long? utilisateurId, CancellationToken ct = default) =>
            JournaliserSqlAsync(cx, tx, action, table, modeAvant, modeApres, version, auteur, valeurAvantJson, valeurApresJson, utilisateurId, ct);

        internal static async Task JournaliserSqlAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, string action, string table, string modeAvant, string modeApres,
            long? version, string? auteur, string? avant, string? apres, long? utilisateurId, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand("SELECT reglage_journaliser($1, $2, $3, $4, $5, $6, $7, $8, $9)", cx, tx);
            cmd.Parameters.AddWithValue(action);
            cmd.Parameters.AddWithValue(table);
            cmd.Parameters.AddWithValue(modeAvant);
            cmd.Parameters.AddWithValue(modeApres);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)version ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)auteur ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = (object?)avant ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = (object?)apres ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)utilisateurId ?? DBNull.Value });
            await cmd.ExecuteScalarAsync(ct);
        }

        public Task ValiderAsync(CancellationToken ct = default) => tx.CommitAsync(ct);

        public async ValueTask DisposeAsync()
        {
            // Sans validation, la transaction est annulée (rien n'est écrit) ; le marqueur de session disparaît avec elle
            await tx.DisposeAsync();
            await cx.DisposeAsync();
        }
    }
}
