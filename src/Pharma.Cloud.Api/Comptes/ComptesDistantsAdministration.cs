using Npgsql;
using Pharma.Shared;

namespace Pharma.Cloud.Api.Comptes;

/// <summary>
/// Lot S7.1 (script central 030) : comptes distants des écrans du central, par la console (connexion du propriétaire, comme les
/// autres commandes). Chaque commande est notée au journal des accès distants (sans identifiant ni nom : n° du compte seulement).
///   --creer-compte-editeur &lt;identifiant&gt; --nom &lt;nom&gt; (--telephone &lt;n°&gt; | --email &lt;adresse&gt;)   mot de passe provisoire affiché UNE fois
///   --comptes-distants                                                                       liste (sans empreinte ni contact complet)
///   --desactiver-compte &lt;identifiant&gt;                                                     sessions fermées
///   --deverrouiller-compte &lt;identifiant&gt;
/// </summary>
public static class ComptesDistantsAdministration
{
    public static readonly string[] Commandes = ["--creer-compte-editeur", "--comptes-distants", "--desactiver-compte", "--deverrouiller-compte"];

    public static bool Concerne(string commande) => Commandes.Contains(commande);

    /// <summary>Arguments de --creer-compte-editeur contrôlés (règles pures, testées) : identifiant, nom, téléphone, e-mail.</summary>
    public static (string Identifiant, string Nom, string? Telephone, string? Email) LireCreation(string[] args)
    {
        var identifiant = ComptesDistants.NormaliserIdentifiant(args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null);
        if (!ComptesDistants.IdentifiantValide(identifiant))
            throw new ArgumentException("Identifiant attendu : 3 à 60 caractères, minuscules, chiffres, « . _ - » (ex. editeur.dupont).");
        var nom = Option(args, "--nom")?.Trim();
        if (nom is not { Length: >= 1 and <= 120 }) throw new ArgumentException("Nom attendu : --nom <nom> (120 caractères au plus).");
        var telephone = Option(args, "--telephone")?.Replace(" ", "", StringComparison.Ordinal);
        var email = Option(args, "--email")?.Trim();
        if (telephone is null && email is null) throw new ArgumentException("Contact du second facteur attendu : --telephone <n°> ou --email <adresse>.");
        if (telephone is not null && !ComptesDistants.TelephoneValide(telephone)) throw new ArgumentException("Téléphone invalide : 8 à 15 chiffres, « + » permis en tête.");
        if (email is not null && !Depart.AdresseCourriel.EstValide(email)) throw new ArgumentException("Adresse e-mail invalide.");
        return (identifiant!, nom, telephone, email);
    }

    public static async Task<int> ExecuterAsync(NpgsqlDataSource db, string[] args, TextWriter sortie, CancellationToken ct = default)
    {
        switch (args[0])
        {
            case "--creer-compte-editeur":
                {
                    var (identifiant, nom, telephone, email) = LireCreation(args);
                    var provisoire = ComptesDistants.MotDePasseProvisoire();
                    await using var cmd = db.CreateCommand("SELECT cloud_compte_distant_creer($1, $2, 'editeur', $3, $4, $5, $6)");
                    cmd.Parameters.AddWithValue(identifiant);
                    cmd.Parameters.AddWithValue(nom);
                    cmd.Parameters.AddWithValue(BCrypt.Net.BCrypt.HashPassword(provisoire, ComptesDistants.CoutBcrypt));
                    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)telephone ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)email ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
                    cmd.Parameters.AddWithValue($"console {Environment.UserName}@{Environment.MachineName}");
                    try
                    {
                        await cmd.ExecuteScalarAsync(ct);
                    }
                    catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
                    {
                        throw new ArgumentException($"Le compte « {identifiant} » existe déjà.");
                    }
                    await sortie.WriteLineAsync($"Compte éditeur « {identifiant} » créé. Mot de passe PROVISOIRE (affiché une seule fois, à changer à la première connexion) :");
                    await sortie.WriteLineAsync(provisoire);
                    return 0;
                }
            case "--comptes-distants":
                {
                    await using var cmd = db.CreateCommand("""
                        SELECT identifiant, nom, role, actif, mot_de_passe_provisoire, verrouille_jusqu_au, derniere_connexion_le,
                               telephone IS NOT NULL, email IS NOT NULL,
                               (SELECT count(*) FROM session_distante s WHERE s.compte_id = c.id AND s.fin_le IS NULL AND s.expire_le > now())
                        FROM compte_distant c ORDER BY role, identifiant
                        """);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    var vu = false;
                    while (await r.ReadAsync(ct))
                    {
                        vu = true;
                        var etat = !r.GetBoolean(3) ? "DÉSACTIVÉ" : !r.IsDBNull(5) && r.GetDateTime(5) > DateTime.UtcNow ? $"bloqué jusqu'au {r.GetDateTime(5).ToLocalTime():yyyy-MM-dd HH:mm}" : "actif";
                        await sortie.WriteLineAsync($"{r.GetString(0),-24} {r.GetString(2),-10} {etat,-28} {(r.GetBoolean(4) ? "mot de passe provisoire" : "mot de passe choisi"),-24} "
                            + $"dernière connexion {(r.IsDBNull(6) ? "jamais" : r.GetDateTime(6).ToLocalTime().ToString("yyyy-MM-dd HH:mm"))} ; "
                            + $"second facteur : {(r.GetBoolean(8) ? "e-mail" : "")}{(r.GetBoolean(7) && r.GetBoolean(8) ? " + " : "")}{(r.GetBoolean(7) ? "téléphone" : "")} ; "
                            + $"sessions ouvertes : {r.GetInt64(9)} ; {r.GetString(1)}");
                    }
                    if (!vu) await sortie.WriteLineAsync("Aucun compte distant.");
                    return 0;
                }
            case "--desactiver-compte":
                {
                    var identifiant = Identifiant(args);
                    await using var cmd = db.CreateCommand("SELECT cloud_compte_distant_desactiver($1)");
                    cmd.Parameters.AddWithValue(identifiant);
                    var n = (int)(await cmd.ExecuteScalarAsync(ct))!;
                    await sortie.WriteLineAsync(n < 0 ? $"Compte « {identifiant} » inconnu ou déjà désactivé." : $"Compte « {identifiant} » désactivé ; sessions fermées : {n}.");
                    return n < 0 ? 1 : 0;
                }
            case "--deverrouiller-compte":
                {
                    var identifiant = Identifiant(args);
                    await using var cmd = db.CreateCommand("SELECT cloud_compte_distant_deverrouiller($1)");
                    cmd.Parameters.AddWithValue(identifiant);
                    var ok = (bool)(await cmd.ExecuteScalarAsync(ct))!;
                    await sortie.WriteLineAsync(ok ? $"Compte « {identifiant} » débloqué (compteur d'échecs remis à zéro)." : $"Compte « {identifiant} » inconnu.");
                    return ok ? 0 : 1;
                }
        }
        return 1;
    }

    private static string Identifiant(string[] args)
    {
        var identifiant = ComptesDistants.NormaliserIdentifiant(args.Length > 1 ? args[1] : null);
        return ComptesDistants.IdentifiantValide(identifiant) ? identifiant! : throw new ArgumentException($"Attendu : {args[0]} <identifiant>.");
    }

    private static string? Option(string[] args, string nom)
    {
        var i = Array.IndexOf(args, nom);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
