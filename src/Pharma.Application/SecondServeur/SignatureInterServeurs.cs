using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Pharma.Application.SecondServeur;

// ---- Bascule du 2e serveur, vague 2, lot V2-2 : signature entre serveurs (contrat-api.md § 4) --------------------------------------
// Les deux serveurs partagent Jwt:Cle. La clé de signature en est DÉRIVÉE (HKDF-SHA256, étiquette propre) : Jwt:Cle n'est jamais utilisée
// telle quelle, et ni la clé, ni sa dérivée, ni leurs octets ne sortent de cette classe (journal, réponse, message d'erreur).

/// <summary>En-têtes reçus, tels quels (null = absent). Valeurs d'un correspondant non encore authentifié : à ne jamais journaliser brutes.</summary>
public sealed record EnTetesRecus(string? Serveur, string? Horodatage, string? Nonce, string? Signature);

/// <summary>En-têtes à poser sur une requête ou une réponse signée.</summary>
public sealed record EnTetesSignes(string Serveur, string Horodatage, string Nonce, string Signature);

public enum RefusSignature
{
    /// <summary>En-tête absent, vide, de format invalide ou nom de serveur douteux.</summary>
    EnTetesInvalides,
    /// <summary>Écart d'horloge supérieur à la fenêtre (±60 s par défaut).</summary>
    HorodatageHorsFenetre,
    /// <summary>Signature fausse : autre clé Jwt:Cle, message modifié, ou fabrication.</summary>
    SignatureFausse,
    /// <summary>Nonce déjà vu dans la durée de conservation (rejeu).</summary>
    NonceRejoue
}

/// <summary>Dérivations de clés à partir de Jwt:Cle (HKDF-SHA256, 32 octets, sans sel).</summary>
public static class CleInterServeurs
{
    public const string InfoSignature = "pharmaofficine/inter-serveurs/v1";
    public const string InfoEmpreinte = "pharmaofficine/empreinte-jeton/v1";
    public const string MessageEmpreinte = "empreinte-jeton";
    public const int Longueur = 32;

    public static byte[] Deriver(string jwtCle, string info)
    {
        if (string.IsNullOrWhiteSpace(jwtCle)) throw new ArgumentException("Clé des jetons absente.", nameof(jwtCle));
        var secret = Encoding.UTF8.GetBytes(jwtCle);
        try { return HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, Longueur, salt: [], info: Encoding.UTF8.GetBytes(info)); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    /// <summary>
    /// Empreinte de la clé des jetons (§ 4.2) : 16 caractères hexadécimaux minuscules, HMAC d'une étiquette fixe par une clé dérivée.
    /// Permet de comparer deux serveurs sans divulguer la clé (jamais un hachage brut de la clé, qui se tenterait par dictionnaire).
    /// </summary>
    public static string Empreinte(string jwtCle)
    {
        var cle = Deriver(jwtCle, InfoEmpreinte);
        try { return Convert.ToHexStringLower(HMACSHA256.HashData(cle, Encoding.UTF8.GetBytes(MessageEmpreinte)).AsSpan(0, 8)); }
        finally { CryptographicOperations.ZeroMemory(cle); }
    }

    /// <summary>Comparaison en temps constant de deux empreintes (null d'un côté : jamais égales).</summary>
    public static bool EmpreintesEgales(string? a, string? b) =>
        a is { Length: > 0 } && b is { Length: > 0 }
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

/// <summary>Cache mémoire des nonces déjà vus : un nonce n'est accepté qu'une fois dans sa durée de conservation.</summary>
public sealed class CacheNonces(TimeSpan conservation, int capaciteMax = 200_000)
{
    private readonly ConcurrentDictionary<string, long> _vus = new(StringComparer.Ordinal);
    private long _dernierNettoyage;

    /// <summary>Vrai si le nonce est nouveau (et le retient) ; faux s'il a déjà été vu ou si le cache est plein.</summary>
    public bool TryEnregistrer(string nonce, DateTimeOffset maintenant)
    {
        var t = maintenant.ToUnixTimeSeconds();
        var expire = t + (long)conservation.TotalSeconds;
        if (t - Interlocked.Read(ref _dernierNettoyage) >= 30 || _vus.Count >= capaciteMax) Nettoyer(t);
        if (_vus.Count >= capaciteMax) return false;   // plein de nonces valides (clé connue de l'appelant) : refus plutôt qu'oubli
        if (_vus.TryAdd(nonce, expire)) return true;
        // Présent mais expiré (nettoyage non encore passé) : remplacé de façon atomique
        return _vus.TryGetValue(nonce, out var ancien) && ancien <= t && _vus.TryUpdate(nonce, expire, ancien);
    }

    public int Taille => _vus.Count;

    private void Nettoyer(long t)
    {
        Interlocked.Exchange(ref _dernierNettoyage, t);
        foreach (var (nonce, expire) in _vus)
            if (expire <= t) _vus.TryRemove(new KeyValuePair<string, long>(nonce, expire));
    }
}

/// <summary>
/// Signature et vérification des requêtes et réponses entre les deux serveurs (HMAC-SHA256). Verrouillée par le contrat : chaînes canoniques,
/// en-têtes, fenêtre d'horloge, cache de nonces. Une instance par serveur (singleton).
/// </summary>
public sealed class SignatureInterServeurs
{
    public const string PrefixeRequete = "PHARMA-INTER-V1";
    public const string PrefixeReponse = "PHARMA-INTER-V1-REPONSE";
    public const int LongueurNomMax = 100;
    public const int OctetsNonce = 16;
    public const int FenetreMin = 30, FenetreMax = 300;

    private readonly byte[] _cle;
    private readonly TimeProvider _temps;
    private readonly CacheNonces _nonces;

    public string NomServeur { get; }
    public int FenetreSecondes { get; }
    /// <summary>Empreinte publique de Jwt:Cle (§ 4.2) : sans danger à exposer.</summary>
    public string EmpreinteJeton { get; }

    public SignatureInterServeurs(string jwtCle, string nomServeur, int fenetreSecondes = 60, TimeProvider? temps = null)
    {
        if (!NomValide(nomServeur)) throw new ArgumentException("Nom de serveur invalide.", nameof(nomServeur));
        _cle = CleInterServeurs.Deriver(jwtCle, CleInterServeurs.InfoSignature);
        EmpreinteJeton = CleInterServeurs.Empreinte(jwtCle);
        NomServeur = nomServeur;
        FenetreSecondes = Math.Clamp(fenetreSecondes, FenetreMin, FenetreMax);
        _temps = temps ?? TimeProvider.System;
        // Un nonce doit rester retenu tant que son horodatage serait encore accepté (2 fenêtres), et au moins 5 min (contrat)
        _nonces = new CacheNonces(TimeSpan.FromSeconds(Math.Max(5 * 60, 2 * FenetreSecondes + 5)));
    }

    // ---- Requêtes ----
    public EnTetesSignes SignerRequete(string methode, string cheminEtRequete, ReadOnlySpan<byte> corps)
    {
        var horodatage = Maintenant().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(OctetsNonce));
        return new EnTetesSignes(NomServeur, horodatage, nonce, Signer(ChaineRequete(methode, cheminEtRequete, horodatage, nonce, corps, NomServeur)));
    }

    /// <summary>Null = requête authentique et neuve (son nonce est alors retenu). L'ordre limite le travail d'un inconnu : format, fenêtre, signature, puis seulement le cache.</summary>
    public RefusSignature? VerifierRequete(string methode, string cheminEtRequete, EnTetesRecus e, ReadOnlySpan<byte> corps)
    {
        if (!FormatValide(e, out var horodatage)) return RefusSignature.EnTetesInvalides;
        if (!DansLaFenetre(horodatage)) return RefusSignature.HorodatageHorsFenetre;
        var attendu = Signer(ChaineRequete(methode, cheminEtRequete, e.Horodatage!, e.Nonce!, corps, e.Serveur!));
        if (!SignatureEgale(attendu, e.Signature!)) return RefusSignature.SignatureFausse;
        // Seuls les nonces d'appels authentiques sont retenus : un inconnu ne peut pas remplir le cache
        return _nonces.TryEnregistrer(e.Nonce!, Maintenant()) ? null : RefusSignature.NonceRejoue;
    }

    // ---- Réponses ----
    /// <summary>En-têtes d'une réponse : le nonce renvoyé est celui de la requête (la réponse lui est liée), l'horodatage est celui de la réponse.</summary>
    public EnTetesSignes SignerReponse(int statut, string nonceRequete, ReadOnlySpan<byte> corps)
    {
        var horodatage = Maintenant().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new EnTetesSignes(NomServeur, horodatage, nonceRequete, Signer(ChaineReponse(statut, nonceRequete, horodatage, corps, NomServeur)));
    }

    /// <summary>Null = réponse authentique, récente et liée à CETTE requête (le nonce renvoyé doit être celui envoyé).</summary>
    public RefusSignature? VerifierReponse(int statut, string nonceRequete, EnTetesRecus e, ReadOnlySpan<byte> corps)
    {
        if (!FormatValide(e, out var horodatage) || !string.Equals(e.Nonce, nonceRequete, StringComparison.Ordinal)) return RefusSignature.EnTetesInvalides;
        if (!DansLaFenetre(horodatage)) return RefusSignature.HorodatageHorsFenetre;
        var attendu = Signer(ChaineReponse(statut, nonceRequete, e.Horodatage!, corps, e.Serveur!));
        return SignatureEgale(attendu, e.Signature!) ? null : RefusSignature.SignatureFausse;
    }

    // ---- Chaînes canoniques (contrat § 4) ----
    public static string ChaineRequete(string methode, string cheminEtRequete, string horodatage, string nonce, ReadOnlySpan<byte> corps, string serveur) =>
        string.Join('\n', PrefixeRequete, methode.ToUpperInvariant(), cheminEtRequete, horodatage, nonce, HachageCorps(corps), serveur);

    public static string ChaineReponse(int statut, string nonceRequete, string horodatage, ReadOnlySpan<byte> corps, string serveur) =>
        string.Join('\n', PrefixeReponse, statut.ToString(System.Globalization.CultureInfo.InvariantCulture), nonceRequete, horodatage, HachageCorps(corps), serveur);

    public static string HachageCorps(ReadOnlySpan<byte> corps) => Convert.ToHexStringLower(SHA256.HashData(corps));

    /// <summary>Nom de serveur : 1 à 100 caractères, sans caractère de contrôle (le séparateur de la chaîne canonique est « \n »).</summary>
    public static bool NomValide(string? nom) => !string.IsNullOrWhiteSpace(nom) && nom.Length <= LongueurNomMax && !nom.Any(char.IsControl);

    private string Signer(string chaine) => Base64Url.EncodeToString(HMACSHA256.HashData(_cle, Encoding.UTF8.GetBytes(chaine)));

    /// <summary>Comparaison à temps constant sur les octets décodés (une signature mal formée est fausse, sans exception).</summary>
    private static bool SignatureEgale(string attendue, string recue)
    {
        var a = Base64Url.DecodeFromChars(attendue);
        Span<byte> r = stackalloc byte[32];
        if (recue.Length > 64 || !Base64Url.IsValid(recue) || !Base64Url.TryDecodeFromChars(recue, r, out var n) || n != 32) return false;
        return CryptographicOperations.FixedTimeEquals(a, r[..n]);
    }

    private bool FormatValide(EnTetesRecus e, out long horodatage)
    {
        horodatage = 0;
        if (!NomValide(e.Serveur) || string.IsNullOrEmpty(e.Horodatage) || e.Horodatage.Length > 12 || string.IsNullOrEmpty(e.Nonce) || e.Nonce.Length != 22
            || string.IsNullOrEmpty(e.Signature) || e.Signature.Length > 64) return false;
        if (!long.TryParse(e.Horodatage, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out horodatage)) return false;
        Span<byte> nonce = stackalloc byte[OctetsNonce + 2];
        return Base64Url.IsValid(e.Nonce) && Base64Url.TryDecodeFromChars(e.Nonce, nonce, out var n) && n == OctetsNonce;
    }

    private bool DansLaFenetre(long horodatage) => Math.Abs(Maintenant().ToUnixTimeSeconds() - horodatage) <= FenetreSecondes;

    private DateTimeOffset Maintenant() => _temps.GetUtcNow();
}

/// <summary>Contrôle de l'adresse de l'appelant : réseau privé seulement ET adresse déclarée de l'autre serveur (SecondServeur:AutreServeur).</summary>
public sealed class ControleAppelantInterServeurs(string? autreServeur, Func<string, CancellationToken, Task<IPAddress[]>>? resoudre = null, TimeProvider? temps = null)
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resoudre = resoudre ?? ((h, ct) => Dns.GetHostAddressesAsync(h, ct));
    private readonly TimeProvider _temps = temps ?? TimeProvider.System;
    private IPAddress[] _adresses = [];
    private DateTimeOffset _jusqua = DateTimeOffset.MinValue;

    /// <summary>Hôte de l'autre serveur tel que réglé (adresse IP ou nom ; un « :port » éventuel est ignoré), null si non déclaré.</summary>
    public static string? Hote(string? autreServeur)
    {
        if (!DeuxServeursDeclares.Est(autreServeur)) return null;
        var h = autreServeur!.Trim();
        if (IPAddress.TryParse(h, out _)) return h;
        var i = h.LastIndexOf(':');
        return i > 0 && h.IndexOf(':') == i ? h[..i] : h;
    }

    public bool Declare => Hote(autreServeur) is not null;

    public static bool EstPrivee(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var o = a.GetAddressBytes();
            return o[0] == 10 || (o[0] == 172 && o[1] is >= 16 and <= 31) || (o[0] == 192 && o[1] == 168);
        }
        return a.AddressFamily == AddressFamily.InterNetworkV6 && (a.IsIPv6LinkLocal || (a.GetAddressBytes()[0] & 0xFE) == 0xFC);
    }

    /// <summary>Vrai si l'appelant est sur le réseau privé et correspond à l'adresse déclarée de l'autre serveur.</summary>
    public async Task<bool> AutoriseAsync(IPAddress? appelant, CancellationToken ct)
    {
        if (appelant is null || Hote(autreServeur) is not { } hote) return false;
        if (appelant.IsIPv4MappedToIPv6) appelant = appelant.MapToIPv4();
        if (!EstPrivee(appelant)) return false;
        foreach (var a in await AdressesAsync(hote, ct)) if ((a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Equals(appelant)) return true;
        return false;
    }

    private async Task<IPAddress[]> AdressesAsync(string hote, CancellationToken ct)
    {
        if (IPAddress.TryParse(hote, out var ip)) return [ip];
        var now = _temps.GetUtcNow();
        if (now < _jusqua) return _adresses;
        try { _adresses = await _resoudre(hote, ct); }
        catch (SocketException) { _adresses = []; }
        _jusqua = now.AddSeconds(60);
        return _adresses;
    }
}

public enum VerdictCleJetons { Inconnu, Identique, Different }

/// <summary>
/// État partagé de la comparaison des clés des jetons entre les deux serveurs (écran « clé des jetons identique : oui / non / inconnu »,
/// alerte au titulaire). Alimenté par la sonde signée sortante et par les refus de signature venant de l'adresse de l'autre serveur.
/// </summary>
public sealed class SuiviCleJetons(TimeProvider? temps = null)
{
    public static readonly TimeSpan IntervalleAlerte = TimeSpan.FromHours(1);
    public const int RefusEntrantsSeuil = 3;
    private static readonly TimeSpan FenetreRefus = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _temps = temps ?? TimeProvider.System;
    private readonly object _verrou = new();
    private readonly Queue<DateTimeOffset> _refus = new();
    private DateTimeOffset? _derniereAlerte;

    public VerdictCleJetons Verdict { get; private set; } = VerdictCleJetons.Inconnu;
    public DateTime? VerifieLeUtc { get; private set; }

    public void Mettre(VerdictCleJetons verdict)
    {
        lock (_verrou) { Verdict = verdict; VerifieLeUtc = _temps.GetUtcNow().UtcDateTime; }
    }

    /// <summary>Un appel signé venant de l'adresse déclarée de l'autre serveur a été refusé pour signature fausse.</summary>
    public void NoterRefusEntrant()
    {
        lock (_verrou) { _refus.Enqueue(_temps.GetUtcNow()); Purger(); }
    }

    public int RefusEntrantsRecents()
    {
        lock (_verrou) { Purger(); return _refus.Count; }
    }

    public bool RefusEntrantsSuspects => RefusEntrantsRecents() >= RefusEntrantsSeuil;

    /// <summary>Au plus une alerte par heure ; vrai = il faut alerter maintenant (et la remise est notée).</summary>
    public bool AlerteDue()
    {
        lock (_verrou)
        {
            var now = _temps.GetUtcNow();
            if (_derniereAlerte is { } d && now - d < IntervalleAlerte) return false;
            _derniereAlerte = now;
            return true;
        }
    }

    private void Purger()
    {
        var limite = _temps.GetUtcNow() - FenetreRefus;
        while (_refus.Count > 0 && _refus.Peek() < limite) _refus.Dequeue();
    }
}
