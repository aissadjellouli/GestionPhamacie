using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pharma.Shared;

/// <summary>Ligne d'un fichier d'import de comptes distants (n° = ligne physique du fichier, en-tête = 1) et son statut (null = valable).</summary>
public sealed record LigneCompteImport(int Numero, string Role, string Identifiant, string Nom, string? Email, string? Telephone, string? Statut = null)
{
    public bool Valable => Statut is null or "ok";
}

/// <summary>Résultat de la lecture d'un fichier : lignes (chacune avec son statut) ou refus du fichier entier (clé de message).</summary>
public sealed record LectureImportComptes(IReadOnlyList<LigneCompteImport> Lignes, string? Refus)
{
    public bool Valable => Refus is null && Lignes.Count > 0 && Lignes.All(l => l.Valable);
}

/// <summary>
/// Script central 040 : import de comptes distants par fichier CSV (écran « Comptes » de l'éditeur et console --creer-comptes).
/// Colonnes : role ; identifiant ; nom ; email ; telephone (ordre libre, ligne d'en-tête obligatoire, « rôle », « téléphone »,
/// « e-mail » ou « courriel » acceptés) ; séparateur « ; » ou « , » (celui de l'en-tête) ; champs entre guillemets permis (« "" » =
/// guillemet) ; UTF-8 avec ou sans BOM ; 200 lignes au plus ; lignes vides ignorées. Mêmes contrôles que la base (statuts du
/// script 040 : identifiant, doublon, nom, role, contact, email, telephone ; « colonnes » si le nombre de champs diffère de
/// l'en-tête) ; la base revérifie tout et ajoute « existe » (identifiant déjà pris).
/// </summary>
public static partial class ImportComptesCsv
{
    public const int LignesMax = 200;
    public const int OctetsMax = 128 * 1024;
    public static readonly string[] Colonnes = ["role", "identifiant", "nom", "email", "telephone"];

    /// <summary>Statuts d'une ligne (même ordre que la base) et refus d'un fichier : clés des messages des écrans et de la console.</summary>
    public static readonly string[] StatutsLigne = ["colonnes", "forme", "identifiant", "doublon", "existe", "nom", "role", "contact", "email", "telephone"];
    public static readonly string[] RefusFichier = ["vide", "taille", "encodage", "entete", "trop_de_lignes"];

    /// <summary>Explication de chaque statut de ligne (aperçu des écrans, sortie de la console).</summary>
    public static readonly IReadOnlyDictionary<string, string> LibellesStatuts = new Dictionary<string, string>
    {
        ["colonnes"] = "nombre de colonnes différent de l'en-tête",
        ["forme"] = "guillemet non fermé",
        ["identifiant"] = "identifiant invalide (3 à 60 caractères : minuscules, chiffres, « . _ - »)",
        ["doublon"] = "identifiant déjà présent plus haut dans le fichier",
        ["existe"] = "identifiant déjà pris",
        ["nom"] = "nom obligatoire (120 caractères au plus)",
        ["role"] = "rôle : editeur ou titulaire",
        ["contact"] = "ni e-mail ni téléphone",
        ["email"] = "e-mail invalide",
        ["telephone"] = "téléphone invalide (8 à 15 chiffres, « + » permis en tête)",
    };

    /// <summary>Explication des refus d'un fichier entier (console ; les écrans ont leurs messages import_*).</summary>
    public static readonly IReadOnlyDictionary<string, string> LibellesRefus = new Dictionary<string, string>
    {
        ["vide"] = "fichier vide (ligne d'en-tête puis une ligne par compte)",
        ["taille"] = "fichier trop gros (128 Ko au plus)",
        ["encodage"] = "fichier illisible : enregistrez-le en UTF-8",
        ["entete"] = "en-tête invalide : role ; identifiant ; nom ; email ; telephone (séparateur « ; » ou « , »)",
        ["trop_de_lignes"] = "plus de 200 comptes",
    };

    public static LectureImportComptes Lire(byte[]? contenu)
    {
        if (contenu is null || contenu.Length == 0) return Refus("vide");
        if (contenu.Length > OctetsMax) return Refus("taille");
        string texte;
        try
        {
            var debut = contenu.Length >= 3 && contenu[0] == 0xEF && contenu[1] == 0xBB && contenu[2] == 0xBF ? 3 : 0;
            texte = new UTF8Encoding(false, true).GetString(contenu, debut, contenu.Length - debut);
        }
        catch (DecoderFallbackException)
        {
            return Refus("encodage");
        }
        var lignes = texte.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var iEntete = Array.FindIndex(lignes, l => l.Trim().Length > 0);
        if (iEntete < 0) return Refus("vide");
        var entete = lignes[iEntete];
        var separateur = entete.Contains(';') ? ';' : entete.Contains(',') ? ',' : '\0';
        if (separateur == '\0' || Champs(entete, separateur) is not { } noms) return Refus("entete");
        var colonnes = noms.Select(NomColonne).ToList();
        if (colonnes.Count != Colonnes.Length || Colonnes.Any(c => colonnes.Count(x => x == c) != 1)) return Refus("entete");

        var resultat = new List<LigneCompteImport>();
        for (var i = iEntete + 1; i < lignes.Length; i++)
        {
            if (lignes[i].Trim().Length == 0) continue;
            if (resultat.Count == LignesMax) return Refus("trop_de_lignes");
            var numero = i + 1;
            var champs = Champs(lignes[i], separateur);
            if (champs is null || champs.Count != colonnes.Count)
            {
                resultat.Add(new LigneCompteImport(numero, "", "", "", null, null, champs is null ? "forme" : "colonnes"));
                continue;
            }
            string V(string c) => champs[colonnes.IndexOf(c)].Trim();
            resultat.Add(Normaliser(numero, V("role"), V("identifiant"), V("nom"), V("email"), V("telephone")));
        }
        if (resultat.Count == 0) return Refus("vide");
        // Doublons dans le fichier : chaque occurrence après la première
        var vus = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < resultat.Count; i++)
        {
            var l = resultat[i];
            if (l.Statut is "colonnes" or "forme" or "identifiant") continue;
            if (!vus.Add(l.Identifiant)) resultat[i] = l with { Statut = "doublon" };
        }
        return new LectureImportComptes(resultat, null);
    }

    /// <summary>Ligne normalisée (identifiant en minuscules, rôle sans accent, téléphone sans espace) et son premier refus (ordre de la base).</summary>
    public static LigneCompteImport Normaliser(int numero, string role, string identifiant, string nom, string? email, string? telephone)
    {
        var r = SansAccent(role.Trim()).ToLowerInvariant();
        var id = ComptesDistants.NormaliserIdentifiant(identifiant) ?? "";
        var n = nom.Trim();
        var e = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        var t = string.IsNullOrWhiteSpace(telephone) ? null : telephone.Replace(" ", "", StringComparison.Ordinal).Trim();
        if (t is { Length: 0 }) t = null;
        string? statut = !ComptesDistants.IdentifiantValide(id) ? "identifiant"
            : n.Length is < 1 or > 120 || n.Any(char.IsControl) ? "nom"
            : r is not ("editeur" or "titulaire") ? "role"
            : e is null && t is null ? "contact"
            : e is not null && !EmailValide(e) ? "email"
            : t is not null && !ComptesDistants.TelephoneValide(t) ? "telephone"
            : null;
        return new LigneCompteImport(numero, r, id, n, e, t, statut);
    }

    /// <summary>Forme d'adresse de la base (contrainte de compte_distant) : 254 caractères au plus, une arobase, un point dans le domaine.</summary>
    public static bool EmailValide(string? email) => email is { Length: > 0 and <= 254 } && Email().IsMatch(email);

    /// <summary>Lignes envoyées à la base (tableau JSON du script 040), avec l'empreinte BCrypt de chaque ligne à la création.</summary>
    public static string Json(IEnumerable<LigneCompteImport> lignes, IReadOnlyDictionary<int, string>? empreintes = null) =>
        JsonSerializer.Serialize(lignes.Select(l => new Dictionary<string, object?>
        {
            ["ligne"] = l.Numero, ["role"] = l.Role, ["identifiant"] = l.Identifiant, ["nom"] = l.Nom, ["email"] = l.Email,
            ["telephone"] = l.Telephone, ["empreinte"] = empreintes is not null && empreintes.TryGetValue(l.Numero, out var e) ? e : null,
        }));

    /// <summary>Statut d'une ligne après l'avis de la base : le refus de l'application (forme) prime, sinon celui de la base.</summary>
    public static IReadOnlyList<LigneCompteImport> Fusionner(IReadOnlyList<LigneCompteImport> lignes, IReadOnlyDictionary<int, string> base_) =>
        lignes.Select(l => l.Valable && base_.TryGetValue(l.Numero, out var s) ? l with { Statut = s == "ok" ? null : s } : l).ToList();

    private static LectureImportComptes Refus(string cle) => new([], cle);

    private static string NomColonne(string nom)
    {
        var n = SansAccent(nom.Trim().Trim('"')).ToLowerInvariant().Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal);
        return n switch { "courriel" or "mail" => "email", "tel" => "telephone", _ => n };
    }

    private static string SansAccent(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        return new string(d.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).Normalize(NormalizationForm.FormC);
    }

    /// <summary>Champs d'une ligne (guillemets doubles permis, « "" » = guillemet) ; null si un guillemet n'est pas fermé.</summary>
    private static List<string>? Champs(string ligne, char separateur)
    {
        var champs = new List<string>();
        var courant = new StringBuilder();
        var entre = false;
        for (var i = 0; i < ligne.Length; i++)
        {
            var c = ligne[i];
            if (entre)
            {
                if (c == '"' && i + 1 < ligne.Length && ligne[i + 1] == '"') { courant.Append('"'); i++; }
                else if (c == '"') entre = false;
                else courant.Append(c);
            }
            else if (c == '"' && courant.ToString().Trim().Length == 0) { courant.Clear(); entre = true; }
            else if (c == separateur) { champs.Add(courant.ToString()); courant.Clear(); }
            else courant.Append(c);
        }
        if (entre) return null;
        champs.Add(courant.ToString());
        return champs;
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex Email();
}

/// <summary>
/// Script central 040 : e-mail de l'IDENTIFIANT d'un compte distant (création, import, remise à zéro). Jamais le mot de passe :
/// il est transmis séparément (SMS si un fournisseur est en service, sinon par l'éditeur).
/// </summary>
public static class MessageIdentifiant
{
    public const string Objet = "Votre compte des écrans du serveur central";
    public const string PhraseMotDePasse = "Le mot de passe provisoire vous sera transmis séparément";
    public const string AdresseInconnue = "l'adresse vous sera communiquée par l'éditeur";

    /// <summary>Adresse des écrans gardée seulement si c'est une adresse https absolue (sinon texte générique).</summary>
    public static string? Adresse(string? adresse) =>
        Uri.TryCreate(adresse?.Trim(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(u.UserInfo) ? u.ToString() : null;

    public static string Texte(string identifiant, string role, string? adresseEcrans, bool remise) =>
        $"""
        Bonjour,

        {(remise ? "Le mot de passe de votre compte des écrans du serveur central a été remis à zéro par l'éditeur." : "Un compte vous a été ouvert sur les écrans du serveur central.")}

        Identifiant : {identifiant}
        Rôle : {(role == "editeur" ? "éditeur (administrateur)" : "titulaire")}
        Adresse des écrans : {Adresse(adresseEcrans) ?? AdresseInconnue}

        {PhraseMotDePasse}. Il est à changer à la première connexion et n'est valable que quelques jours : passé ce délai, demandez à l'éditeur d'en régénérer un.
        À chaque connexion, un code de confirmation vous est aussi envoyé.

        Si vous n'attendiez pas ce message, prévenez l'éditeur.
        """;
}

/// <summary>
/// Envoi du mot de passe provisoire par SMS : interface prévue, AUCUN fournisseur retenu aujourd'hui (mot de passe « à transmettre par
/// SMS (manuel) »). Jamais le mot de passe dans un journal.
/// </summary>
public interface IEnvoiSmsMotDePasse
{
    bool Disponible { get; }

    Task EnvoyerAsync(string telephone, string identifiant, string motDePasse, CancellationToken ct);
}

public sealed class EnvoiSmsMotDePasseIndisponible : IEnvoiSmsMotDePasse
{
    public bool Disponible => false;

    public Task EnvoyerAsync(string telephone, string identifiant, string motDePasse, CancellationToken ct) =>
        throw new NotSupportedException("Envoi par SMS non disponible : aucun fournisseur retenu.");
}

/// <summary>Compte à transmettre (créé, importé ou remis à zéro) : mot de passe provisoire en mémoire seulement.</summary>
public sealed record CompteATransmettre(Guid Compte, string Identifiant, string Role, string? Email, string? Telephone, string MotDePasse);

/// <summary>
/// Compte transmis, montré UNE fois à l'éditeur. Email : ok, echec, non_demande, sans_adresse. Sms : envoye, manuel,
/// manuel_sans_telephone, echec. Jamais en base ni au journal.
/// </summary>
public sealed record CompteTransmis(Guid Compte, string Identifiant, string Role, string MotDePasse, string Email, string Sms)
{
    public string EmailTexte => Email switch
    {
        "ok" => "oui", "echec" => "échec", "non_demande" => "non (case décochée)", "sans_adresse" => "non (aucune adresse)", _ => "non",
    };

    public string SmsTexte => Sms switch
    {
        "envoye" => "envoyé par SMS",
        "echec" => "échec du SMS : à transmettre par SMS (manuel)",
        "manuel_sans_telephone" => "à transmettre (manuel, aucun téléphone)",
        _ => "à transmettre par SMS (manuel)",
    };

    public string RoleTexte => Role == "editeur" ? "Éditeur" : "Titulaire";

    /// <summary>Ligne du texte copié (tabulations) : identifiant, rôle, mot de passe, identifiant par e-mail, mot de passe.</summary>
    public string Copie => $"{Identifiant}\t{RoleTexte}\t{MotDePasse}\tidentifiant par e-mail : {EmailTexte}\tmot de passe : {SmsTexte}";

    public override string ToString() => $"{Identifiant} ({Role}) : e-mail {Email}, SMS {Sms}, mot de passe masqué";
}

/// <summary>
/// Transmission des comptes (écrans et console) : identifiant par e-mail si demandé et possible (le mot de passe ne part JAMAIS par
/// e-mail) ; mot de passe par SMS si le compte a un téléphone et qu'un fournisseur est en service, sinon « manuel ». Un échec
/// d'envoi n'annule rien : il est signalé dans la liste. noter : résultat de chaque envoi tenté (événement, objet de la liste fermée
/// du script 040), jamais l'adresse, le numéro ni le mot de passe.
/// </summary>
public static class TransmissionComptes
{
    public const int EnvoisSimultanes = 4;

    /// <summary>Délai propre de CHAQUE envoi (e-mail ou SMS) : au-delà, « echec » pour ce compte, les autres continuent.</summary>
    public static readonly TimeSpan DelaiParEnvoi = TimeSpan.FromSeconds(30);

    public static async Task<IReadOnlyList<CompteTransmis>> TransmettreAsync(IReadOnlyList<CompteATransmettre> comptes, bool envoyerIdentifiant,
        Func<string, string, string, CancellationToken, Task>? courriel, IEnvoiSmsMotDePasse sms, string? adresseEcrans, bool remise,
        Func<Guid, string, string, CancellationToken, Task>? noter, CancellationToken ct, TimeSpan? delaiParEnvoi = null)
    {
        var delai = delaiParEnvoi ?? DelaiParEnvoi;
        var resultats = new CompteTransmis[comptes.Count];
        var notes = new List<(Guid, string, string)>();
        await Parallel.ForEachAsync(Enumerable.Range(0, comptes.Count), new ParallelOptions { MaxDegreeOfParallelism = EnvoisSimultanes, CancellationToken = ct },
            async (i, jeton) =>
            {
                var c = comptes[i];
                string email;
                if (!envoyerIdentifiant) email = "non_demande";
                else if (c.Email is null) email = "sans_adresse";
                else if (courriel is null) email = "echec";
                else
                {
                    try
                    {
                        using var unite = CancellationTokenSource.CreateLinkedTokenSource(jeton);
                        unite.CancelAfter(delai);
                        await courriel(c.Email, MessageIdentifiant.Objet, MessageIdentifiant.Texte(c.Identifiant, c.Role, adresseEcrans, remise), unite.Token);
                        email = "ok";
                    }
                    catch (OperationCanceledException) when (!jeton.IsCancellationRequested)
                    {
                        email = "echec";   // délai de cet envoi dépassé
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        email = "echec";
                    }
                }
                if (email is "ok" or "echec") lock (notes) notes.Add((c.Compte, "identifiant_envoi", "email:" + email));
                string texto;
                if (c.Telephone is null) texto = "manuel_sans_telephone";
                else if (!sms.Disponible) texto = "manuel";
                else
                {
                    try
                    {
                        using var unite = CancellationTokenSource.CreateLinkedTokenSource(jeton);
                        unite.CancelAfter(delai);
                        await sms.EnvoyerAsync(c.Telephone, c.Identifiant, c.MotDePasse, unite.Token);
                        texto = "envoye";
                    }
                    catch (NotSupportedException)
                    {
                        texto = "manuel";
                    }
                    catch (OperationCanceledException) when (!jeton.IsCancellationRequested)
                    {
                        texto = "echec";   // délai de cet envoi dépassé
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        texto = "echec";
                    }
                    lock (notes) notes.Add((c.Compte, "provisoire_envoi", "sms:" + (texto == "envoye" ? "ok" : texto == "manuel" ? "indisponible" : "echec")));
                }
                resultats[i] = new CompteTransmis(c.Compte, c.Identifiant, c.Role, c.MotDePasse, email, texto);
            });
        if (noter is not null)
            foreach (var (compte, evenement, objet) in notes) await noter(compte, evenement, objet, ct);
        return resultats;
    }
}
