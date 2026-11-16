using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Pharma.Domain.Reglages;
using Pharma.Shared;

namespace Pharma.Cloud.Api.Reglages;

/// <summary>
/// Commandes d'administration des réglages mutualisés (lot M6, docs/conception-synchronisation.md § 16 bis). Tout passe par les fonctions
/// d'administration du script central 010, avec la connexion du propriétaire (PHARMA_CLOUD_ADMIN_CONNEXION) : jamais par pharma_cloud_api.
/// Erreurs : exception ArgumentException ou InvalidOperationException (message en français) ; refus de la confirmation : code 1.
/// </summary>
public sealed class AdministrationReglages(Func<NpgsqlDataSource> connexion, TextWriter sortie, TextWriter erreur, TextReader entree,
    Func<DateOnly> aujourdhui, Func<DateTime> maintenantUtc, string compte, string poste)
{
    private const string Perimetre = "tous";

    /// <summary>Fabrique pour la console : compte Windows et poste de l'administrateur inscrits au journal central (D10).</summary>
    public static AdministrationReglages PourConsole(Func<NpgsqlDataSource> connexion) =>
        new(connexion, Console.Out, Console.Error, Console.In, () => DateOnly.FromDateTime(DateTime.Now), () => DateTime.UtcNow,
            Tronquer($"{Environment.UserDomainName}\\{Environment.UserName}"), Tronquer(Environment.MachineName));

    private static string Tronquer(string s) => s.Length <= 200 ? s : s[..200];

    public async Task<int> ExecuterAsync(string[] args, CancellationToken ct = default)
    {
        var c = ArgumentsReglages.Analyser(args);
        try
        {
            return c.Nom switch
            {
                "--reglages-mutualises" => await EtatAsync(c, ct),
                "--publier-reglage" => await PublierAsync(c, ct),
                "--mode-reglage" => await ModeAsync(c, ct),
                "--modele-depuis-officine" => await ModeleDepuisOfficineAsync(c, ct),
                "--liberer-reglages" => await LibererAsync(c, true, ct),
                "--retablir-reglages" => await LibererAsync(c, false, ct),
                "--journal-reglages" => await JournalAsync(c, ct),
                _ => throw new ArgumentException("Commande inconnue.")
            };
        }
        catch (PostgresException e)
        {
            // Les fonctions du script 010 lèvent des messages métier en français (code P0001)
            throw new InvalidOperationException(e.SqlState == "P0001" ? e.MessageText : $"Base centrale : {e.MessageText}", e);
        }
    }

    // ---- --reglages-mutualises ----------------------------------------------------------------

    private sealed record Modele(string Table, string Libelle, string Mode, int Version, bool Publie, string Auteur, DateTime ModifieLe, bool AjoutsLocaux);

    private sealed record EtatOfficine(Guid Officine, string Libelle, string Table, string? Mode, string? ModeRecu, long? VersionRecue, long? VersionAppliquee,
        string Etat, string? MotifRejet, string? Origine, DateTime? AppliqueLe, DateTime? LibereeLe);

    private async Task<int> EtatAsync(CommandeReglages c, CancellationToken ct)
    {
        var table = c.Positions.Count > 0 ? ArgumentsReglages.ExigerTable(c.Table()) : null;
        await using var db = connexion();
        var modeles = await LireModelesAsync(db, table, ct);
        var etats = new List<EtatOfficine>();
        string? indisponible = null;
        long officines = 0;
        try
        {
            await using (var cmd = db.CreateCommand("SELECT count(*) FROM officine WHERE resiliee_le IS NULL"))
                officines = (long)(await cmd.ExecuteScalarAsync(ct))!;
            await using var cmd2 = db.CreateCommand("""
                SELECT officine_id, libelle, nom_table, mode, mode_recu, version_modele_recue, version_modele_appliquee, etat, motif_rejet, origine, applique_le, liberee_le
                FROM cloud_etat_reglages($1)
                """);
            cmd2.Parameters.Add(new NpgsqlParameter { Value = (object?)table ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            await using var r = await cmd2.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                etats.Add(new EtatOfficine(r.GetGuid(0), r.GetString(1), r.GetString(2), Texte(r, 3), Texte(r, 4), Long(r, 5), Long(r, 6), r.GetString(7),
                    Texte(r, 8), Texte(r, 9), r.IsDBNull(10) ? null : r.GetDateTime(10), r.IsDBNull(11) ? null : r.GetDateTime(11)));
        }
        catch (PostgresException e)
        {
            indisponible = e.MessageText;
        }

        sortie.WriteLine($"Réglages mutualisés ({modeles.Count} table(s), {officines} officine(s) active(s)) :");
        foreach (var m in modeles)
        {
            var mode = m.Mode == ValidationModele.ModeImpose ? "IMPOSÉ" : "proposé";
            if (!m.Publie)
                sortie.WriteLine($"  {m.Table,-26} {m.Libelle} : NON PUBLIÉ (rien ne descend ; mode noté : {mode})");
            else
                sortie.WriteLine($"  {m.Table,-26} {m.Libelle} : {mode}, version {m.Version}, par {m.Auteur} le {Date(m.ModifieLe)}{(m.AjoutsLocaux ? "" : ", sans ajout local")}");
            if (indisponible is not null) continue;
            var lignes = etats.Where(e => e.Table == m.Table).ToList();
            if (!m.Publie && lignes.All(e => e.Etat == "aucun")) continue;
            var applique = lignes.Count(e => e.Etat == "applique");
            var aJour = lignes.Count(e => e.Etat == "applique" && e.VersionAppliquee == m.Version);
            var rejete = lignes.Count(e => e.Etat == "rejete");
            var nonAdopte = lignes.Count(e => e.Etat == "non_adopte");
            var liberees = lignes.Count(e => e.LibereeLe is not null);
            sortie.WriteLine($"      officines : {applique} ont appliqué ({aJour} à la version {m.Version}), {rejete} ont rejeté, {nonAdopte} n'ont pas adopté, "
                + $"{Math.Max(0, officines - lignes.Count)} sans information{(liberees > 0 ? $", {liberees} libérée(s)" : "")}");
            foreach (var e in lignes.Where(e => e.Etat == "rejete"))
                sortie.WriteLine($"      REJET {e.Officine}  {e.Libelle} : {e.MotifRejet ?? "motif non reçu"}");
            if (table is not null)
                foreach (var e in lignes.Where(e => e.Etat != "rejete"))
                    sortie.WriteLine($"      {e.Officine}  {e.Libelle} : {e.Etat}, mode en vigueur {e.Mode ?? "-"}, reçu {e.ModeRecu ?? "-"} v{e.VersionRecue?.ToString(CultureInfo.InvariantCulture) ?? "-"}, "
                        + $"appliqué v{e.VersionAppliquee?.ToString(CultureInfo.InvariantCulture) ?? "-"}{(e.AppliqueLe is null ? "" : $" le {Date(e.AppliqueLe.Value)}")}, origine {e.Origine ?? "-"}{(e.LibereeLe is null ? "" : " (libérée)")}");
        }
        if (indisponible is not null) sortie.WriteLine($"État des officines indisponible : {indisponible}");
        else sortie.WriteLine("L'état des officines est celui de leur dernier envoi au central (table montante reglage_mutualise).");
        return 0;
    }

    private static async Task<List<Modele>> LireModelesAsync(NpgsqlDataSource db, string? table, CancellationToken ct)
    {
        var liste = new List<Modele>();
        await using var cmd = db.CreateCommand("""
            SELECT t.nom_table, t.libelle, m.mode, m.version_modele, m.valeur IS NOT NULL, m.modifie_par, m.modifie_le, m.ajouts_locaux
            FROM cloud_reglage_table t JOIN cloud_reglage_modele m ON m.nom_table = t.nom_table AND m.perimetre = $1
            WHERE $2::text IS NULL OR t.nom_table = $2 ORDER BY t.nom_table
            """);
        cmd.Parameters.AddWithValue(Perimetre);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)table ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new Modele(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetBoolean(4), r.GetString(5), r.GetDateTime(6), r.GetBoolean(7)));
        return liste;
    }

    // ---- --publier-reglage --------------------------------------------------------------------

    private async Task<int> PublierAsync(CommandeReglages c, CancellationToken ct)
    {
        var table = ArgumentsReglages.ExigerTable(c.Table());
        var auteur = c.Auteur();
        var motif = c.Motif();
        var fichier = c.Option("--fichier") ?? throw new ArgumentException("Le fichier du modèle est obligatoire : --fichier <json>.");
        if (!File.Exists(fichier)) throw new ArgumentException($"Fichier introuvable : {fichier}");
        if (new FileInfo(fichier).Length > 4 * ContratMutu.TailleMax) throw new ArgumentException("Le fichier est trop volumineux pour un modèle (64 Kio au plus).");
        var simulation = c.Drapeau("--simulation");

        // D12 : contrôle métier complet avant toute publication, avec la date du jour
        var (modele, erreurs) = ModelePublication.Preparer(table, await File.ReadAllTextAsync(fichier, Encoding.UTF8, ct), aujourdhui());
        if (modele is null)
        {
            erreur.WriteLine($"Modèle refusé pour « {table} » ({erreurs.Count} erreur(s)) :");
            foreach (var e in erreurs) erreur.WriteLine($"  - {e}");
            erreur.WriteLine(simulation ? "SIMULATION : rien n'a été écrit." : "Rien n'a été publié.");
            return 1;
        }

        // État actuel : obligatoire pour publier ; en simulation, un contrôle hors connexion reste possible
        Modele? actuel = null;
        NpgsqlDataSource? db = null;
        try
        {
            try
            {
                db = connexion();
                actuel = (await LireModelesAsync(db, table, ct)).FirstOrDefault();
            }
            catch (Exception e) when (simulation && e is InvalidOperationException or NpgsqlException)
            {
                sortie.WriteLine($"(État actuel non lu : {e.Message})");
            }

            // Sans lecture de l'état (simulation hors connexion), on suppose un changement
            var change = db is null || actuel is null || !actuel.Publie || actuel.Mode != modele.Mode
                || (modele.PorteAjoutsLocaux && actuel.AjoutsLocaux != modele.AjoutsLocaux)
                || !await MemeValeurAsync(db, table, modele, ct);
            sortie.WriteLine($"{(simulation ? "SIMULATION — " : "")}Publication de « {table} » : modèle valide.");
            sortie.WriteLine($"  Mode : {(actuel is { Publie: true } ? Libelle(actuel.Mode) + " -> " : "")}{Libelle(modele.Mode)}");
            if (modele.PorteAjoutsLocaux) sortie.WriteLine($"  Ajouts locaux dans les officines : {(modele.AjoutsLocaux == true ? "permis" : "interdits")}");
            sortie.WriteLine($"  Contenu : {modele.Elements} élément(s), {modele.Taille} octets ; auteur inscrit au journal : {auteur} ({compte} sur {poste})");
            if (actuel is not null)
                sortie.WriteLine(change ? $"  Version du modèle : {actuel.Version} -> {actuel.Version + 1}" : $"  Aucun changement par rapport à la version {actuel.Version} : rien ne serait publié.");
            if (modele.Mode == ValidationModele.ModeImpose && change)
                sortie.WriteLine("  ATTENTION : en mode imposé, la valeur du modèle remplacera celle de chaque officine, même modifiée localement (avec message au titulaire de chacune).");
            if (simulation)
            {
                sortie.WriteLine("SIMULATION : rien n'a été écrit.");
                return 0;
            }
            if (!change)
            {
                sortie.WriteLine("Rien à publier : le modèle est identique à celui en vigueur.");
                return 0;
            }
            if (modele.Mode == ValidationModele.ModeImpose && !Confirmer(c, $"Publier « {table} » en mode imposé ?"))
            {
                erreur.WriteLine("Publication annulée : rien n'a été écrit. (Confirmation « oui » ou option --oui.)");
                return 1;
            }

            await using var cmd = db!.CreateCommand("SELECT cloud_publier_reglage($1, $2, $3, $4, $5, $6, $7, $8, $9)");
            cmd.Parameters.AddWithValue(table);
            cmd.Parameters.Add(new NpgsqlParameter { Value = modele.ValeurJson, NpgsqlDbType = NpgsqlDbType.Jsonb });
            cmd.Parameters.AddWithValue(auteur);
            cmd.Parameters.AddWithValue(modele.Mode);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)modele.AjoutsLocaux ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Boolean });
            cmd.Parameters.AddWithValue(compte);
            cmd.Parameters.AddWithValue(poste);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)motif ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.AddWithValue(Perimetre);
            var version = (int)(await cmd.ExecuteScalarAsync(ct))!;
            sortie.WriteLine($"Publié : « {table} » en {Libelle(modele.Mode)}, version {version}, journalisé. Il descend dans chaque officine à son prochain échange (une minute environ) ; "
                + "l'officine l'applique puis peut le rejeter s'il ne passe pas ses contrôles (état : --reglages-mutualises).");
            return 0;
        }
        finally
        {
            if (db is not null) await db.DisposeAsync();
        }
    }

    private static async Task<bool> MemeValeurAsync(NpgsqlDataSource db, string table, ModelePrepare modele, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT valeur::text FROM cloud_reglage_modele WHERE perimetre = $1 AND nom_table = $2");
        cmd.Parameters.AddWithValue(Perimetre);
        cmd.Parameters.AddWithValue(table);
        return await cmd.ExecuteScalarAsync(ct) is string courante && JsonNode.DeepEquals(JsonNode.Parse(courante), modele.Valeur);
    }

    // ---- --mode-reglage -----------------------------------------------------------------------

    private async Task<int> ModeAsync(CommandeReglages c, CancellationToken ct)
    {
        var table = ArgumentsReglages.ExigerTable(c.Table());
        var mode = c.Table(1);
        var auteur = c.Auteur();
        var motif = c.Motif();
        if (mode == ValidationModele.ModeLocal)
            throw new ArgumentException("Le mode « local » n'existe pas pour une table du central (modes du modèle : impose ou propose). "
                + "Pour qu'une officine redevienne maîtresse de ses réglages : --liberer-reglages <code officine> ; "
                + "pour qu'aucun modèle ne descende pour une table, ne rien publier.");
        if (mode is not (ValidationModele.ModeImpose or ValidationModele.ModePropose))
            throw new ArgumentException("Mode attendu : impose, propose (ou local, refusé ici).");

        if (mode == ValidationModele.ModeImpose)
        {
            sortie.WriteLine($"ATTENTION : passer « {table} » en « imposé » REMPLACERA, dans chaque officine, les valeurs modifiées localement par celles du modèle central.");
            sortie.WriteLine("Le titulaire de chaque officine en sera prévenu par un message ; la table ne sera plus modifiable sur place (sauf libération : --liberer-reglages).");
            if (!Confirmer(c, "Confirmer le passage en imposé ?"))
            {
                erreur.WriteLine("Annulé : le mode n'a pas été changé. (Confirmation « oui » ou option --oui.)");
                return 1;
            }
        }

        await using var db = connexion();
        var avant = (await LireModelesAsync(db, table, ct)).FirstOrDefault();
        await using var cmd = db.CreateCommand("SELECT cloud_regler_mode_reglage($1, $2, $3, $4, $5, $6, $7)");
        cmd.Parameters.AddWithValue(table);
        cmd.Parameters.AddWithValue(mode);
        cmd.Parameters.AddWithValue(auteur);
        cmd.Parameters.AddWithValue(compte);
        cmd.Parameters.AddWithValue(poste);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)motif ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(Perimetre);
        var version = (int)(await cmd.ExecuteScalarAsync(ct))!;
        if (avant is not null && avant.Mode == mode)
            sortie.WriteLine($"« {table} » est déjà en mode {Libelle(mode)} : rien n'a changé.");
        else if (version == 0)
            sortie.WriteLine($"« {table} » : mode {Libelle(mode)} noté et journalisé. Aucun modèle n'est publié : rien ne descend (voir --publier-reglage).");
        else
            sortie.WriteLine($"« {table} » : mode {Libelle(mode)}, nouvelle version {version}, journalisé. Il descend dans chaque officine à son prochain échange (une minute environ).");
        return 0;
    }

    // ---- --modele-depuis-officine -------------------------------------------------------------

    private async Task<int> ModeleDepuisOfficineAsync(CommandeReglages c, CancellationToken ct)
    {
        var officine = c.Officine(0);
        var table = ArgumentsReglages.ExigerTable(c.Table(1));
        var fichier = c.Option("--fichier") ?? throw new ArgumentException("Le fichier de sortie est obligatoire : --fichier <sortie>.");
        var mode = c.Option("--mode") ?? ValidationModele.ModePropose;
        if (mode is not (ValidationModele.ModeImpose or ValidationModele.ModePropose)) throw new ArgumentException("--mode : impose ou propose.");
        if (File.Exists(fichier) && !c.Drapeau("--ecraser")) throw new ArgumentException($"Le fichier existe déjà : {fichier} (ajoutez --ecraser pour le remplacer).");

        await using var db = connexion();
        await using var cn = await db.OpenConnectionAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await using (var existe = new NpgsqlCommand("SELECT libelle FROM officine WHERE id = $1", cn, tx))
        {
            existe.Parameters.AddWithValue(officine);
            if (await existe.ExecuteScalarAsync(ct) is not string libelle) throw new InvalidOperationException($"Officine inconnue : {officine}");
            sortie.WriteLine($"Officine : {libelle} ({officine})");
        }
        // Les tables miroir sont protégées par ligne : l'officine est posée pour cette transaction seulement
        await using (var pose = new NpgsqlCommand("SELECT set_config('app.officine_id', $1, true)", cn, tx))
        {
            pose.Parameters.AddWithValue(officine.ToString());
            await pose.ExecuteScalarAsync(ct);
        }
        var colonnes = ModeleDepuisOfficine.ColonnesALire(table);
        var sql = $"SELECT {string.Join(", ", colonnes.Select(x => $"\"{x}\""))} FROM public.\"{table}\" WHERE sync_absente_le IS NULL ORDER BY {ModeleDepuisOfficine.Ordre(table)}";
        var lignes = new List<LigneMiroir>();
        await using (var lecture = new NpgsqlCommand(sql, cn, tx))
        await using (var r = await lecture.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var v = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < colonnes.Count; i++) v[colonnes[i]] = r.IsDBNull(i) ? null : r.GetValue(i);
                lignes.Add(new LigneMiroir(v));
            }
        await tx.RollbackAsync(ct);

        var (json, notes) = ModeleDepuisOfficine.Construire(table, officine, lignes, mode, aujourdhui(), maintenantUtc());
        await File.WriteAllTextAsync(fichier, json + Environment.NewLine, new UTF8Encoding(false), ct);
        foreach (var n in notes) sortie.WriteLine($"  Note : {n}");
        sortie.WriteLine($"Modèle écrit : {fichier} ({lignes.Count} ligne(s) du miroir de « {table} »). Rien n'a été publié.");
        var (_, erreurs) = ModelePublication.Preparer(table, json, aujourdhui());
        if (erreurs.Count > 0)
        {
            sortie.WriteLine($"ATTENTION : ce modèle ne passerait pas la publication ({erreurs.Count} erreur(s)) ; à corriger à la main :");
            foreach (var e in erreurs) sortie.WriteLine($"  - {e}");
        }
        sortie.WriteLine("À relire avant publication : version, date et auteur du fichier sont remplacés par le central ; retirer ce qui est propre à cette officine.");
        sortie.WriteLine($"Simulation : --publier-reglage {table} --fichier {fichier} --auteur <nom> --simulation");
        return 0;
    }

    // ---- --liberer-reglages / --retablir-reglages ---------------------------------------------

    private async Task<int> LibererAsync(CommandeReglages c, bool liberer, CancellationToken ct)
    {
        var officine = c.Officine();
        var auteur = c.Auteur();
        var motif = c.Motif();
        await using var db = connexion();
        string libelle;
        DateTime? liberee;
        await using (var etat = db.CreateCommand("SELECT libelle, mutualisation_liberee_le FROM officine WHERE id = $1"))
        {
            etat.Parameters.AddWithValue(officine);
            await using var r = await etat.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new InvalidOperationException($"Officine inconnue : {officine}");
            (libelle, liberee) = (r.GetString(0), r.IsDBNull(1) ? null : r.GetDateTime(1));
        }
        if (liberer && liberee is not null)
        {
            sortie.WriteLine($"{libelle} ({officine}) est déjà libérée depuis le {Date(liberee.Value)} : rien n'a changé.");
            return 0;
        }
        if (!liberer && liberee is null)
        {
            sortie.WriteLine($"{libelle} ({officine}) n'est pas libérée : rien à rétablir.");
            return 0;
        }
        await using var cmd = db.CreateCommand(liberer ? "SELECT cloud_liberer_reglages($1, $2, $3, $4, $5, $6)" : "SELECT cloud_retablir_reglages($1, $2, $3, $4, $5, $6)");
        cmd.Parameters.AddWithValue(officine);
        cmd.Parameters.AddWithValue(auteur);
        cmd.Parameters.AddWithValue(compte);
        cmd.Parameters.AddWithValue(poste);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)motif ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(Perimetre);
        var n = (int)(await cmd.ExecuteScalarAsync(ct))!;
        sortie.WriteLine(liberer
            ? $"{libelle} ({officine}) libérée, journalisé : {n} table(s) publiée(s) repassent en « local » à son prochain échange ; ses valeurs sont gardées et ses tables redeviennent modifiables."
            : $"{libelle} ({officine}) rétablie, journalisé : {n} modèle(s) publié(s) redescendent à son prochain échange ; en mode imposé ils remplaceront les valeurs locales (message au titulaire).");
        return 0;
    }

    // ---- --journal-reglages -------------------------------------------------------------------

    private async Task<int> JournalAsync(CommandeReglages c, CancellationToken ct)
    {
        var table = c.Positions.Count > 0 ? ArgumentsReglages.ExigerTable(c.Table()) : null;
        var depuis = ArgumentsReglages.Depuis(c.Option("--depuis"));
        var limite = ArgumentsReglages.Limite(c.Option("--limite"));
        await using var db = connexion();
        await using var cmd = db.CreateCommand("""
            SELECT le, nom_table, officine_id, action, mode_avant, mode_apres, version_modele, auteur, compte_systeme, poste, motif
            FROM cloud_journal_reglage
            WHERE ($1::text IS NULL OR nom_table = $1) AND ($2::timestamptz IS NULL OR le >= $2)
            ORDER BY le DESC, id DESC LIMIT $3
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)table ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = depuis is null ? DBNull.Value : depuis.Value.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.AddWithValue(limite);
        var lignes = new List<string>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                lignes.Add($"{Date(r.GetDateTime(0))}  {r.GetString(3),-11} {(r.IsDBNull(1) ? "(toutes)" : r.GetString(1)),-26}"
                    + $"{(r.IsDBNull(2) ? "" : $" officine {r.GetGuid(2)}")} {(r.IsDBNull(4) ? "-" : r.GetString(4))} -> {(r.IsDBNull(5) ? "-" : r.GetString(5))}"
                    + $"{(r.IsDBNull(6) ? "" : $" v{r.GetInt32(6)}")} par {r.GetString(7)} ({(r.IsDBNull(8) ? "?" : r.GetString(8))} sur {(r.IsDBNull(9) ? "?" : r.GetString(9))})"
                    + $"{(r.IsDBNull(10) ? "" : $" : {r.GetString(10)}")}");
        lignes.Reverse();
        if (lignes.Count == 0) sortie.WriteLine("Journal des réglages mutualisés : aucune entrée.");
        else
        {
            sortie.WriteLine($"Journal des réglages mutualisés ({lignes.Count} entrée(s), de la plus ancienne à la plus récente) :");
            foreach (var l in lignes) sortie.WriteLine($"  {l}");
        }
        return 0;
    }

    // ---- Outils -------------------------------------------------------------------------------

    /// <summary>Confirmation : option --oui, sinon la réponse « oui » (ou « o ») saisie. Sans saisie possible, c'est un refus.</summary>
    private bool Confirmer(CommandeReglages c, string question)
    {
        if (c.Drapeau("--oui")) return true;
        sortie.Write($"{question} Tapez « oui » pour confirmer : ");
        var reponse = entree.ReadLine()?.Trim().ToLowerInvariant();
        return reponse is "oui" or "o";
    }

    private static string Libelle(string mode) => mode == ValidationModele.ModeImpose ? "imposé" : "proposé";

    private static string Date(DateTime d) => d.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string? Texte(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static long? Long(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
}
