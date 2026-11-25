using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Pharma.Shared;

namespace Pharma.Desktop.Services;

/// <summary>Clé privée du poste dans le magasin de clés de Windows (CNG, clé machine non exportable). Doublure en mémoire dans les tests.</summary>
public interface ICleMachine
{
    /// <summary>Ouvre la clé nommée, null si elle n'existe pas (ou plus) sur ce PC.</summary>
    ECDsa? Ouvrir(string nom);
    /// <summary>Crée une nouvelle clé ECDSA P-256 ; rend la clé et le fournisseur utilisé (TPM ou logiciel).</summary>
    (ECDsa Cle, string Fournisseur) Creer(string nom);
    void Supprimer(string nom);
}

/// <summary>
/// Clé CNG machine (utilisable par tout compte Windows du PC), NON EXPORTABLE : fournisseur TPM (« Microsoft Platform Crypto Provider ») essayé d'abord,
/// sinon fournisseur logiciel (« Microsoft Software Key Storage Provider »). Droit d'utilisation donné aux utilisateurs authentifiés du PC (descripteur de
/// sécurité posé à la création, toujours : sans lui, un compte standard ne pourrait pas se servir d'une clé créée par un administrateur).
/// Constaté le 2026-11-24 : un compte Windows standard ne peut pas créer de clé machine (« Accès refusé ») ; la clé est alors créée par le logiciel relancé
/// en administrateur (demande d'élévation de Windows, <see cref="CreationCleAdministrateur"/>), puis ouverte par le compte courant (décision D4, option 1).
/// À vérifier à l'essai réel : utilisation par un compte standard d'une clé créée ainsi, durée d'une signature TPM.
/// </summary>
public sealed class CleMachineCng : ICleMachine
{
    public static readonly CngProvider Tpm = new("Microsoft Platform Crypto Provider");
    private static readonly CngProvider[] Fournisseurs = [Tpm, CngProvider.MicrosoftSoftwareKeyStorageProvider];

    // SYSTEM et Administrateurs : tout ; Utilisateurs authentifiés : lecture (utilisation de la clé pour signer)
    private const string Droits = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GR;;;AU)";
    private const CngPropertyOptions DaclSecurityInformation = (CngPropertyOptions)4;

    public ECDsa? Ouvrir(string nom)
    {
        foreach (var f in Fournisseurs)
        {
            try
            {
                if (!CngKey.Exists(nom, f, CngKeyOpenOptions.MachineKey)) continue;
                return new ECDsaCng(CngKey.Open(nom, f, CngKeyOpenOptions.MachineKey));
            }
            catch (CryptographicException) { /* fournisseur absent (pas de TPM) ou clé illisible : fournisseur suivant */ }
        }
        return null;
    }

    public (ECDsa Cle, string Fournisseur) Creer(string nom)
    {
        try { return CreerIci(nom); }
        catch (InvalidOperationException) when (!EstAdministrateur())
        {
            // Compte standard : la clé est créée par le logiciel relancé en administrateur, puis ouverte ici (droit de lecture des utilisateurs authentifiés)
            CreationCleAdministrateur.Lancer(nom);
            foreach (var f in Fournisseurs)
            {
                try
                {
                    if (CngKey.Exists(nom, f, CngKeyOpenOptions.MachineKey))
                        return (new ECDsaCng(CngKey.Open(nom, f, CngKeyOpenOptions.MachineKey)), f.Provider);
                }
                catch (CryptographicException) { /* fournisseur absent ou clé illisible : fournisseur suivant */ }
            }
            throw new InvalidOperationException(
                "La clé de ce poste n'a pas été créée, ou ce compte Windows ne peut pas s'en servir : réessayez, sinon prévenez le titulaire.");
        }
    }

    public static bool EstAdministrateur()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Travail du processus relancé en administrateur (« --creer-cle-poste ») : crée la clé, puis ouvre le dossier standard de l'identité
    /// (%ProgramData%\PharmaOfficine\Poste, jamais la racine ni le dossier de PHARMA_POSTE_IDENTITE) aux utilisateurs authentifiés : un compte Windows
    /// standard du PC peut y enregistrer l'identité du poste. Rend le code de sortie.
    /// </summary>
    public static int CreerEnAdministrateur(string nom)
    {
        try { CreerIci(nom).Cle.Dispose(); }
        catch (InvalidOperationException e) { App.NoterErreur(e); return CreationCleAdministrateur.Echec; }
        try
        {
            var dossier = new DirectoryInfo(IdentitePoste.DossierStandard());
            dossier.Create();
            var droits = dossier.GetAccessControl();
            droits.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            dossier.SetAccessControl(droits);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { App.NoterErreur(e); /* la clé est créée */ }
        return CreationCleAdministrateur.Reussi;
    }

    /// <summary>Crée la clé dans ce processus (droits d'administrateur nécessaires pour une clé machine). Échec : InvalidOperationException.</summary>
    public static (ECDsa Cle, string Fournisseur) CreerIci(string nom)
    {
        CryptographicException? derniere = null;
        foreach (var f in Fournisseurs)
        {
            try
            {
                var p = new CngKeyCreationParameters
                {
                    Provider = f,
                    KeyCreationOptions = CngKeyCreationOptions.MachineKey | CngKeyCreationOptions.OverwriteExistingKey,
                    ExportPolicy = CngExportPolicies.None,
                    KeyUsage = CngKeyUsages.Signing
                };
                var sd = new RawSecurityDescriptor(Droits);
                var octets = new byte[sd.BinaryLength];
                sd.GetBinaryForm(octets, 0);
                p.Parameters.Add(new CngProperty("Security Descr", octets, DaclSecurityInformation));
                return (new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsaP256, nom, p)), f.Provider);
            }
            catch (CryptographicException e) { derniere = e; }
        }
        throw new InvalidOperationException("Windows refuse de créer la clé de ce poste.", derniere);
    }

    public void Supprimer(string nom)
    {
        foreach (var f in Fournisseurs)
        {
            try
            {
                if (CngKey.Exists(nom, f, CngKeyOpenOptions.MachineKey)) CngKey.Open(nom, f, CngKeyOpenOptions.MachineKey).Delete();
            }
            catch (CryptographicException) { /* déjà absente, ou droits insuffisants : la clé n'est plus référencée */ }
        }
    }
}

/// <summary>Identité du poste enrôlé, gardée dans %ProgramData%\PharmaOfficine\Poste\poste-identite.json (rien de secret : la clé privée reste dans Windows).</summary>
public sealed record IdentitePosteFichier(long PosteId, string Nom, string Cle, string? Fournisseur);

/// <summary>
/// Identité du poste (liste des postes autorisés) : identifiant et nom donnés par le serveur à l'enrôlement, clé privée non exportable. Chaque enrôlement crée
/// une NOUVELLE clé (nom unique) : l'ancienne n'est supprimée qu'une fois le nouvel enrôlement accepté (un code faux ne casse pas le poste).
/// </summary>
public sealed class IdentitePoste
{
    public const string FichierNom = "poste-identite.json";
    private readonly ICleMachine cles;
    private readonly string fichier;
    private IdentitePosteFichier? identite;
    private ECDsa? cle;

    public IdentitePoste(ICleMachine? cles = null, string? dossier = null)
    {
        this.cles = cles ?? new CleMachineCng();
        fichier = Path.Combine(dossier ?? DossierParDefaut(), FichierNom);
        Charger();
    }

    /// <summary>
    /// %ProgramData%\PharmaOfficine\Poste, sauf variable PHARMA_POSTE_IDENTITE (dossier) : plusieurs postes d'essai sur un même PC, chacun avec son identité.
    /// </summary>
    public static string DossierParDefaut() =>
        Environment.GetEnvironmentVariable("PHARMA_POSTE_IDENTITE") is { Length: > 0 } dossier ? dossier : DossierStandard();

    /// <summary>
    /// %ProgramData%\PharmaOfficine\Poste : sous-dossier propre au poste. Jamais la racine %ProgramData%\PharmaOfficine, qui est aussi le dossier des
    /// données de l'API quand le PC serveur sert de poste (GED, journaux, clés) : seul ce sous-dossier est ouvert en modification aux comptes du PC.
    /// </summary>
    public static string DossierStandard() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PharmaOfficine", "Poste");

    public long? PosteId => Enrole ? identite!.PosteId : null;
    public string? Nom => Enrole ? identite!.Nom : null;
    public bool Enrole => identite is not null && cle is not null;
    public string? Fournisseur => identite?.Fournisseur;

    /// <summary>Dernier refus de preuve reçu du serveur (poste_revoque, poste_non_autorise, poste_non_prouve…), affiché sur l'écran de connexion.</summary>
    public string? DernierRefus { get; set; }

    public event Action? Changee;

    private void Charger()
    {
        try
        {
            if (!File.Exists(fichier)) return;
            identite = JsonSerializer.Deserialize<IdentitePosteFichier>(File.ReadAllText(fichier));
            cle = identite is { Cle.Length: > 0 } i ? cles.Ouvrir(i.Cle) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            App.NoterErreur(e);
            (identite, cle) = (null, null);
        }
    }

    /// <summary>Signature P1363 (base64url) de la chaîne, par la clé du poste. Poste non enrôlé : exception.</summary>
    public string Signer(string chaine) => PreuvePoste.Signer(cle ?? throw new InvalidOperationException("Poste non enrôlé."), chaine);

    /// <summary>Nouvelle clé pour un enrôlement : rend sa clé publique (SPKI), la clé pour signer la preuve de possession, son nom et son fournisseur.</summary>
    public (byte[] Spki, ECDsa Cle, string Nom, string Fournisseur) PreparerCle()
    {
        var nom = "PharmaOfficine-Poste-" + Guid.NewGuid().ToString("N")[..12];
        var (nouvelle, fournisseur) = cles.Creer(nom);
        return (nouvelle.ExportSubjectPublicKeyInfo(), nouvelle, nom, fournisseur);
    }

    /// <summary>Enrôlement accepté : la nouvelle clé devient celle du poste, l'ancienne est supprimée.</summary>
    public void Valider(long posteId, string nomPoste, string nomCle, ECDsa nouvelle, string fournisseur)
    {
        var ancienne = identite?.Cle;
        var lu = new IdentitePosteFichier(posteId, nomPoste, nomCle, fournisseur);
        Directory.CreateDirectory(Path.GetDirectoryName(fichier)!);
        File.WriteAllText(fichier, JsonSerializer.Serialize(lu));
        (identite, cle, DernierRefus) = (lu, nouvelle, null);
        if (ancienne is { Length: > 0 } && ancienne != nomCle) cles.Supprimer(ancienne);
        Changee?.Invoke();
    }

    /// <summary>Enrôlement refusé : la nouvelle clé est supprimée, l'identité d'avant reste.</summary>
    public void Abandonner(string nomCle, ECDsa nouvelle)
    {
        nouvelle.Dispose();
        cles.Supprimer(nomCle);
    }

    public void NoterRefus(string? code)
    {
        if (DernierRefus == code) return;
        DernierRefus = code;
        Changee?.Invoke();
    }
}

/// <summary>Heure du serveur vue du poste : décalage corrigé par l'en-tête Date des réponses et par l'heure jointe à un refus « preuve_horloge ».</summary>
public sealed class HorlogePoste
{
    private long decalage;   // secondes à ajouter à l'heure du PC

    public long Unix => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Interlocked.Read(ref decalage);

    public long Decalage => Interlocked.Read(ref decalage);

    /// <summary>Heure du serveur connue : le décalage est corrigé s'il dépasse 5 secondes (l'en-tête Date est à la seconde près).</summary>
    public void Corriger(long serveurUnix)
    {
        var d = serveurUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(d - Interlocked.Read(ref decalage)) > 5) Interlocked.Exchange(ref decalage, d);
    }
}

/// <summary>
/// Signe chaque requête du poste enrôlé (en-têtes X-Pharma-Poste-*, contrat § 1) : méthode, chemin + requête tels qu'envoyés, heure du serveur, nonce,
/// identifiant du poste, empreinte de l'en-tête Authorization. Corrige l'horloge avec l'en-tête Date des réponses ; un refus « preuve_horloge » est rejoué UNE fois
/// après correction si la requête n'a pas de corps ou un corps déjà en mémoire. Note les refus de preuve pour l'écran de connexion.
/// </summary>
public sealed class SignaturePoste(IdentitePoste identite, HorlogePoste horloge, HttpMessageHandler interne) : DelegatingHandler(interne)
{
    private static readonly string[] CodesPoste =
        [PreuvePoste.PosteNonProuve, PreuvePoste.PosteRevoque, PreuvePoste.PosteNonAutorise, PreuvePoste.PreuveHorloge];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage requete, CancellationToken ct)
    {
        Signer(requete);
        var rep = await base.SendAsync(requete, ct);
        if (rep.Headers.Date is { } date) horloge.Corriger(date.ToUnixTimeSeconds());
        if (rep.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)) return rep;

        if (rep.Headers.TryGetValues(PreuvePoste.EnTeteHeureServeur, out var heures) && long.TryParse(heures.FirstOrDefault(), out var heure)
            && identite.Enrole && await CloneAsync(requete) is { } nouvelle)
        {
            horloge.Corriger(heure);
            rep.Dispose();
            Signer(nouvelle);
            rep = await base.SendAsync(nouvelle, ct);
        }
        await NoterRefusAsync(rep);
        return rep;
    }

    /// <summary>Pose les en-têtes de preuve (poste enrôlé seulement ; jamais sur l'enrôlement, qui porte sa propre preuve).</summary>
    public void Signer(HttpRequestMessage requete)
    {
        foreach (var n in new[] { PreuvePoste.EnTeteId, PreuvePoste.EnTeteHorodatage, PreuvePoste.EnTeteNonce, PreuvePoste.EnTetePreuve }) requete.Headers.Remove(n);
        if (!identite.Enrole || identite.PosteId is not { } id || requete.RequestUri is not { IsAbsoluteUri: true } uri) return;
        if (uri.AbsolutePath.StartsWith("/api/postes/enrolement", StringComparison.OrdinalIgnoreCase)) return;
        var horodatage = horloge.Unix;
        var nonce = PreuvePoste.NouveauNonce();
        var chaine = PreuvePoste.Chaine(requete.Method.Method, uri.PathAndQuery, horodatage, nonce, id, requete.Headers.Authorization?.ToString());
        requete.Headers.TryAddWithoutValidation(PreuvePoste.EnTeteId, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        requete.Headers.TryAddWithoutValidation(PreuvePoste.EnTeteHorodatage, horodatage.ToString(System.Globalization.CultureInfo.InvariantCulture));
        requete.Headers.TryAddWithoutValidation(PreuvePoste.EnTeteNonce, nonce);
        requete.Headers.TryAddWithoutValidation(PreuvePoste.EnTetePreuve, identite.Signer(chaine));
    }

    private static async Task<HttpRequestMessage?> CloneAsync(HttpRequestMessage r)
    {
        if (r.Content is not (null or ByteArrayContent)) return null;   // corps en flux : pas de nouvel essai (l'appelant voit le refus)
        var c = new HttpRequestMessage(r.Method, r.RequestUri) { Version = r.Version };
        foreach (var h in r.Headers) c.Headers.TryAddWithoutValidation(h.Key, h.Value);
        foreach (var o in r.Options) ((IDictionary<string, object?>)c.Options)[o.Key] = o.Value;
        if (r.Content is { } contenu)
        {
            c.Content = new ByteArrayContent(await contenu.ReadAsByteArrayAsync());
            foreach (var h in contenu.Headers) c.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        return c;
    }

    private async Task NoterRefusAsync(HttpResponseMessage rep)
    {
        try
        {
            if (rep.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true) return;
            await rep.Content.LoadIntoBufferAsync();
            using var doc = JsonDocument.Parse(await rep.Content.ReadAsStringAsync());
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("code", out var c) && c.GetString() is { } code
                && CodesPoste.Contains(code))
                identite.NoterRefus(code);
        }
        catch (Exception e) when (e is JsonException or HttpRequestException or InvalidOperationException) { /* corps illisible : rien à noter */ }
    }
}
