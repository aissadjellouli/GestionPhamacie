using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Notifications;

namespace Pharma.Application.Notifications;

// ---- Modèles ----------------------------------------------------------------

/// <summary>Réglages lus en base ; le mot de passe SMTP n'en sort jamais, seul son existence est indiquée.</summary>
public sealed record ReglagesNotification(
    string? SmtpServeur, int SmtpPort, bool SmtpTls, string? SmtpExpediteur, string? SmtpIdentifiant, bool MotDePasseEnregistre,
    string? ModemPort, TimeOnly HeureRapport, DateOnly? DernierRapport,
    decimal? SmsSeuilEcart, bool SmsLotBloque, bool SmsSauvegarde)
{
    public bool SmtpConfigure => !string.IsNullOrWhiteSpace(SmtpServeur) && !string.IsNullOrWhiteSpace(SmtpExpediteur);
    public bool SmsActifs => !string.IsNullOrWhiteSpace(ModemPort);
}

/// <summary>Nouveau mot de passe : null = inchangé, chaîne vide = effacé.</summary>
public sealed record ModificationReglagesNotification(
    string? SmtpServeur, int SmtpPort, bool SmtpTls, string? SmtpExpediteur, string? SmtpIdentifiant, string? NouveauMotDePasse,
    string? ModemPort, TimeOnly HeureRapport, decimal? SmsSeuilEcart, bool SmsLotBloque, bool SmsSauvegarde);

/// <summary>Paramètres de connexion passés à l'adaptateur d'envoi, mot de passe déchiffré en mémoire seulement.</summary>
public sealed record ConnexionSmtp(string Serveur, int Port, bool Tls, string Expediteur, string? Identifiant, string? MotDePasse);

public sealed record NouveauMessage(
    CanalNotification Canal, long DestinataireId, string Adresse, TypeNotification Type, string? Objet, string Corps, string? CorpsHtml);

public sealed record MessageEnFile(
    long Id, CanalNotification Canal, string Destinataire, string Adresse, TypeNotification Type, string? Objet, string Corps,
    string? CorpsHtml, string Statut, int Tentatives, DateTime? ProchaineTentative, string? DerniereErreur, DateTime CreeLe, DateTime? EnvoyeLe);

/// <summary>Contenu d'une notification : texte détaillé pour l'e-mail, texte court pour le SMS (null = pas de SMS).</summary>
public sealed record ContenuNotification(TypeNotification Type, string Objet, string TexteEmail, string? HtmlEmail, string? TexteSms);

public sealed record SaisieDestinataire(
    string? Nom, string? Email, string? Telephone, bool CanalEmail, bool CanalSms, IReadOnlyCollection<TypeNotification> Types, bool Actif);

// ---- Ports ------------------------------------------------------------------

public interface INotificationsDepot
{
    Task<ReglagesNotification> ReglagesAsync(CancellationToken ct = default);
    /// <param name="motDePasseChiffre">Utilisé seulement si <paramref name="changerMotDePasse"/> est vrai (null = effacer).</param>
    Task EnregistrerReglagesAsync(ModificationReglagesNotification reglages, bool changerMotDePasse, byte[]? motDePasseChiffre, CancellationToken ct = default);
    Task<byte[]?> MotDePasseChiffreAsync(CancellationToken ct = default);
    Task NoterRapportEnvoyeAsync(DateOnly jour, CancellationToken ct = default);

    Task<IReadOnlyList<Destinataire>> DestinatairesAsync(CancellationToken ct = default);
    Task<long> CreerDestinataireAsync(SaisieDestinataire d, CancellationToken ct = default);
    Task<bool> ModifierDestinataireAsync(long id, SaisieDestinataire d, CancellationToken ct = default);
    Task<bool> MarquerValideAsync(long id, bool valide, CancellationToken ct = default);

    Task AjouterMessagesAsync(IReadOnlyList<NouveauMessage> messages, CancellationToken ct = default);
    Task<IReadOnlyList<MessageEnFile>> MessagesAEnvoyerAsync(DateTime maintenantUtc, int limite, CancellationToken ct = default);
    Task MarquerEnvoyeAsync(long id, DateTime quandUtc, CancellationToken ct = default);
    /// <param name="prochaineTentativeUtc">Null : abandon, le message passe au statut « echec ».</param>
    Task MarquerEchecAsync(long id, string erreur, DateTime? prochaineTentativeUtc, CancellationToken ct = default);
    Task<IReadOnlyList<MessageEnFile>> FileAsync(int limite, CancellationToken ct = default);
}

/// <summary>Chiffrement des secrets enregistrés en base (DPAPI sur le PC serveur).</summary>
public interface IProtectionSecret
{
    byte[] Chiffrer(string secret);
    string Dechiffrer(byte[] chiffre);
}

public interface IEnvoiEmail
{
    Task EnvoyerAsync(ConnexionSmtp connexion, string adresse, string objet, string texte, string? html, CancellationToken ct = default);
}

public interface IEnvoiSms
{
    /// <summary>Envoi par commandes AT sur le port série du modem GSM.</summary>
    Task EnvoyerAsync(string portSerie, string numero, string texte, CancellationToken ct = default);
}

/// <summary>Met une notification en file pour chaque destinataire vérifié qui l'a demandée.</summary>
public interface INotificationService
{
    /// <returns>Nombre de messages mis en file.</returns>
    Task<int> MettreEnFileAsync(ContenuNotification contenu, CancellationToken ct = default);
}

/// <summary>Données agrégées du rapport quotidien : aucune donnée de patient ni d'ordonnance.</summary>
public interface IDonneesRapport
{
    Task<DonneesRapportQuotidien> LireAsync(DateOnly jour, CancellationToken ct = default);
}

public sealed record EcartEmploye(string Employe, string Poste, decimal Ecart);
public sealed record AlerteDuJour(string Type, string Produit, string? NumeroLot, string Detail);

public sealed record DonneesRapportQuotidien(
    DateOnly Jour, int NombreVentes, decimal ChiffreAffaires, decimal PartTiersPayant,
    IReadOnlyList<EcartEmploye> Ecarts, int Annulations, decimal MontantAnnule,
    IReadOnlyList<AlerteDuJour> Peremptions, IReadOnlyList<AlerteDuJour> Ruptures);

// ---- Mise en file -------------------------------------------------------------

public sealed class NotificationService(INotificationsDepot depot, Administration.IPharmacie? pharmacie = null) : INotificationService
{
    /// <summary>Signature des e-mails : le nom de la pharmacie (Administration, Langue et pharmacie), sinon « Pharma Officine ».</summary>
    public static async Task<string> SignatureAsync(Administration.IPharmacie? pharmacie, CancellationToken ct = default) =>
        (pharmacie is null ? null : (await pharmacie.LireAsync(ct)).Nom) is { Length: > 0 } nom ? nom.Trim() : "Pharma Officine";

    public async Task<int> MettreEnFileAsync(ContenuNotification c, CancellationToken ct = default)
    {
        var reglages = await depot.ReglagesAsync(ct);
        var messages = Construire(c, await depot.DestinatairesAsync(ct), reglages.SmsActifs, await SignatureAsync(pharmacie, ct));
        if (messages.Count > 0) await depot.AjouterMessagesAsync(messages, ct);
        return messages.Count;
    }

    /// <summary>Un message par canal et par destinataire concerné ; pas de SMS sans modem configuré.</summary>
    /// <param name="signature">Nom de la pharmacie, en fin d'e-mail (le rapport du soir, mis en forme à part, signe lui-même).</param>
    public static List<NouveauMessage> Construire(ContenuNotification c, IEnumerable<Destinataire> destinataires, bool smsActifs, string? signature = null)
    {
        var liste = new List<NouveauMessage>();
        // E-mail signé du nom de la pharmacie (version texte et version HTML)
        var texte = c.HtmlEmail is null && signature is not null ? $"{c.TexteEmail}\n\n{signature}" : c.TexteEmail;
        foreach (var d in destinataires)
        {
            if (d.Recoit(c.Type, CanalNotification.Email))
                liste.Add(new NouveauMessage(CanalNotification.Email, d.Id, d.Adresse(CanalNotification.Email), c.Type, c.Objet, texte,
                    c.HtmlEmail ?? HtmlAlerte(c.Objet, c.TexteEmail, signature)));
            if (smsActifs && c.TexteSms is not null && d.Recoit(c.Type, CanalNotification.Sms))
                liste.Add(new NouveauMessage(CanalNotification.Sms, d.Id, d.Adresse(CanalNotification.Sms), c.Type, null, TexteSms.Preparer(c.TexteSms), null));
        }
        return liste;
    }

    /// <summary>E-mail d'alerte : l'objet de l'alerte en gras en tête, puis le texte (paragraphes et retours à la ligne gardés).</summary>
    public static string HtmlAlerte(string objet, string texte, string? signature = null)
    {
        // Caractères spéciaux du HTML seulement : les accents restent lisibles (e-mail en UTF-8)
        static string E(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        var paragraphes = texte.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => $"<p style=\"margin: 0 0 10px\">{string.Join("<br />", p.Split('\n').Select(E))}</p>");
        return "<div style=\"font-family: 'Segoe UI', Arial, sans-serif; font-size: 14px; line-height: 1.5; color: #1f2937\">"
               + $"<p style=\"margin: 0 0 14px; font-size: 16px\"><strong>{E(objet)}</strong></p>"
               + string.Concat(paragraphes)
               + (signature is null ? "" : $"<p style=\"margin: 18px 0 0; color: #475467\">{E(signature)}</p>")
               + "</div>";
    }
}

// ---- Envoi de la file (appelé par la tâche de fond) ---------------------------

public sealed record BilanEnvoi(int Envoyes, int Echecs);

public sealed class TraiterFileAttente(INotificationsDepot depot, IEnvoiEmail email, IEnvoiSms sms, IProtectionSecret secrets,
    IAlertesNonEnvoyees? nonEnvoyees = null, ITraceErreurs? trace = null)
{
    public async Task<BilanEnvoi> ExecuterAsync(DateTime maintenantUtc, CancellationToken ct = default)
    {
        var aEnvoyer = await depot.MessagesAEnvoyerAsync(maintenantUtc, 50, ct);
        if (aEnvoyer.Count == 0) return new BilanEnvoi(0, 0);

        var reglages = await depot.ReglagesAsync(ct);
        // Mot de passe SMTP illisible (base restaurée sur un autre PC) : seuls les e-mails échouent, les SMS partent
        ConnexionSmtp? connexion = null;
        string? erreurSmtp = null;
        if (reglages.SmtpConfigure)
        {
            try
            {
                var chiffre = await depot.MotDePasseChiffreAsync(ct);
                connexion = new ConnexionSmtp(reglages.SmtpServeur!, reglages.SmtpPort, reglages.SmtpTls, reglages.SmtpExpediteur!,
                    reglages.SmtpIdentifiant, chiffre is null ? null : secrets.Dechiffrer(chiffre));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                erreurSmtp = ex.Message;
            }
        }

        int envoyes = 0, echecs = 0;
        foreach (var m in aEnvoyer)
        {
            try
            {
                if (m.Canal == CanalNotification.Email)
                {
                    if (connexion is null) throw new InvalidOperationException(erreurSmtp ?? "Serveur SMTP non configuré.");
                    // « 3 lot(s) » → « 3 lots », « 1 boîte(s) » → « 1 boîte » (Accord) : textes venus du code et de la base
                    await email.EnvoyerAsync(connexion, m.Adresse, Accord.Texte(m.Objet ?? "Pharma Officine"), Accord.Texte(m.Corps),
                        m.CorpsHtml is null ? null : Accord.Texte(m.CorpsHtml), ct);
                }
                else
                {
                    if (!reglages.SmsActifs) throw new InvalidOperationException("Modem GSM non configuré.");
                    await sms.EnvoyerAsync(reglages.ModemPort!, m.Adresse, Accord.Texte(m.Corps), ct);
                }
                await depot.MarquerEnvoyeAsync(m.Id, maintenantUtc, ct);
                envoyes++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Internet, serveur SMTP ou modem indisponible : nouvelle tentative plus tard
                var prochaine = RegleNouvelleTentative.Prochaine(maintenantUtc, m.CreeLe, m.Tentatives + 1);
                await depot.MarquerEchecAsync(m.Id, ex.Message, prochaine, ct);
                echecs++;
                // Abandon (trop ancien) : signalé au titulaire par le bandeau de l'accueil
                if (prochaine is null)
                    await AlerteNonEnvoyeeSignalee.NoterAsync(nonEnvoyees, trace, m.Type,
                        $"{(m.Canal == CanalNotification.Email ? "E-mail" : "SMS")} à {m.Destinataire} : {m.Objet ?? Accord.Texte(m.Corps)}",
                        $"Envoi abandonné après {m.Tentatives + 1} tentative(s) : {ex.Message}");
            }
        }
        return new BilanEnvoi(envoyes, echecs);
    }
}

// ---- Rapport quotidien ---------------------------------------------------------

public sealed class EnvoyerRapportQuotidien(INotificationsDepot depot, IDonneesRapport donnees, INotificationService notifications, Administration.IPharmacie pharmacie,
    Alertes.IMoteurAlertes? alertes = null)
{
    /// <summary>Envoie le rapport du jour une fois l'heure réglée passée, une seule fois par jour.</summary>
    public async Task<bool> ExecuterAsync(DateTime maintenantLocal, CancellationToken ct = default)
    {
        var reglages = await depot.ReglagesAsync(ct);
        var jour = DateOnly.FromDateTime(maintenantLocal);
        if (TimeOnly.FromDateTime(maintenantLocal) < reglages.HeureRapport || reglages.DernierRapport >= jour) return false;
        await EnvoyerAsync(jour, ct);
        await depot.NoterRapportEnvoyeAsync(jour, ct);
        return true;
    }

    public async Task<int> EnvoyerAsync(DateOnly jour, CancellationToken ct = default)
    {
        // Alertes recalculées juste avant : une alerte devenue sans objet (lot qui sera vendu à temps, stock revenu) n'est pas envoyée
        if (alertes is not null) await alertes.GenererAsync(ct);
        var d = await donnees.LireAsync(jour, ct);
        var nom = (await pharmacie.LireAsync(ct)).Nom;
        return await notifications.MettreEnFileAsync(RapportQuotidien.Construire(d, nom), ct);
    }
}

/// <summary>Mise en forme du rapport : texte et HTML, à partir des seules données agrégées.</summary>
public static class RapportQuotidien
{
    private static readonly System.Globalization.CultureInfo Fr = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");

    public static ContenuNotification Construire(DonneesRapportQuotidien d, string? pharmacie)
    {
        var titre = $"Rapport du {d.Jour.ToString("dd/MM/yyyy", Fr)}" + (pharmacie is null ? "" : $" · {pharmacie}");
        var texte = new System.Text.StringBuilder();
        texte.AppendLine(titre).AppendLine();
        texte.AppendLine($"Chiffre du jour : {Da(d.ChiffreAffaires)} ({d.NombreVentes} vente(s), dont {Da(d.PartTiersPayant)} en tiers payant)");
        texte.AppendLine($"Annulations : {d.Annulations} ({Da(d.MontantAnnule)})");
        // Rubriques sans rien à signaler : absentes de l'e-mail (ni « 0 » ni « RAS », réservé à l'écran Alertes)
        if (d.Ecarts.Count > 0)
        {
            texte.AppendLine().AppendLine("Écarts de caisse :");
            foreach (var e in d.Ecarts) texte.AppendLine($"  {e.Employe} ({e.Poste}) : {Signe(e.Ecart)}");
        }
        if (d.Peremptions.Count > 0)
        {
            texte.AppendLine().AppendLine($"Nouvelles alertes de péremption : {d.Peremptions.Count}");
            foreach (var a in d.Peremptions) texte.AppendLine($"  {a.Produit}{Lot(a)} : {Detail(a)}");
        }
        if (d.Ruptures.Count > 0)
        {
            texte.AppendLine().AppendLine($"Nouvelles ruptures : {d.Ruptures.Count}");
            foreach (var a in d.Ruptures) texte.AppendLine($"  {a.Produit} : {Detail(a)}");
        }

        var h = new System.Text.StringBuilder();
        h.Append("<!doctype html><html lang=\"fr\"><body style=\"font-family:Segoe UI,Arial,sans-serif;color:#1f2937;max-width:640px\">");
        h.Append($"<h1 style=\"color:#0E5A32;font-size:20px\">{E(titre)}</h1>");
        h.Append("<table style=\"border-collapse:collapse\">");
        Ligne(h, "Chiffre du jour", $"{Da(d.ChiffreAffaires)} ({d.NombreVentes} vente(s))");
        Ligne(h, "Dont tiers payant", Da(d.PartTiersPayant));
        Ligne(h, "Annulations", $"{d.Annulations} ({Da(d.MontantAnnule)})");
        h.Append("</table>");
        if (d.Ecarts.Count > 0)
        {
            h.Append("<h2 style=\"font-size:16px\">Écarts de caisse par employé</h2>");
            h.Append("<table style=\"border-collapse:collapse\">");
            foreach (var e in d.Ecarts)
                Ligne(h, $"{e.Employe} ({e.Poste})", Signe(e.Ecart), e.Ecart != 0 ? "#912018" : null);
            h.Append("</table>");
        }
        Liste(h, "Nouvelles alertes de péremption", d.Peremptions);
        Liste(h, "Nouvelles ruptures", d.Ruptures);
        // Signé du nom de la pharmacie
        var signature = string.IsNullOrWhiteSpace(pharmacie) ? "Pharma Officine" : pharmacie.Trim();
        texte.AppendLine().AppendLine(signature);
        h.Append($"<p style=\"margin:18px 0 0;color:#475467\">{E(signature)}</p>");
        h.Append("<p style=\"color:#6b7280;font-size:12px\">Message automatique. Aucune donnée de patient n’est transmise.</p></body></html>");

        return new ContenuNotification(TypeNotification.RapportQuotidien, titre, texte.ToString(), h.ToString(), null);
    }

    private static void Liste(System.Text.StringBuilder h, string titre, IReadOnlyList<AlerteDuJour> alertes)
    {
        // Rien de nouveau : rubrique absente de l'e-mail
        if (alertes.Count == 0) return;
        h.Append($"<h2 style=\"font-size:16px\">{E(titre)} : {alertes.Count}</h2>");
        h.Append("<ul>");
        foreach (var a in alertes) h.Append($"<li><strong>{E(a.Produit)}</strong>{E(Lot(a))} : {E(Detail(a))}</li>");
        h.Append("</ul>");
    }

    private static void Ligne(System.Text.StringBuilder h, string libelle, string valeur, string? couleur = null) =>
        h.Append($"<tr><td style=\"padding:4px 16px 4px 0\">{E(libelle)}</td><td style=\"padding:4px 0;font-weight:600{(couleur is null ? "" : ";color:" + couleur)}\">{E(valeur)}</td></tr>");

    // Le message de l'alerte reprend déjà le produit et le lot : on ne les répète pas
    private static string Detail(AlerteDuJour a)
    {
        foreach (var prefixe in new[] { $"{a.Produit}, lot {a.NumeroLot} : ", $"{a.Produit} " })
            if (a.Detail.StartsWith(prefixe, StringComparison.Ordinal) && a.Detail.Length > prefixe.Length)
                return char.ToUpper(a.Detail[prefixe.Length]) + a.Detail[(prefixe.Length + 1)..];
        return a.Detail;
    }

    private static string Lot(AlerteDuJour a) => a.NumeroLot is null ? "" : $" · lot {a.NumeroLot}";
    private static string Da(decimal m) => m.ToString("N2", Fr) + " DA";
    private static string Signe(decimal m) => (m > 0 ? "+" : "") + Da(m);
    private static string E(string s) => System.Net.WebUtility.HtmlEncode(s);
}

// ---- Alertes immédiates du titulaire ------------------------------------------

/// <summary>Événements graves signalés au titulaire, par SMS (urgent) et par e-mail.</summary>
public interface IAlertesTitulaire
{
    Task EcartCaisseAsync(string employe, string poste, decimal ecart, decimal seuilCaisse, CancellationToken ct = default);
    Task LotBloqueAsync(string produit, string numeroLot, int boitesEnStock, int boitesVendues, CancellationToken ct = default);
    Task SauvegardeEnEchecAsync(bool disqueAbsent, string detail, CancellationToken ct = default);
    Task EcartsInventaireAsync(string rayon, int lignes, int boites, decimal valeur, CancellationToken ct = default);
    Task AnnulationsAsync(string poste, int annulations, int seuil, CancellationToken ct = default);
    /// <summary>Ancien lot délaissé au profit d'un lot plus récent (le message nomme le vendeur) : inventaire du produit à faire.</summary>
    Task LotDelaisseAsync(string message, bool urgent, CancellationToken ct = default);
    /// <summary>Point du contrôle interne (caisse, stock, réception, retours) : texte court, employé et chiffres, aucune donnée de patient.</summary>
    Task SurveillanceAsync(string message, bool urgent, CancellationToken ct = default);
}

/// <summary>
/// Jamais bloquant : l'opération (clôture, rappel, inventaire, sauvegarde…) est déjà enregistrée. Une alerte qui ne peut pas être
/// mise en file (erreur, base indisponible) ou qu'aucun destinataire vérifié ne reçoit est inscrite au journal du serveur
/// (erreur seulement) et mémorisée pour le bandeau de l'accueil du titulaire (alerte_non_envoyee).
/// </summary>
public sealed class AlertesTitulaire(INotificationsDepot depot, INotificationService notifications,
    IAlertesNonEnvoyees? nonEnvoyees = null, ITraceErreurs? trace = null) : IAlertesTitulaire
{
    private static readonly System.Globalization.CultureInfo Fr = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Raison mémorisée quand la file ne reçoit aucun message pour l'alerte.</summary>
    public const string AucunDestinataire = "Aucun destinataire vérifié et actif ne reçoit ce type d’alerte (Administration › Notifications).";

    /// <summary>
    /// Écart de caisse : tout écart non nul est notifié (e-mail, et dans le logiciel par le journal des actions) ;
    /// le SMS seulement au-delà du seuil SMS réglé, à défaut du seuil d'écart de la caisse.
    /// </summary>
    public Task EcartCaisseAsync(string employe, string poste, decimal ecart, decimal seuilCaisse, CancellationToken ct = default)
    {
        var montant = (ecart > 0 ? "+" : "") + ecart.ToString("N2", Fr) + " DA";
        // Manque (moins d'argent que l'attendu) ou excédent (plus d'argent) : dit en clair, l'un et l'autre surveillés
        var nature = Pharma.Domain.Caisse.NatureEcart.Libelle(ecart);
        var objet = $"{nature} de caisse {montant} ({poste})";
        return ProtegerAsync(TypeNotification.EcartCaisse, objet, async () =>
        {
            if (ecart == 0) return -1;   // rien à signaler
            var r = await depot.ReglagesAsync(ct);
            var seuil = r.SmsSeuilEcart ?? seuilCaisse;
            var natureSms = Pharma.Domain.Caisse.NatureEcart.EstManque(ecart) ? "Manque" : "Excedent";
            var texte = $"{natureSms} de caisse {montant} a la cloture de {employe} ({poste}), seuil {seuil.ToString("N0", Fr)} DA.";
            // E-mail : une information par ligne (les retours à la ligne deviennent des lignes dans l'e-mail, HtmlAlerte)
            return await notifications.MettreEnFileAsync(new ContenuNotification(TypeNotification.EcartCaisse,
                $"{nature} de caisse {montant}",
                $"{nature} de caisse à la clôture.\n\nCaisse : {poste}\nClôturée par : {employe}\nÉcart : {montant} ({nature.ToLowerInvariant()})\nSeuil d’alerte SMS : {seuil.ToString("N2", Fr)} DA",
                null, Math.Abs(ecart) > seuil ? texte : null), ct);
        });
    }

    /// <summary>
    /// Ni motif (saisi librement, il pourrait citer un patient) ni liste des patients : produit, lot et nombres seulement.
    /// Patients à contacter : mentionnés seulement si des boîtes du lot ont été vendues.
    /// </summary>
    public Task LotBloqueAsync(string produit, string numeroLot, int boitesEnStock, int boitesVendues, CancellationToken ct = default) =>
        ProtegerAsync(TypeNotification.LotBloque, $"Lot {numeroLot} bloqué : {produit}", async () =>
        {
            var r = await depot.ReglagesAsync(ct);
            var texte = $"Lot {numeroLot} de {produit} bloque (rappel) : {boitesEnStock} boite(s) en stock, {boitesVendues} vendue(s).";
            var suite = boitesVendues > 0
                ? "Patients à contacter : liste dans le logiciel (Stock, Traçabilité)."
                : "Aucune boîte vendue : aucun patient à contacter.";
            return await notifications.MettreEnFileAsync(new ContenuNotification(TypeNotification.LotBloque,
                $"Lot {numeroLot} bloqué : {produit}",
                $"Le lot {numeroLot} de {produit} a été bloqué (rappel).\n\nBoîtes en stock : {boitesEnStock}\nBoîtes déjà vendues : {boitesVendues}\n\n{suite}",
                null, r.SmsLotBloque ? texte : null), ct);
        });

    /// <summary>
    /// Ancien lot délaissé : message calculé en base (produit, lots, vendeurs et nombres de boîtes, aucune donnée de patient).
    /// SMS avec le réglage des SMS de lot (court, sans accents : TexteSms le prépare).
    /// </summary>
    public Task SurveillanceAsync(string message, bool urgent, CancellationToken ct = default)
    {
        var objet = urgent ? "URGENT : contrôle interne, point à examiner" : "Contrôle interne : point à examiner";
        return ProtegerAsync(TypeNotification.Surveillance, objet, async () =>
        {
            var r = await depot.ReglagesAsync(ct);
            return await notifications.MettreEnFileAsync(new ContenuNotification(TypeNotification.Surveillance, objet,
                $"{message}\n\nDétail dans le logiciel : Contrôle interne (titulaire).", null, r.SmsLotBloque ? $"{objet}. {message}" : null), ct);
        });
    }

    public Task LotDelaisseAsync(string message, bool urgent, CancellationToken ct = default)
    {
        var objet = urgent ? "URGENT : ancien lot délaissé, inventaire à faire" : "Ancien lot délaissé, inventaire à faire";
        return ProtegerAsync(TypeNotification.LotDelaisse, objet, async () =>
        {
            var r = await depot.ReglagesAsync(ct);
            return await notifications.MettreEnFileAsync(new ContenuNotification(TypeNotification.LotDelaisse, objet,
                $"{message}\n\nDans le logiciel : Alertes, « Lancer l’inventaire de ce produit ».",
                null, r.SmsLotBloque ? $"{objet}. {message}" : null), ct);
        });
    }

    public Task SauvegardeEnEchecAsync(bool disqueAbsent, string detail, CancellationToken ct = default)
    {
        var objet = disqueAbsent ? "Sauvegarde impossible : disque externe absent" : "Sauvegarde échouée";
        return ProtegerAsync(TypeNotification.Sauvegarde, objet, async () =>
        {
            var r = await depot.ReglagesAsync(ct);
            return await notifications.MettreEnFileAsync(new ContenuNotification(TypeNotification.Sauvegarde, objet,
                $"{objet}.\n\n{detail}", null, r.SmsSauvegarde ? $"{objet}. {detail}" : null), ct);
        });
    }

    /// <summary>Annulations de vente au-delà du seuil sur une même session de caisse (envoyé avec les alertes d'écart de caisse).</summary>
    public Task AnnulationsAsync(string poste, int annulations, int seuil, CancellationToken ct = default) =>
        ProtegerAsync(TypeNotification.EcartCaisse, $"{annulations} ventes annulées sur la caisse {poste}", () =>
            notifications.MettreEnFileAsync(new ContenuNotification(TypeNotification.EcartCaisse,
                $"{annulations} ventes annulées sur la caisse {poste}",
                $"Ventes annulées depuis l’ouverture de la caisse.\n\nCaisse : {poste}\nAnnulations : {annulations}\nSeuil d’alerte : {seuil}\n\nDétail : journal des actions.",
                null, $"{annulations} ventes annulees sur la caisse {poste} (seuil {seuil})."), ct));

    /// <summary>Écarts d'inventaire au-delà des seuils : le titulaire doit les valider (Stock, Inventaire).</summary>
    public Task EcartsInventaireAsync(string rayon, int lignes, int boites, decimal valeur, CancellationToken ct = default)
    {
        var montant = (valeur > 0 ? "+" : "") + valeur.ToString("N2", Fr) + " DA";
        var signe = (boites > 0 ? "+" : "") + boites;
        return ProtegerAsync(TypeNotification.EcartInventaire, $"Inventaire rayon {rayon} : {lignes} écart(s) à valider", () =>
            notifications.MettreEnFileAsync(new ContenuNotification(TypeNotification.EcartInventaire,
                $"Inventaire rayon {rayon} : {lignes} écart(s) à valider",
                $"Écarts d’inventaire au-delà des seuils, à valider.\n\nRayon : {rayon}\nÉcarts : {lignes}\nBoîtes : {signe}\nValeur au prix d’achat : {montant}\n\nÀ faire : validez-les ou faites recompter (Stock, Inventaire).",
                null, $"Inventaire rayon {rayon} : {lignes} ecart(s) a valider ({signe} boite(s), {montant})."), ct));
    }

    /// <param name="mise">Nombre de messages mis en file (négatif : rien à envoyer, pas un échec).</param>
    private async Task ProtegerAsync(TypeNotification type, string objet, Func<Task<int>> mise)
    {
        string raison;
        try
        {
            if (await mise() != 0) return;
            raison = AucunDestinataire;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            trace?.Erreur($"Alerte au titulaire non mise en file : {objet}", e);
            raison = $"Mise en file impossible : {e.Message}";
        }
        await AlerteNonEnvoyeeSignalee.NoterAsync(nonEnvoyees, trace, type, objet, raison);
    }
}

// ---- Alertes qui n'ont pas pu partir (bandeau du titulaire) ----------------------

/// <summary>Alerte au titulaire qui n'a pas pu partir : mise en file impossible, aucun destinataire, ou envoi abandonné.</summary>
public sealed record AlerteNonEnvoyee(long Id, string Type, string Objet, string Raison, DateTime CreeLe);

/// <summary>Alertes non encore vues par le titulaire : nombre total et les plus récentes.</summary>
public sealed record AlertesNonEnvoyeesNonVues(int Nombre, IReadOnlyList<AlerteNonEnvoyee> Dernieres);

public interface IAlertesNonEnvoyees
{
    Task NoterAsync(TypeNotification type, string objet, string raison, CancellationToken ct = default);
    Task<AlertesNonEnvoyeesNonVues> NonVuesAsync(int limite, CancellationToken ct = default);
    /// <summary>Marque vues les alertes non vues jusqu'à l'identifiant affiché (celles arrivées depuis restent signalées).</summary>
    Task<int> MarquerVuesAsync(long jusquA, long utilisateurId, CancellationToken ct = default);
}

public static class AlerteNonEnvoyeeSignalee
{
    /// <summary>Mémorise l'échec pour le bandeau ; si la base est elle-même indisponible, seul le journal du serveur le garde.</summary>
    public static async Task NoterAsync(IAlertesNonEnvoyees? nonEnvoyees, ITraceErreurs? trace, TypeNotification type, string objet, string raison)
    {
        if (nonEnvoyees is null) return;
        try { await nonEnvoyees.NoterAsync(type, objet, raison, CancellationToken.None); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            trace?.Erreur($"Alerte non envoyée non mémorisée pour le titulaire : {objet} ({raison})", e);
        }
    }

    /// <summary>
    /// Après l'opération déjà enregistrée : une erreur avant ou pendant l'alerte (lecture du détail…) ne la fait jamais échouer ;
    /// elle est inscrite au journal du serveur.
    /// </summary>
    public static async Task SansBlocageAsync(Func<Task> alerte, string quoi, ITraceErreurs? trace)
    {
        try { await alerte(); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            trace?.Erreur($"Alerte au titulaire non envoyée : {quoi}", e);
        }
    }
}

/// <summary>Bandeau de l'accueil : réservé au titulaire.</summary>
public sealed class ConsulterAlertesNonEnvoyees(IAlertesNonEnvoyees nonEnvoyees, IPermissionService droits)
{
    public async Task<AlertesNonEnvoyeesNonVues> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter les alertes non envoyées", ct);
        return await nonEnvoyees.NonVuesAsync(5, ct);
    }
}

public sealed class MarquerAlertesNonEnvoyeesVues(IAlertesNonEnvoyees nonEnvoyees, IPermissionService droits)
{
    public async Task<int> ExecuterAsync(long jusquA, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "marquer les alertes non envoyées comme vues", ct);
        if (jusquA <= 0) throw new RegleMetierException("Aucune alerte à marquer comme vue.");
        return await nonEnvoyees.MarquerVuesAsync(jusquA, utilisateurId, ct);
    }
}

// ---- Administration : réglages, destinataires, test, file ---------------------

public sealed class ObtenirReglagesNotification(INotificationsDepot depot, IPermissionService droits)
{
    public async Task<ReglagesNotification> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter les réglages des notifications", ct);
        return await depot.ReglagesAsync(ct);
    }
}

public sealed partial class ModifierReglagesNotification(INotificationsDepot depot, IProtectionSecret secrets, IJournal journal, IPermissionService droits,
    SuperAdmin.EnvoiCodeSuperAdmin? copieSuperAdmin = null)
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^COM\d{1,3}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FormatPort();

    public async Task ExecuterAsync(ModificationReglagesNotification m, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "modifier les réglages des notifications", ct);
        static string? Net(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var r = m with
        {
            SmtpServeur = Net(m.SmtpServeur), SmtpExpediteur = Net(m.SmtpExpediteur), SmtpIdentifiant = Net(m.SmtpIdentifiant),
            ModemPort = Net(m.ModemPort)?.ToUpperInvariant()
        };
        if (r.SmtpPort is < 1 or > 65535) throw new RegleMetierException("Port SMTP entre 1 et 65535.", "smtp_port");
        if (r.SmtpExpediteur is not null && !r.SmtpExpediteur.Contains('@')) throw new RegleMetierException("Adresse de l’expéditeur invalide.", "smtp_expediteur");
        if ((r.SmtpServeur is null) != (r.SmtpExpediteur is null))
            throw new RegleMetierException("Renseignez à la fois le serveur SMTP et l’adresse de l’expéditeur, ou aucun des deux.");
        if (r.ModemPort is not null && !FormatPort().IsMatch(r.ModemPort)) throw new RegleMetierException("Port du modem invalide (ex. COM3).");
        if (r.SmsSeuilEcart is < 0) throw new RegleMetierException("Le seuil d’écart ne peut pas être négatif.", "sms_seuil");

        var changer = m.NouveauMotDePasse is not null;
        var chiffre = string.IsNullOrEmpty(m.NouveauMotDePasse) ? null : secrets.Chiffrer(m.NouveauMotDePasse);
        await depot.EnregistrerReglagesAsync(r, changer, chiffre, ct);
        // Copie pour le code du super admin (envoyé même si la base est détruite) ; un échec n'empêche pas l'enregistrement
        try { copieSuperAdmin?.Recopier(r with { NouveauMotDePasse = m.NouveauMotDePasse }); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException) { }
        await journal.EcrireAsync(utilisateurId, "reglages_notification", "parametre_notification", 1,
            new Dictionary<string, object?> { ["smtp"] = r.SmtpServeur, ["modem"] = r.ModemPort, ["mot_de_passe_modifie"] = changer },
            Domain.Securite.Permissions.AdminParametres, ct);
    }
}

public sealed class ListerDestinataires(INotificationsDepot depot, IPermissionService droits)
{
    public async Task<IReadOnlyList<Destinataire>> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter les destinataires", ct);
        return await depot.DestinatairesAsync(ct);
    }
}

/// <summary>Création ou modification ; un changement d'adresse retire la vérification.</summary>
public sealed class EnregistrerDestinataire(INotificationsDepot depot, IPermissionService droits, IJournal journal)
{
    public async Task<long> ExecuterAsync(long? id, SaisieDestinataire s, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "renseigner les destinataires des alertes", ct);
        RegleDestinataire.Verifier(s.Nom, s.Email, s.Telephone, s.CanalEmail, s.CanalSms, s.Types);
        var propre = s with
        {
            Nom = s.Nom!.Trim(),
            Email = string.IsNullOrWhiteSpace(s.Email) ? null : s.Email.Trim(),
            Telephone = string.IsNullOrWhiteSpace(s.Telephone) ? null : RegleDestinataire.NormaliserTelephone(s.Telephone)
        };
        if (id is null)
        {
            var nouveau = await depot.CreerDestinataireAsync(propre, ct);
            await Journaliser(nouveau, propre, utilisateurId, ct);
            return nouveau;
        }

        var avant = (await depot.DestinatairesAsync(ct)).FirstOrDefault(d => d.Id == id) ?? throw new RegleMetierException("Destinataire introuvable.");
        await depot.ModifierDestinataireAsync(id.Value, propre, ct);
        if (avant.Email != propre.Email || avant.Telephone != propre.Telephone)
            await depot.MarquerValideAsync(id.Value, false, ct);
        await Journaliser(id.Value, propre, utilisateurId, ct);
        return id.Value;
    }

    private Task Journaliser(long id, SaisieDestinataire d, long utilisateurId, CancellationToken ct) =>
        journal.EcrireAsync(utilisateurId, "destinataire_notification", "destinataire_notification", id,
            new Dictionary<string, object?> { ["nom"] = d.Nom, ["email"] = d.Email, ["telephone"] = d.Telephone, ["actif"] = d.Actif },
            Domain.Securite.Permissions.AdminParametres, ct);
}

/// <summary>Message de test vers un destinataire enregistré (même non vérifié : c'est ce qui permet de le vérifier).</summary>
public sealed class EnvoyerMessageTest(INotificationsDepot depot, IPermissionService droits, Administration.IPharmacie? pharmacie = null)
{
    public async Task<int> ExecuterAsync(long destinataireId, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "envoyer un message de test", ct);
        var d = (await depot.DestinatairesAsync(ct)).FirstOrDefault(x => x.Id == destinataireId)
                ?? throw new RegleMetierException("Destinataire introuvable : seules les adresses enregistrées reçoivent des messages.");
        if (!d.Actif) throw new RegleMetierException("Destinataire désactivé.");
        var reglages = await depot.ReglagesAsync(ct);
        var contenu = new ContenuNotification(TypeNotification.Test, "Pharma Officine : message de test",
            $"Bonjour {d.Nom}, ce message de test confirme que vous recevrez les alertes de la pharmacie. Signalez sa réception au titulaire pour valider votre adresse.",
            null, "Pharma Officine : message de test. Signalez sa reception au titulaire pour valider votre numero.");
        var messages = NotificationService.Construire(contenu, [d], reglages.SmsActifs, await NotificationService.SignatureAsync(pharmacie, ct));
        if (messages.Count == 0)
            throw new RegleMetierException(d.CanalSms && !reglages.SmsActifs ? "Modem GSM non configuré : aucun SMS possible." : "Aucun canal utilisable pour ce destinataire.");
        await depot.AjouterMessagesAsync(messages, ct);
        return messages.Count;
    }
}

public sealed class ValiderDestinataire(INotificationsDepot depot, IPermissionService droits)
{
    public async Task ExecuterAsync(long id, bool valide, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "valider un destinataire", ct);
        // Vérifier une adresse se fait par le code reçu (VerifierCodeDestinataire) ; ici, seulement retirer la vérification
        if (valide) throw new RegleMetierException("Pour vérifier une adresse, cliquez sur « Vérifier » et saisissez le code reçu par le destinataire.");
        if (!await depot.MarquerValideAsync(id, valide, ct)) throw new RegleMetierException("Destinataire introuvable.");
    }
}

/// <summary>Filtres de la file des messages : type (objet), statut, période de création (jours inclus).</summary>
public sealed record FiltreFile(string? Type = null, string? Statut = null, DateOnly? Du = null, DateOnly? Au = null);

public interface IFileMessagesLecture
{
    Task<Page<MessageEnFile>> PageAsync(DemandePage page, FiltreFile filtre, CancellationToken ct = default);
}

public sealed class ConsulterFileMessages(INotificationsDepot depot, IPermissionService droits, IFileMessagesLecture? pages = null)
{
    public static readonly IReadOnlyList<string> Statuts = ["en_attente", "envoye", "echec"];

    /// <summary>File paginée et filtrée (écran Administration, Notifications), comme les autres listes.</summary>
    public async Task<Page<MessageEnFile>> PageAsync(DemandePage page, FiltreFile filtre, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter la file des messages", ct);
        if (filtre.Du is { } du && filtre.Au is { } au && au < du) throw new RegleMetierException("La date de fin précède la date de début.", "au");
        if (filtre.Statut is { } s && !Statuts.Contains(s)) throw new RegleMetierException("Statut inconnu.", "statut");
        if (filtre.Type is { } t && !Enum.GetValues<TypeNotification>().Any(x => x.Code() == t)) throw new RegleMetierException("Type de message inconnu.", "type");
        return await (pages ?? throw new InvalidOperationException("Lecture paginée de la file non branchée.")).PageAsync(page, filtre, ct);
    }

    public async Task<IReadOnlyList<MessageEnFile>> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter la file des messages", ct);
        return await depot.FileAsync(100, ct);
    }
}
