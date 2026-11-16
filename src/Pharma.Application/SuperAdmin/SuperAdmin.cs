using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Notifications;

namespace Pharma.Application.SuperAdmin;

// ---- Modèles ----------------------------------------------------------------

/// <summary>
/// Copie des réglages d'envoi (modem GSM, serveur SMTP) gardée avec le compte : le code part même si la base est détruite.
/// Mise à jour à chaque enregistrement des notifications et à chaque envoi de code lu en base.
/// </summary>
public sealed record CopieEnvoi(string? ModemPort, string? SmtpServeur, int SmtpPort, bool SmtpTls, string? SmtpExpediteur,
    string? SmtpIdentifiant, string? SmtpMotDePasse)
{
    public bool SmtpConfigure => !string.IsNullOrWhiteSpace(SmtpServeur) && !string.IsNullOrWhiteSpace(SmtpExpediteur);
}

/// <summary>
/// Compte du super admin : hors de la base, dans un fichier chiffré du PC serveur. Au moins un canal vérifié (téléphone ou e-mail) ;
/// un canal absent est null. Un fichier ancien (téléphone et e-mail renseignés) reste lisible tel quel.
/// </summary>
/// <param name="DossierSauvegardes">Copie du dossier des sauvegardes (disque externe) : la restauration le retrouve même base détruite.</param>
public sealed record CompteSuperAdmin(string Identifiant, string MotDePasseHash, string? Telephone, string? Email, CopieEnvoi? Envoi = null,
    string? DossierSauvegardes = null)
{
    [JsonIgnore] public bool ASms => !string.IsNullOrWhiteSpace(Telephone);
    [JsonIgnore] public bool AEmail => !string.IsNullOrWhiteSpace(Email);
    [JsonIgnore] public int NombreCanaux => (ASms ? 1 : 0) + (AEmail ? 1 : 0);
}

/// <summary>
/// Canaux du compte (création et modification, commande --super-admin). Seuls les canaux dont le code a été reçu sont retenus :
/// un canal saisi mais non vérifié n'est PAS enregistré (jamais de canal « non vérifié » dans le fichier, car il pourrait recevoir
/// des codes à la place du titulaire ou n'en recevoir aucun).
/// </summary>
public static class CanauxSuperAdmin
{
    public const string Retrait = "-";

    public const string AucunCanalCreation = "Aucun canal de validation ne fonctionne : le compte serait inutilisable, car chaque action demande un code. " +
        "Réglez le modem GSM (SMS) ou le serveur e-mail, dans Administration › Notifications, puis recommencez. Compte non créé.";

    public const string DernierCanal = "Impossible de retirer le dernier canal vérifié : il faut au moins un téléphone ou une adresse e-mail vérifié. Rien n'a été modifié.";

    /// <summary>Canaux souhaités d'après la saisie : vide = inchangé, « - » = retirer, sinon nouvelle valeur (format contrôlé, numéro normalisé).</summary>
    public static (string? Telephone, string? Email) Souhaites(CompteSuperAdmin? actuel, string? saisieTelephone, string? saisieEmail)
    {
        string? tel = actuel?.Telephone, mail = actuel?.Email;
        if (saisieTelephone?.Trim() == Retrait) tel = null;
        else if (!string.IsNullOrWhiteSpace(saisieTelephone))
        {
            if (!RegleDestinataire.EstMobile(saisieTelephone))
                throw new RegleMetierException("Téléphone invalide : mobile 05, 06 ou 07 suivi de 8 chiffres, ou +213.");
            tel = RegleDestinataire.NormaliserTelephone(saisieTelephone);
        }
        if (saisieEmail?.Trim() == Retrait) mail = null;
        else if (!string.IsNullOrWhiteSpace(saisieEmail))
        {
            if (!RegleDestinataire.EstEmail(saisieEmail)) throw new RegleMetierException("Adresse e-mail invalide.");
            mail = saisieEmail.Trim();
        }
        if (string.IsNullOrWhiteSpace(tel) && string.IsNullOrWhiteSpace(mail))
            throw new RegleMetierException(actuel is null ? "Au moins un canal est obligatoire : un téléphone mobile (SMS) ou une adresse e-mail." : DernierCanal);
        return (string.IsNullOrWhiteSpace(tel) ? null : tel, string.IsNullOrWhiteSpace(mail) ? null : mail);
    }

    /// <summary>Un canal est-il nouveau (ou modifié) et donc à vérifier par un code ?</summary>
    public static bool ADeVerifier(string? ancien, string? souhaite) =>
        souhaite is not null && !string.Equals(ancien?.Trim(), souhaite.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Canaux à enregistrer après les vérifications : un canal modifié et non vérifié garde son ancienne valeur (ou reste absent à la création).
    /// Aucun canal vérifié : refus (création : message des réglages à faire ; modification : rien n'est modifié).
    /// </summary>
    public static (string? Telephone, string? Email) Retenir(CompteSuperAdmin? actuel, (string? Telephone, string? Email) souhaites,
        bool telephoneVerifie, bool emailVerifie)
    {
        var tel = souhaites.Telephone is null ? null : !ADeVerifier(actuel?.Telephone, souhaites.Telephone) || telephoneVerifie ? souhaites.Telephone : actuel?.Telephone;
        var mail = souhaites.Email is null ? null : !ADeVerifier(actuel?.Email, souhaites.Email) || emailVerifie ? souhaites.Email : actuel?.Email;
        if (string.IsNullOrWhiteSpace(tel) && string.IsNullOrWhiteSpace(mail))
            throw new RegleMetierException(actuel is null ? AucunCanalCreation : DernierCanal);
        return (tel, mail);
    }
}

/// <summary>
/// Où le même code est réellement parti : numéro et adresse masqués, null si le canal n'a pas fonctionné.
/// Les messages d'échec (techniques) ne servent qu'au journal du serveur.
/// </summary>
public sealed record ResultatEnvoi(string? Sms, string? Email, string? EchecSms = null, string? EchecEmail = null, bool CanalUnique = false)
{
    /// <summary>Canaux utilisés : « SMS et e-mail », « SMS » ou « e-mail ».</summary>
    public string Canal => Sms is not null && Email is not null ? "SMS et e-mail" : Sms is not null ? "SMS" : "e-mail";

    /// <summary>Destinataires masqués, dans l'ordre des canaux.</summary>
    public string Destinataire => Sms is not null && Email is not null ? $"{Sms} et {Email}" : Sms ?? Email ?? "";

    /// <summary>Canaux utilisés, pour le journal : « par SMS au 06•••••512 et par e-mail à t•••@exemple.dz ». Jamais le code.</summary>
    public string Libelle => (Sms, Email) switch
    {
        ({ } s, { } e) => $"par SMS au {s} et par e-mail à {e}",
        ({ } s, null) => $"par SMS au {s}",
        (null, { } e) => $"par e-mail à {e}",
        _ => "par aucun canal"
    };

    /// <summary>Phrase montrée au super admin ; un canal en échec est dit.</summary>
    public string Message => $"Code envoyé {Libelle}" + (Sms is null && EchecSms is not null ? " ; SMS non parti" : Email is null && EchecEmail is not null ? " ; e-mail non parti" : "")
        + (CanalUnique ? " (seul canal vérifié du compte)" : "");
}

/// <summary>Demande de validation envoyée : où le code est parti (numéros et adresses masqués) et jusqu'à quand il vaut.</summary>
public sealed record DemandeValidation(Guid Id, string Canal, string Destinataire, DateTime ExpireLe, string? Message = null);

/// <summary>Validation vérifiée pour la requête en cours (code juste, action prévue). Libellé : canaux réellement utilisés.</summary>
public sealed record ValidationAcceptee(string Action, string Canal, string Destinataire, string? Libelle = null);

// ---- Ports ------------------------------------------------------------------

/// <summary>Fichier chiffré du compte (DPAPI, portée machine) sur le PC serveur.</summary>
public interface ICompteSuperAdmin
{
    string Emplacement { get; }
    CompteSuperAdmin? Lire();
    void Enregistrer(CompteSuperAdmin compte);
}

/// <summary>Pharmacien titulaire actif : auteur des données chargées par le super admin (registre des psychotropes compris).</summary>
public interface ITitulaireLecture
{
    Task<long?> IdAsync(CancellationToken ct = default);
}

// ---- Actions ----------------------------------------------------------------

public static class ActionsSuperAdmin
{
    public const string RepriseExtraire = "reprise.extraire";
    public const string RepriseCharger = "reprise.charger";
    public const string RepriseAnnuler = "reprise.annuler";
    public const string ChangerTelephone = "compte.telephone";
    public const string ChangerEmail = "compte.email";
    public const string RestaurerBase = "restauration.base";
    public const string RestaurerGed = "restauration.ged";

    /// <summary>Libellé de l'action, repris dans l'avertissement, le SMS et le journal.</summary>
    public static readonly IReadOnlyDictionary<string, string> Libelles = new Dictionary<string, string>
    {
        [RepriseExtraire] = "extraire les donnees de l'ancien logiciel",
        [RepriseCharger] = "charger la reprise dans la base",
        [RepriseAnnuler] = "annuler la reprise",
        [ChangerTelephone] = "changer le telephone du super admin",
        [ChangerEmail] = "changer l'e-mail du super admin",
        [RestaurerBase] = "restaurer la base de donnees",
        [RestaurerGed] = "restaurer les documents de la GED"
    };

    /// <summary>Marque ajoutée aux droits du poste pour afficher les écrans du super admin.</summary>
    public const string Marque = "role.super_admin";
}

// ---- Super admin de la requête -----------------------------------------------

/// <summary>Renseigné par l'API pour la requête en cours : identifiant du super admin connecté et validation vérifiée.</summary>
public sealed class SuperAdminCourant
{
    public string? Identifiant { get; set; }
    public ValidationAcceptee? Validation { get; set; }

    public void Exiger(string action)
    {
        if (Identifiant is null)
            throw new DroitRefuseException(ActionsSuperAdmin.Marque, action, $"Seul le super admin peut {action}.");
    }

    /// <summary>Action sensible : le code reçu par SMS ou par e-mail doit avoir été vérifié pour cette action précise.</summary>
    public ValidationAcceptee ExigerValidation(string action, string libelle)
    {
        Exiger(libelle);
        return Validation is { } v && v.Action == action
            ? v
            : throw new DroitRefuseException(ActionsSuperAdmin.Marque, libelle, $"Validation par code manquante pour {libelle}.");
    }

    /// <summary>Détails ajoutés au journal : qui a lancé l'action, et par quel code elle a été validée.</summary>
    public Dictionary<string, object?> Trace() => new()
    {
        ["super_admin"] = Identifiant,
        ["validation"] = Validation is { } v ? $"code envoyé {v.Libelle ?? $"par {v.Canal} au {v.Destinataire}"}" : null
    };

    public string Auteur => $"super admin {Identifiant}";
}

// ---- Codes de validation ------------------------------------------------------

/// <summary>
/// Codes à 6 chiffres en mémoire du serveur (jamais en base : elle peut être détruite) : valables 5 minutes, à usage unique,
/// liés à une action. 3 codes faux : nouvelles demandes bloquées 15 minutes. Mots de passe faux : blocage comme à la connexion.
/// </summary>
public sealed class ValidationsSuperAdmin(IHorloge? horloge = null)
{
    public static readonly TimeSpan Duree = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Blocage = TimeSpan.FromMinutes(15);
    public const int EssaisMax = 3;

    private sealed record Demande(string Action, byte[] Empreinte, DateTime ExpireLe, ResultatEnvoi Envoi);

    private readonly ConcurrentDictionary<Guid, Demande> demandes = new();
    private readonly object verrou = new();
    private int codesFaux;
    private DateTime? bloqueJusqu;

    /// <summary>Mots de passe faux (connexion et validations) : même règle que les employés.</summary>
    public EssaisConnexion MotsDePasse { get; } = new();

    private DateTime Maintenant => horloge?.MaintenantUtc ?? DateTime.UtcNow;

    public DateTime? BloqueJusqu
    {
        get { lock (verrou) return bloqueJusqu is { } b && b > Maintenant ? b : null; }
    }

    public static string NouveauCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public DemandeValidation Enregistrer(string action, string code, ResultatEnvoi envoi)
    {
        foreach (var (id, d) in demandes)
            if (d.ExpireLe <= Maintenant) demandes.TryRemove(id, out _);
        var demande = new Demande(action, Empreinte(code), Maintenant + Duree, envoi);
        var cle = Guid.NewGuid();
        demandes[cle] = demande;
        return new DemandeValidation(cle, envoi.Canal, envoi.Destinataire, demande.ExpireLe, envoi.Message);
    }

    /// <summary>Vérifie et consomme le code. Faux : message clair ; trois codes faux bloquent les validations 15 minutes.</summary>
    public ValidationAcceptee Verifier(Guid id, string? code, string action)
    {
        if (BloqueJusqu is { } b)
            throw new RegleMetierException($"Validations bloquées après {EssaisMax} codes faux : réessayez après {b.ToLocalTime():HH:mm}.", "code_validation");
        if (!demandes.TryGetValue(id, out var d) || d.Action != action)
            throw new RegleMetierException("Demande de validation inconnue : demandez un nouveau code.", "code_validation");
        if (d.ExpireLe <= Maintenant)
        {
            demandes.TryRemove(id, out _);
            throw new RegleMetierException("Code expiré (5 minutes) : demandez un nouveau code.", "code_validation");
        }
        if (string.IsNullOrWhiteSpace(code) || !CryptographicOperations.FixedTimeEquals(Empreinte(code.Trim()), d.Empreinte))
        {
            lock (verrou)
            {
                if (++codesFaux >= EssaisMax)
                {
                    (codesFaux, bloqueJusqu) = (0, Maintenant + Blocage);
                    demandes.Clear();
                    throw new RegleMetierException($"Code faux : validations bloquées {Blocage.TotalMinutes:0} minutes après {EssaisMax} codes faux.", "code_validation");
                }
            }
            throw new RegleMetierException("Code faux : vérifiez le code reçu.", "code_validation");
        }
        demandes.TryRemove(id, out _);
        lock (verrou) codesFaux = 0;
        return new ValidationAcceptee(d.Action, d.Envoi.Canal, d.Envoi.Destinataire, d.Envoi.Libelle);
    }

    private static byte[] Empreinte(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code));
}

// ---- Envoi du code -----------------------------------------------------------

/// <summary>
/// Envoie le MÊME code au super admin par SMS (modem GSM) ET par e-mail, en même temps. Un seul canal suffit : si l'un échoue,
/// l'autre part quand même et le résultat le dit ; si les deux échouent, l'envoi est refusé. Pour chaque canal, essai avec les
/// réglages de la base, puis, en cas d'échec ou de base inaccessible, avec la copie gardée dans le fichier du compte. La copie n'est
/// remplacée par les réglages de la base qu'après un envoi réussi avec eux : une base restaurée avec des réglages périmés ne l'écrase pas.
/// </summary>
public sealed class EnvoiCodeSuperAdmin(
    ICompteSuperAdmin comptes, IEnvoiSms sms, IEnvoiEmail email, IProtectionSecret secrets, INotificationsDepot? depot = null,
    Action<string>? signaler = null)
{
    /// <summary>
    /// Action sensible ou validation d'identité : le même code part sur chaque canal vérifié du compte (SMS au téléphone, e-mail à l'adresse) ;
    /// un compte à un seul canal ne reçoit que sur celui-là. Avec un nouveau numéro : SMS à ce numéro seulement.
    /// </summary>
    public Task<ResultatEnvoi> EnvoyerAsync(CompteSuperAdmin compte, string? numero, string texte, CancellationToken ct = default) =>
        numero is null
            ? EnvoyerCoreAsync(compte, compte.Telephone, compte.Email, compte.ASms, compte.AEmail, texte, ct, compte.NombreCanaux == 1)
            // Nouveau numéro : le code ne part que par SMS à ce numéro (l'e-mail ne prouve pas le numéro)
            : EnvoyerCoreAsync(compte, numero, compte.Email, true, false, texte, ct);

    /// <summary>Nouvelle adresse e-mail : le code ne part que vers cette adresse (elle prouve qu'on la possède).</summary>
    public Task<ResultatEnvoi> EnvoyerAAdresseAsync(CompteSuperAdmin compte, string adresse, string texte, CancellationToken ct = default) =>
        EnvoyerCoreAsync(compte, compte.Telephone, adresse, false, true, texte, ct);

    private async Task<ResultatEnvoi> EnvoyerCoreAsync(CompteSuperAdmin compte, string? telephone, string? adresse, bool parSms, bool parEmail,
        string texte, CancellationToken ct, bool canalUnique = false)
    {
        if (!parSms && !parEmail)
            throw new RegleMetierException("Ce compte n'a aucun canal vérifié : recréez-le avec Pharma.Api.exe --super-admin.");
        var enBase = await ReglagesBaseAsync(ct);
        var essais = new List<(CopieEnvoi Reglages, string Origine)>();
        if (enBase is not null) essais.Add((enBase, "réglages de la base"));
        if (compte.Envoi is not null && compte.Envoi != enBase) essais.Add((compte.Envoi, "copie du compte"));

        string? smsOk = null, emailOk = null, echecSms = null, echecEmail = null;
        var erreursSms = new List<string>();
        var erreursEmail = new List<string>();
        var baseVerifiee = true;   // tous les canaux que les réglages de la base prévoient ont marché avec eux
        foreach (var (reglages, origine) in essais)
        {
            var estBase = ReferenceEquals(reglages, enBase);
            if (parSms && smsOk is null)
            {
                if (string.IsNullOrWhiteSpace(reglages.ModemPort)) erreursSms.Add($"modem GSM non configuré [{origine}]");
                else
                    try
                    {
                        await sms.EnvoyerAsync(reglages.ModemPort, telephone!, TexteSms.Preparer(texte), ct);
                        smsOk = RegleDestinataire.MasquerTelephone(telephone!);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        erreursSms.Add($"{e.Message} [{origine}]");
                        signaler?.Invoke($"Code super admin : SMS non envoyé ({e.Message}) [{origine}]");
                        if (estBase) baseVerifiee = false;
                    }
            }
            if (parEmail && emailOk is null)
            {
                if (!reglages.SmtpConfigure) erreursEmail.Add($"serveur SMTP non configuré [{origine}]");
                else
                    try
                    {
                        await email.EnvoyerAsync(new ConnexionSmtp(reglages.SmtpServeur!, reglages.SmtpPort, reglages.SmtpTls, reglages.SmtpExpediteur!, reglages.SmtpIdentifiant, reglages.SmtpMotDePasse),
                            adresse!, "Code de validation du super admin", texte, null, ct);
                        emailOk = RegleDestinataire.MasquerEmail(adresse!);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        erreursEmail.Add($"{e.Message} [{origine}]");
                        signaler?.Invoke($"Code super admin : e-mail non envoyé ({e.Message}) [{origine}]");
                        if (estBase) baseVerifiee = false;
                    }
            }
        }

        // Réglages de la base vérifiés par cet envoi : ils deviennent la copie du compte
        if (enBase is not null && baseVerifiee && enBase != compte.Envoi && (smsOk is not null || emailOk is not null))
            try { if (comptes.Lire() is { } c) comptes.Enregistrer(c with { Envoi = enBase }); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        if (parSms && smsOk is null) echecSms = erreursSms.Count > 0 ? string.Join(", ", erreursSms) : "ni modem GSM ni serveur e-mail réglés";
        if (parEmail && emailOk is null) echecEmail = erreursEmail.Count > 0 ? string.Join(", ", erreursEmail) : "ni modem GSM ni serveur e-mail réglés";
        if (smsOk is null && emailOk is null)
        {
            var detail = new List<string>();
            if (echecSms is not null) detail.Add($"SMS : {echecSms}");
            if (echecEmail is not null) detail.Add($"e-mail : {echecEmail}");
            var canaux = parSms && parEmail ? "ni par SMS ni par e-mail" : parSms ? "par SMS (seul canal du compte)" : "par e-mail (seul canal du compte)";
            throw new RegleMetierException($"Code non envoyé, {canaux} ({string.Join(" ; ", detail)}). Vérifiez le modem GSM et le serveur e-mail " +
                                           "(Administration › Notifications au titulaire), ou restaurez depuis le PC serveur (scripts\restaurer_pharmacie.ps1).");
        }
        return new ResultatEnvoi(smsOk, emailOk, echecSms, echecEmail, canalUnique);
    }

    /// <summary>Réglages lus en base ; null si elle ne répond pas (détruite, en cours de restauration…).</summary>
    private async Task<CopieEnvoi?> ReglagesBaseAsync(CancellationToken ct)
    {
        if (depot is null) return null;
        try
        {
            var r = await depot.ReglagesAsync(ct);
            string? motDePasse = null;
            if (r.MotDePasseEnregistre && await depot.MotDePasseChiffreAsync(ct) is { } chiffre)
                try { motDePasse = secrets.Dechiffrer(chiffre); } catch (InvalidOperationException) { motDePasse = null; }
            return new CopieEnvoi(r.ModemPort, r.SmtpServeur, r.SmtpPort, r.SmtpTls, r.SmtpExpediteur, r.SmtpIdentifiant, motDePasse);
        }
        catch (Exception e) when (e is not OperationCanceledException and not RegleMetierException)
        {
            signaler?.Invoke($"Code super admin : base inaccessible ({e.Message}), réglages d'envoi du fichier du compte");
            return null;
        }
    }

    /// <summary>Recopie des réglages enregistrés dans l'administration (le mot de passe SMTP n'est recopié que s'il change).</summary>
    public void Recopier(ModificationReglagesNotification m)
    {
        if (comptes.Lire() is not { } compte) return;
        var motDePasse = m.NouveauMotDePasse is null ? compte.Envoi?.SmtpMotDePasse : m.NouveauMotDePasse.Length == 0 ? null : m.NouveauMotDePasse;
        comptes.Enregistrer(compte with
        {
            Envoi = new CopieEnvoi(m.ModemPort, m.SmtpServeur, m.SmtpPort, m.SmtpTls, m.SmtpExpediteur, m.SmtpIdentifiant, motDePasse)
        });
    }
}

// ---- Cas d'usage --------------------------------------------------------------

/// <summary>Connexion du super admin : identifiant et mot de passe du fichier du compte. Null : refus (sans dire pourquoi).</summary>
public sealed class SeConnecterSuperAdmin(ICompteSuperAdmin comptes, IVerificateurMotDePasse verificateur, ValidationsSuperAdmin etat, IHorloge? horloge = null)
{
    public CompteSuperAdmin? Executer(string? identifiant, string? motDePasse)
    {
        if (string.IsNullOrWhiteSpace(identifiant) || string.IsNullOrEmpty(motDePasse)) return null;
        var nom = "super:" + identifiant.Trim();
        var maintenant = horloge?.MaintenantUtc ?? DateTime.UtcNow;
        if (etat.MotsDePasse.BloqueJusqu(nom, maintenant) is { } jusqu)
            throw new RegleMetierException($"Connexion bloquée après {EssaisConnexion.EssaisMax} mots de passe faux : réessayez après {jusqu.ToLocalTime():HH:mm}.");
        var compte = comptes.Lire();
        var inconnu = compte is null || !string.Equals(compte.Identifiant, identifiant.Trim(), StringComparison.OrdinalIgnoreCase);
        // Identifiant inconnu : hachage factice, même durée de réponse
        if (inconnu) verificateur.VerifierFactice(motDePasse);
        if (inconnu || !verificateur.Verifier(motDePasse, compte!.MotDePasseHash))
        {
            if (etat.MotsDePasse.NoterEchec(nom, maintenant))
                throw new RegleMetierException($"Connexion bloquée {EssaisConnexion.Blocage.TotalMinutes:0} minutes après {EssaisConnexion.EssaisMax} mots de passe faux.");
            return null;
        }
        etat.MotsDePasse.NoterSucces(nom);
        return compte;
    }
}

/// <summary>
/// Avant chaque action sensible : le super admin ressaisit son mot de passe, puis le même code part par SMS et par e-mail (un seul canal suffit).
/// </summary>
public sealed class DemanderValidationSuperAdmin(
    SeConnecterSuperAdmin verifier, ValidationsSuperAdmin etat, EnvoiCodeSuperAdmin envoi, SuperAdminCourant courant)
{
    public async Task<DemandeValidation> ExecuterAsync(string? action, string? motDePasse, CancellationToken ct = default)
    {
        if (action is null || !ActionsSuperAdmin.Libelles.TryGetValue(action, out var libelle))
            throw new RegleMetierException("Action inconnue.");
        courant.Exiger(libelle);
        if (string.IsNullOrEmpty(motDePasse)) throw new RegleMetierException("Ressaisissez votre mot de passe de super admin.", "mot_de_passe_validation");
        if (etat.BloqueJusqu is { } b)
            throw new RegleMetierException($"Validations bloquées après {ValidationsSuperAdmin.EssaisMax} codes faux : réessayez après {b.ToLocalTime():HH:mm}.");
        var compte = verifier.Executer(courant.Identifiant, motDePasse)
                     ?? throw new RegleMetierException("Mot de passe faux.", "mot_de_passe_validation");
        var code = ValidationsSuperAdmin.NouveauCode();
        // Le même code part par SMS et par e-mail ; un seul canal suffit
        var resultat = await envoi.EnvoyerAsync(compte, null,
            $"PharmaOfficine : code {code} pour {libelle}. Valable 5 min. Ne le donnez a personne.", ct);
        return etat.Enregistrer(action, code, resultat);
    }
}

/// <summary>Création du compte sur le PC serveur (commande Pharma.Api.exe --super-admin) : au moins un canal (téléphone ou e-mail), déjà vérifié par l'appelant.</summary>
public sealed class CreerCompteSuperAdmin(ICompteSuperAdmin comptes, Administration.IHacheurMotDePasse hacheur)
{
    public const int LongueurMin = 12;

    public static void Verifier(string? identifiant, string? motDePasse, string? confirmation, string? telephone, string? email)
    {
        if (string.IsNullOrWhiteSpace(identifiant) || identifiant.Trim().Length < 4)
            throw new RegleMetierException("Identifiant de 4 caractères au moins.");
        if (motDePasse is null || motDePasse.Length < LongueurMin)
            throw new RegleMetierException($"Mot de passe de {LongueurMin} caractères au moins.");
        if (motDePasse != confirmation) throw new RegleMetierException("Les deux mots de passe sont différents.");
        var (aTel, aMail) = (!string.IsNullOrWhiteSpace(telephone), !string.IsNullOrWhiteSpace(email));
        if (!aTel && !aMail) throw new RegleMetierException("Au moins un canal est obligatoire : un téléphone mobile (SMS) ou une adresse e-mail.");
        if (aTel && !RegleDestinataire.EstMobile(telephone))
            throw new RegleMetierException("Téléphone invalide : mobile 05, 06 ou 07 suivi de 8 chiffres, ou +213.");
        if (aMail && !RegleDestinataire.EstEmail(email)) throw new RegleMetierException("Adresse e-mail invalide.");
    }

    public CompteSuperAdmin Executer(string identifiant, string motDePasse, string confirmation, string? telephone, string? email)
    {
        Verifier(identifiant, motDePasse, confirmation, telephone, email);
        var compte = new CompteSuperAdmin(identifiant.Trim(), hacheur.Hacher(motDePasse),
            string.IsNullOrWhiteSpace(telephone) ? null : RegleDestinataire.NormaliserTelephone(telephone),
            string.IsNullOrWhiteSpace(email) ? null : email.Trim(), comptes.Lire()?.Envoi, comptes.Lire()?.DossierSauvegardes);
        comptes.Enregistrer(compte);
        return compte;
    }
}
