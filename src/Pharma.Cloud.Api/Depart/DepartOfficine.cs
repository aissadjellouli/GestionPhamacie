using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace Pharma.Cloud.Api.Depart;

public sealed record ResultatDepart(int CertificatsRevoques, DateTimeOffset ResilieeLe, bool DejaPartie);

public sealed record FichierExporte(string Fichier, long Lignes, string Empreinte);

/// <summary>Résultat de l'export : EmpreinteArchive = SHA-256 de l'archive CHIFFRÉE (celle remise) ; MotDePasse masqué dans ToString().</summary>
public sealed record ResultatExport(string Chemin, string EmpreinteArchive, IReadOnlyList<FichierExporte> Fichiers, MotDePasseArchive MotDePasse);

/// <summary>
/// Lot S6.3 (script central 021, conception-synchronisation.md § 30) : départ d'une officine. Connexion du PROPRIÉTAIRE de la base
/// centrale (console d'administration). Révocation et résiliation par cloud_depart_officine ; export complet remis au titulaire :
/// un CSV (UTF-8 sans BOM, séparateur « ; », en-tête) par table miroir et pour ligne_remplacee, réunis dans un zip chiffré AES-256
/// (WinZip AE-2, mot de passe tiré au sort, affiché une seule fois ; règle du 2026-11-20) avec empreintes.txt (SHA-256 de chaque CSV,
/// format « empreinte  fichier ») ; effacement par cloud_effacer_officine (simulation par
/// défaut, refusé avant la fin de la durée de conservation après départ). Patients déjà sous code au central : rien de plus à masquer.
/// </summary>
public static partial class DepartOfficine
{
    public const string FichierEmpreintes = "empreintes.txt";

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex NomTable();

    /// <summary>Révoque tous les certificats de l'officine et pose resiliee_le (gardée si déjà posée) ; journalisé.</summary>
    public static async Task<ResultatDepart> PartirAsync(NpgsqlDataSource db, Guid officine, string operateur, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT certificats_revoques, resiliee_le, deja_partie FROM cloud_depart_officine($1, $2)");
        cmd.Parameters.AddWithValue(officine);
        cmd.Parameters.AddWithValue(operateur);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await Reception.DepotCentral.LigneAttendueAsync(r, "cloud_depart_officine", ct);
        return new ResultatDepart(r.GetInt32(0), new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc)), r.GetBoolean(2));
    }

    /// <summary>Chemin de l'archive à écrire : extension .zip, dossier existant, fichier absent (jamais écrasé).</summary>
    public static string VerifierChemin(string? chemin)
    {
        if (string.IsNullOrWhiteSpace(chemin)) throw new ArgumentException("Chemin de l'archive attendu : --sortie <fichier.zip>.");
        var complet = Path.GetFullPath(chemin);
        if (!complet.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("L'archive doit avoir l'extension .zip.");
        if (Path.GetDirectoryName(complet) is not { } dossier || !Directory.Exists(dossier)) throw new ArgumentException("Dossier de sortie introuvable.");
        if (File.Exists(complet)) throw new ArgumentException("Le fichier de sortie existe déjà : il n'est jamais écrasé.");
        return complet;
    }

    /// <summary>Contenu de empreintes.txt : une ligne « empreinte  fichier » par CSV, triée par nom, fins de ligne LF.</summary>
    public static string ContenuEmpreintes(IEnumerable<FichierExporte> fichiers) =>
        string.Concat(fichiers.OrderBy(f => f.Fichier, StringComparer.Ordinal).Select(f => $"{f.Empreinte}  {f.Fichier}\n"));

    /// <summary>
    /// Écrit l'export complet de l'officine (une image cohérente : transaction REPEATABLE READ en lecture seule), directement en zip
    /// chiffré (aucune copie en clair sur le disque). Écrit d'abord un fichier « .partiel » (déjà chiffré), renommé à la fin ; supprimé
    /// en cas d'erreur. L'empreinte SHA-256 est calculée sur l'archive chiffrée finale.
    /// </summary>
    public static async Task<ResultatExport> ExporterAsync(NpgsqlDataSource db, Guid officine, string chemin, CancellationToken ct)
    {
        var cible = VerifierChemin(chemin);
        var partiel = cible + ".partiel";
        var motDePasse = MotDePasseArchive.Tirer();
        var fichiers = new List<FichierExporte>();
        var partielCree = false;   // un .partiel laissé par un autre passage (ou en cours d'écriture) n'est jamais supprimé ici
        try
        {
            await using (var cnx = await db.OpenConnectionAsync(ct))
            await using (var tx = await cnx.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct))
            {
                await using (var ro = new NpgsqlCommand("SET TRANSACTION READ ONLY", cnx, tx)) await ro.ExecuteNonQueryAsync(ct);
                await using (var pose = new NpgsqlCommand("SELECT set_config('app.officine_id', $1, true)", cnx, tx))
                {
                    pose.Parameters.AddWithValue(officine.ToString("D"));
                    await pose.ExecuteScalarAsync(ct);
                }
                var tables = new List<(string Nom, string[] Cle)>();
                await using (var liste = new NpgsqlCommand("""
                    SELECT nom_table, cle FROM cloud_table_miroir WHERE to_regclass('public.' || quote_ident(nom_table)) IS NOT NULL
                    UNION ALL SELECT 'ligne_remplacee', ARRAY['id']
                    -- Lot S6.5.3 (script 022) : déclarations d'époque, versions divergentes, restaurations constatées et anomalies rattachées
                    UNION ALL SELECT t, ARRAY['id'] FROM unnest(ARRAY['cloud_epoque_changement', 'cloud_ligne_divergente',
                        'cloud_integrite_bifurcation', 'cloud_anomalie_classement']) t WHERE to_regclass('public.' || t) IS NOT NULL
                    ORDER BY 1
                    """, cnx, tx))
                await using (var r = await liste.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct)) tables.Add((r.GetString(0), r.GetFieldValue<string[]>(1)));

                // Zip chiffré écrit directement dans le fichier : les CSV ne sont jamais posés en clair sur le disque
                await using var flux = new FileStream(partiel, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                partielCree = true;
                var zip = new ArchiveChiffree(flux, motDePasse);
                foreach (var (nom, cle) in tables)
                {
                    if (!NomTable().IsMatch(nom) || cle.Any(c => !NomTable().IsMatch(c)))
                        throw new InvalidOperationException($"Nom de table ou de colonne inattendu dans cloud_table_miroir : {nom}");
                    long lignes;
                    await using (var compte = new NpgsqlCommand($"SELECT count(*) FROM public.\"{nom}\" WHERE officine_id = $1", cnx, tx))
                    {
                        compte.Parameters.AddWithValue(officine);
                        lignes = (long)(await compte.ExecuteScalarAsync(ct))!;
                    }
                    var ordre = string.Join(", ", cle.Select(c => $"\"{c}\""));
                    var copie = $"COPY (SELECT * FROM public.\"{nom}\" WHERE officine_id = cloud_officine_courante() ORDER BY {ordre}) "
                                + "TO STDOUT WITH (FORMAT csv, HEADER true, DELIMITER ';')";
                    var fichier = nom + ".csv";
                    var empreinte = await zip.AjouterAsync(fichier, async sortie =>
                    {
                        using var lecteur = await cnx.BeginTextExportAsync(copie, ct);
                        return await CopierAsync(lecteur, sortie, ct);
                    }, ct);
                    fichiers.Add(new FichierExporte(fichier, lignes, empreinte));
                }
                await zip.AjouterAsync(FichierEmpreintes, async sortie =>
                {
                    await sortie.WriteAsync(new UTF8Encoding(false).GetBytes(ContenuEmpreintes(fichiers)), ct);
                    return true;
                }, ct);
                await zip.TerminerAsync(ct);
                await flux.FlushAsync(ct);
                await tx.CommitAsync(ct);
            }
            File.Move(partiel, cible);
        }
        catch
        {
            if (partielCree && File.Exists(partiel)) File.Delete(partiel);
            throw;
        }
        string archive;
        await using (var lu = new FileStream(cible, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            archive = Convert.ToHexStringLower(await SHA256.HashDataAsync(lu, ct));
        return new ResultatExport(cible, archive, fichiers, motDePasse);
    }

    /// <summary>
    /// Détail noté au journal des départs : fichier, chiffrement, empreinte de l'archive chiffrée (celle remise et reprise dans l'accusé
    /// de réception), lignes et empreinte de chaque CSV, résultat de l'envoi du mot de passe (canal, adresse masquée, envoyé ou raison
    /// de l'échec). Jamais le mot de passe ni le contenu du message.
    /// </summary>
    public static string DetailExport(ResultatExport export, ResultatEnvoiMotDePasse? envoi = null) => System.Text.Json.JsonSerializer.Serialize(new
    {
        archive = Path.GetFileName(export.Chemin),
        chiffrement = ArchiveChiffree.Description,
        empreinte_archive = export.EmpreinteArchive,
        fichiers = export.Fichiers.Select(f => new { fichier = f.Fichier, lignes = f.Lignes, empreinte = f.Empreinte }),
        envoi_mot_de_passe = envoi is null
            ? (object)new { resultat = "non demande" }
            : new { canal = envoi.Canal, adresse = envoi.AdresseMasquee, resultat = envoi.Envoye ? "envoye" : "echec", raison = envoi.Raison }
    });

    /// <summary>Note l'export remis au journal des départs (<see cref="DetailExport"/>, sans le mot de passe).</summary>
    public static async Task NoterExportAsync(NpgsqlDataSource db, Guid officine, string operateur, ResultatExport export,
        ResultatEnvoiMotDePasse? envoi, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT cloud_noter_export_depart($1, $2, $3)");
        cmd.Parameters.AddWithValue(officine);
        cmd.Parameters.AddWithValue(operateur);
        cmd.Parameters.Add(new NpgsqlParameter { Value = DetailExport(export, envoi), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Libellé de l'officine au central (repris dans l'e-mail du mot de passe).</summary>
    public static async Task<string> LibelleAsync(NpgsqlDataSource db, Guid officine, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT libelle FROM officine WHERE id = $1");
        cmd.Parameters.AddWithValue(officine);
        return await cmd.ExecuteScalarAsync(ct) as string ?? throw new InvalidOperationException("Officine inconnue au serveur central.");
    }

    /// <summary>
    /// Export puis, seulement s'il a réussi et si une adresse est donnée, envoi du mot de passe par e-mail (échec d'envoi non bloquant).
    /// Une erreur d'export remonte telle quelle : aucun envoi.
    /// </summary>
    public static async Task<(ResultatExport Export, ResultatEnvoiMotDePasse? Envoi)> ExporterPuisEnvoyerAsync(
        Func<CancellationToken, Task<ResultatExport>> exporter, string? adresse, IEnvoiCourriel? envoi, string? reglagesManquants,
        string nomOfficine, CancellationToken ct)
    {
        var export = await exporter(ct);
        var resultat = adresse is null ? null : await EnvoiMotDePasse.EnvoyerAsync(envoi, reglagesManquants, adresse, nomOfficine, export, ct);
        return (export, resultat);
    }

    /// <summary>
    /// Compte rendu de fin de commande : lignes par CSV, archive et empreinte SHA-256 de l'archive chiffrée, le mot de passe affiché
    /// une seule fois (dans tous les cas, même envoyé par e-mail), puis le résultat de l'envoi (sans le mot de passe).
    /// </summary>
    public static void EcrireCompteRendu(TextWriter sortie, ResultatExport export, ResultatEnvoiMotDePasse? envoi = null)
    {
        foreach (var f in export.Fichiers.Where(f => f.Lignes > 0)) sortie.WriteLine($"  {f.Fichier} : {f.Lignes} ligne(s)");
        sortie.WriteLine($"Export complet écrit : {export.Chemin} (zip chiffré {ArchiveChiffree.Description}, {export.Fichiers.Count} fichier(s) CSV,"
            + $" {export.Fichiers.Sum(f => f.Lignes)} ligne(s)) ; SHA-256 de l'archive chiffrée : {export.EmpreinteArchive} (noté au journal des départs).");
        sortie.WriteLine($"Mot de passe de l'archive (affiché une seule fois, à noter) : {export.MotDePasse.Valeur}");
        sortie.WriteLine("  Ne pas rediriger cette sortie vers un fichier. Ne jamais remettre le mot de passe par le même canal que l'archive.");
        if (envoi is null)
            sortie.WriteLine("Envoi par e-mail : non demandé (pas d'option --email) ; transmettre le mot de passe affiché par un autre canal que l'archive.");
        else if (envoi.Envoye)
            sortie.WriteLine($"Envoi par e-mail : mot de passe envoyé à {envoi.AdresseMasquee} (noté au journal des départs, sans le mot de passe).");
        else
            sortie.WriteLine($"Envoi par e-mail à {envoi.AdresseMasquee} : ÉCHEC ({envoi.Raison}). L'archive reste valide ; transmettre le mot de passe"
                + " affiché ci-dessus par un autre canal que l'archive.");
        sortie.WriteLine("Envoi par SMS : non disponible (aucun fournisseur SMS au serveur central).");
    }

    /// <summary>
    /// Effacement des données de l'officine (tables miroir, ligne_remplacee, lots bruts, tables d'intégrité, ordres, éléments descendants,
    /// chargements, volumes du jour et époques ; script 027) : simulation (compte) si non confirmé. Sont gardés : la fiche de l'officine, ses
    /// certificats, le code d'activation, les réglages de familles, les journaux (sécurité, départs, réglages, ordres).
    /// Refusé par la base (PostgresException P0001) si l'officine n'est pas partie ou si la durée de conservation n'est pas écoulée.
    /// </summary>
    public static async Task<IReadOnlyList<(string Table, long Lignes)>> EffacerAsync(NpgsqlDataSource db, Guid officine, bool confirmer,
        string? operateur, CancellationToken ct)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT nom_table, lignes FROM cloud_effacer_officine($1, $2, $3)", cnx, tx) { CommandTimeout = 3600 };
        cmd.Parameters.AddWithValue(officine);
        cmd.Parameters.AddWithValue(confirmer);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)operateur ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        var liste = new List<(string, long)>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) liste.Add((r.GetString(0), r.GetInt64(1)));
        await tx.CommitAsync(ct);
        return liste;
    }

    /// <summary>Recopie le texte CSV en UTF-8 (sans BOM) et renvoie son empreinte SHA-256 (hexadécimal minuscule).</summary>
    private static async Task<string> CopierAsync(TextReader lecteur, Stream sortie, CancellationToken ct)
    {
        using var hachage = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var encodeur = new UTF8Encoding(false).GetEncoder();
        var caracteres = new char[32768];
        var octets = new byte[new UTF8Encoding(false).GetMaxByteCount(caracteres.Length)];
        int lus;
        while ((lus = await lecteur.ReadAsync(caracteres.AsMemory(), ct)) > 0)
        {
            var n = encodeur.GetBytes(caracteres, 0, lus, octets, 0, flush: false);
            hachage.AppendData(octets, 0, n);
            await sortie.WriteAsync(octets.AsMemory(0, n), ct);
        }
        var fin = encodeur.GetBytes([], 0, 0, octets, 0, flush: true);
        if (fin > 0)
        {
            hachage.AppendData(octets, 0, fin);
            await sortie.WriteAsync(octets.AsMemory(0, fin), ct);
        }
        return Convert.ToHexStringLower(hachage.GetHashAndReset());
    }
}
