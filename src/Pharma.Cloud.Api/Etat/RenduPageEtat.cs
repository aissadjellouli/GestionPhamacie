using System.Globalization;
using System.Net;
using System.Text;

namespace Pharma.Cloud.Api.Etat;

/// <summary>
/// Page HTML autonome (aucune ressource externe, aucun script), rafraîchie toutes les 10 s, claire ou sombre selon le système.
/// Tout texte venu de la base est encodé (WebUtility.HtmlEncode) : un libellé d'officine ne peut pas injecter de balise.
/// </summary>
public static class RenduPageEtat
{
    public const int RafraichissementSecondes = 10;

    private const string Style = """
        :root{color-scheme:light dark;--fond:#ffffff;--texte:#1f2328;--doux:#59636e;--bord:#d1d9e0;--entete:#f6f8fa;--ok:#1a7f37;--alerte:#9a6700;--ko:#cf222e}
        @media (prefers-color-scheme:dark){:root{--fond:#0d1117;--texte:#e6edf3;--doux:#9198a1;--bord:#3d444d;--entete:#151b23;--ok:#3fb950;--alerte:#d29922;--ko:#f85149}}
        body{margin:0;padding:16px 24px;background:var(--fond);color:var(--texte);font:14px/1.45 "Segoe UI",system-ui,sans-serif}
        h1{font-size:20px;margin:0 0 4px}h2{font-size:16px;margin:24px 0 8px}h3{font-size:14px;margin:16px 0 6px}
        p.meta{color:var(--doux);margin:0 0 12px}
        table{border-collapse:collapse;width:100%;margin-bottom:8px}
        th,td{border:1px solid var(--bord);padding:4px 8px;text-align:left;vertical-align:top}
        th{background:var(--entete);font-weight:600}
        td.n{text-align:right;font-variant-numeric:tabular-nums}
        code{font-family:Consolas,monospace;font-size:13px}
        .ok{color:var(--ok)}.alerte{color:var(--alerte)}.ko{color:var(--ko)}.doux{color:var(--doux)}
        .erreur{border:1px solid var(--ko);color:var(--ko);padding:8px 12px}
        """;

    public static string Html(EtatCentral? etat, string? erreur, DateTimeOffset maintenant)
    {
        var h = new StringBuilder(16 * 1024);
        h.Append("<!doctype html><html lang=\"fr\"><head><meta charset=\"utf-8\">")
         .Append("<meta http-equiv=\"refresh\" content=\"").Append(RafraichissementSecondes).Append("\">")
         .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
         .Append("<title>État du central (développement)</title><style>").Append(Style).Append("</style></head><body>")
         .Append("<h1>État du central — développement</h1><p class=\"meta\">Lecture seule, rafraîchie toutes les ")
         .Append(RafraichissementSecondes).Append(" s · ").Append(E(Heure(maintenant)));
        if (etat is not null) h.Append(" · rôle ").Append("<code>").Append(E(etat.Role)).Append("</code>");
        h.Append(" · <a href=\"etat.json\">etat.json</a></p>");
        if (erreur is not null) h.Append("<p class=\"erreur\">Lecture impossible : ").Append(E(erreur)).Append("</p>");
        if (etat is not null) Corps(h, etat, maintenant);
        return h.Append("</body></html>").ToString();
    }

    private static void Corps(StringBuilder h, EtatCentral e, DateTimeOffset maintenant)
    {
        var libelles = e.Officines.ToDictionary(o => o.Id, o => o.Libelle);

        h.Append("<h2>Officines (").Append(e.Officines.Count).Append(")</h2>");
        Tableau(h, ["Libellé", "Code", "Créée le", "Résiliée / partie le"], e.Officines.Select(o => new[]
        {
            E(o.Libelle), Code(o.Id.ToString("D")), E(Heure(o.CreeLe)),
            o.ResilieeLe is { } r ? Classe("alerte", Heure(r)) : Classe("doux", "—")
        }));

        h.Append("<h2>Certificats (").Append(e.Certificats.Count).Append(")</h2>");
        Tableau(h, ["Officine", "Serveur", "Empreinte (début)", "Émis le", "Échéance", "État"], e.Certificats.Select(c => new[]
        {
            Officine(libelles, c.Officine), E(c.Serveur), Code(c.DebutEmpreinte + "…"), E(Heure(c.EmisLe)), E(Heure(c.Echeance)),
            c.RevoqueLe is { } r ? Classe("ko", "révoqué le " + Heure(r))
                : c.Echeance <= maintenant ? Classe("ko", "expiré") : Classe("ok", "valide")
        }));

        h.Append("<h2>Codes d'activation (").Append(e.Codes.Count).Append(")</h2>");
        Tableau(h, ["Officine", "Serveur", "Créé le", "Expire le", "État"], e.Codes.Select(c => new[]
        {
            Officine(libelles, c.Officine), E(c.Serveur), E(Heure(c.CreeLe)), E(Heure(c.ExpireLe)),
            c.UtiliseLe is { } u ? Classe("ok", "utilisé le " + Heure(u))
                : c.ExpireLe <= maintenant ? Classe("doux", "expiré, non utilisé") : Classe("alerte", "non utilisé")
        }));

        foreach (var d in e.Details)
        {
            h.Append("<h2>").Append(Officine(libelles, d.Officine)).Append("</h2>");
            h.Append("<h3>Chargement complet</h3>");
            Tableau(h, ["N°", "Motif", "État", "Époque", "Début", "Fin", "Lots", "Lignes", "Octets reçus", "Absentes"], d.Chargements.Select(c => new[]
            {
                N(c.Numero), E(c.Motif), Classe(c.Etat == "en_cours" ? "alerte" : c.Etat == "termine" ? "ok" : "doux", c.Etat == "en_cours" ? "en cours" : c.Etat == "termine" ? "terminé" : c.Etat),
                N(c.Epoque), E(Heure(c.DebutLe)), c.FinLe is { } f ? E(Heure(f)) : Classe("doux", "—"), N(c.Lots), N(c.Lignes), N(c.OctetsRecus),
                c.Absentes is { } a ? N(a) : Classe("doux", "—")
            }));
            h.Append("<h3>Époques</h3>");
            Tableau(h, ["Époque", "Dernier lot", "Dernier reçu le"], d.Epoques.Select(p => new[]
            {
                N(p.Epoque), N(p.DernierLot), p.DernierRecuLe is { } r ? E(Heure(r)) : Classe("doux", "—")
            }));
            h.Append("<h3>Derniers lots reçus (").Append(LecteurEtatCentral.LotsParOfficine).Append(" au plus)</h3>");
            Tableau(h, ["N°", "Époque", "Lignes", "Taille (octets)", "Reçu le"], d.Lots.Select(l => new[]
            {
                N(l.Numero), N(l.Epoque), N(l.Lignes), N(l.Octets), E(Heure(l.RecuLe))
            }));
        }

        h.Append("<h2>Journal de sécurité (").Append(LecteurEtatCentral.LignesJournal).Append(" dernières lignes)</h2>");
        Tableau(h, ["N°", "Le", "Officine", "Certificat (début)", "Événement", "Détail"], e.Journal.Select(j => new[]
        {
            N(j.Id), E(Heure(j.Le)), j.Officine is { } o ? Officine(libelles, o) : Classe("doux", "—"),
            j.DebutCertificat is { } c ? Code(c + "…") : Classe("doux", "—"), Code(j.Evenement), E(j.Detail ?? "")
        }));
    }

    private static void Tableau(StringBuilder h, string[] entetes, IEnumerable<string[]> lignes)
    {
        h.Append("<table><thead><tr>");
        foreach (var t in entetes) h.Append("<th>").Append(E(t)).Append("</th>");
        h.Append("</tr></thead><tbody>");
        var vide = true;
        foreach (var l in lignes)
        {
            vide = false;
            h.Append("<tr>");
            foreach (var c in l) h.Append(c.StartsWith("<td", StringComparison.Ordinal) ? c : "<td>" + c + "</td>");
            h.Append("</tr>");
        }
        if (vide) h.Append("<tr><td class=\"doux\" colspan=\"").Append(entetes.Length).Append("\">aucune ligne</td></tr>");
        h.Append("</tbody></table>");
    }

    /// <summary>Encodage HTML de tout texte (venu de la base ou non).</summary>
    public static string E(string texte) => WebUtility.HtmlEncode(texte);

    private static string Code(string texte) => "<code>" + E(texte) + "</code>";

    private static string Classe(string classe, string texte) => "<span class=\"" + classe + "\">" + E(texte) + "</span>";

    private static string N(long n) => "<td class=\"n\">" + n.ToString("N0", CultureInfo.GetCultureInfo("fr-FR")) + "</td>";

    private static string Officine(Dictionary<Guid, string> libelles, Guid id) =>
        libelles.TryGetValue(id, out var l) ? E(l) + " <code class=\"doux\">" + E(id.ToString("D")[..8]) + "</code>" : Code(id.ToString("D"));

    private static string Heure(DateTimeOffset d) => d.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
