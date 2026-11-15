using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Pharma.Shared;

namespace Pharma.Desktop.Services;

/// <summary>
/// Ticket de caisse imprimé par Windows, sur l'imprimante déclarée pour le poste (variable PHARMA_IMPRIMANTE_TICKETS,
/// nom tel qu'il apparaît dans Windows) ou, à défaut, l'imprimante par défaut. Largeur du rouleau :
/// PHARMA_TICKET_LARGEUR_MM (80 par défaut, 58 pour les petits rouleaux).
/// </summary>
public sealed class ImprimanteTickets(ReglagesPoste reglages)
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Réglage du poste (Administration, Imprimantes et étiquettes), sinon PHARMA_IMPRIMANTE_TICKETS.</summary>
    public string? NomDeclare =>
        reglages.Actuelles.ImprimanteTickets is { Length: > 0 } r ? r.Trim()
        : Environment.GetEnvironmentVariable("PHARMA_IMPRIMANTE_TICKETS") is { Length: > 0 } n ? n.Trim() : null;

    /// <summary>Largeurs de rouleau du marché (2026-11-15), mêmes valeurs que FormatTicket côté serveur ; défaut 80 mm.</summary>
    public static readonly IReadOnlyList<int> Largeurs = [57, 58, 76, 80, 112];

    public int LargeurMm =>
        reglages.Actuelles.LargeurTicketMm is int r && Largeurs.Contains(r) ? r
        : int.TryParse(Environment.GetEnvironmentVariable("PHARMA_TICKET_LARGEUR_MM"), out var l) && Largeurs.Contains(l) ? l : 80;

    public bool Automatique => reglages.Actuelles.TicketAutomatique;

    /// <summary>Imprimantes installées dans Windows sur ce poste (nom, imprimante par défaut).</summary>
    public static IReadOnlyList<(string Nom, bool ParDefaut)> Installees()
    {
        try
        {
            using var serveur = new LocalPrintServer();
            var defaut = LocalPrintServer.GetDefaultPrintQueue()?.FullName;
            return serveur.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections])
                .Select(q => (q.FullName, q.FullName == defaut)).OrderBy(q => q.FullName).ToList();
        }
        catch (Exception e) when (e is PrintQueueException or InvalidOperationException) { return []; }
    }

    /// <summary>Ticket fictif pour vérifier l'imprimante et la largeur du rouleau.</summary>
    public Task<string?> ImprimerEssaiAsync(string? pharmacie) => ImprimerAsync(new TicketDto(
        "ESSAI", DateTime.Now, "Ticket d’essai", null, "validee", "CNAS", 80m,
        [new LigneTicketDto("Produit d’essai 500 mg cp", 2, 120m, 240m, 0m), new LigneTicketDto("Produit d’essai sirop 125 ml", 1, 350m, 350m, 0m)],
        590m, 0m, 0m, 590m, "especes", pharmacie, null, null, null), 410m);

    /// <summary>
    /// Imprimante de tickets du poste qui enregistre un fichier (Microsoft Print to PDF, XPS, OneNote…) : le ticket est alors montré à
    /// l'écran en PDF au lieu d'ouvrir « Enregistrer sous » (2026-11-15).
    /// </summary>
    public bool ImprimanteFichier
    {
        get
        {
            try
            {
                if (NomDeclare is { } nom) return EstFichier(nom);
                return LocalPrintServer.GetDefaultPrintQueue() is { } defaut && EstFichier(defaut);
            }
            catch (Exception e) when (e is PrintQueueException or InvalidOperationException) { return false; }
        }
    }

    /// <summary>Imprime le ticket ; renvoie un message d'erreur lisible, ou null si l'impression est partie.</summary>
    public Task<string?> ImprimerAsync(TicketDto ticket, decimal? rendu = null) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var file = TrouverFile(out var erreur);
                if (file is null) return erreur;
                var doc = Construire(ticket, rendu);
                var dialogue = new PrintDialog { PrintQueue = file };
                dialogue.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, $"Ticket {ticket.Numero}");
                return (string?)null;
            }
            catch (PrintQueueException e) { return $"Impression impossible ({e.Message}) : vérifiez que l’imprimante est allumée et branchée."; }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return $"Impression impossible : {e.Message}";
            }
        }).Task;

    /// <summary>
    /// Imprimante « fichier » de Windows (Microsoft Print to PDF, XPS, OneNote, port FILE: ou PORTPROMPT:…) : elle demande où
    /// enregistrer un fichier au lieu d'imprimer.
    /// </summary>
    public static bool EstFichier(PrintQueue file)
    {
        string port;
        try { port = file.QueuePort?.Name ?? ""; }
        catch (Exception e) when (e is PrintQueueException or InvalidOperationException) { port = ""; }
        return port is "PORTPROMPT:" or "FILE:" or "nul:" || port.StartsWith("XPS", StringComparison.OrdinalIgnoreCase)
            || file.FullName.Contains("Print to PDF", StringComparison.OrdinalIgnoreCase)
            || file.FullName.Contains("XPS Document Writer", StringComparison.OrdinalIgnoreCase)
            || file.FullName.StartsWith("OneNote", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Vrai si l'imprimante de ce nom (installée dans Windows) enregistre un fichier au lieu d'imprimer.</summary>
    public static bool EstFichier(string nom)
    {
        try
        {
            using var serveur = new LocalPrintServer();
            var file = serveur.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections])
                .FirstOrDefault(q => string.Equals(q.FullName, nom, StringComparison.OrdinalIgnoreCase) || string.Equals(q.Name, nom, StringComparison.OrdinalIgnoreCase));
            return file is not null && EstFichier(file);
        }
        catch (Exception e) when (e is PrintQueueException or InvalidOperationException) { return false; }
    }

    /// <summary>Nom de l'imprimante par défaut de Windows si c'est une imprimante « fichier » (Microsoft Print to PDF…) ; sinon null.</summary>
    public static string? DefautFichier()
    {
        try
        {
            var defaut = LocalPrintServer.GetDefaultPrintQueue();
            return defaut is not null && EstFichier(defaut) ? defaut.FullName : null;
        }
        catch (Exception e) when (e is PrintQueueException or InvalidOperationException) { return null; }
    }

    private static string MessageFichier(string nom) =>
        $"Aucune imprimante de tickets n’est choisie pour ce poste, et l’imprimante par défaut de Windows est « {nom} », qui enregistre "
        + "un fichier au lieu d’imprimer : rien n’a été imprimé. Choisissez l’imprimante de tickets dans Administration › Imprimantes et étiquettes.";

    private PrintQueue? TrouverFile(out string? erreur)
    {
        erreur = null;
        using var serveur = new LocalPrintServer();
        if (NomDeclare is null)
        {
            var defaut = LocalPrintServer.GetDefaultPrintQueue();
            if (defaut is null) erreur = "Aucune imprimante par défaut dans Windows : installez l’imprimante de tickets ou déclarez-la (PHARMA_IMPRIMANTE_TICKETS).";
            // Imprimante par défaut « fichier » (Microsoft Print to PDF…) : pas d'impression, une fenêtre « Enregistrer » bloquerait
            // le comptoir ; l'imprimante choisie exprès dans le réglage, elle, est respectée même si c'est un PDF
            else if (EstFichier(defaut)) { erreur = MessageFichier(defaut.FullName); return null; }
            return defaut;
        }
        var file = serveur.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections])
            .FirstOrDefault(q => string.Equals(q.FullName, NomDeclare, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(q.Name, NomDeclare, StringComparison.OrdinalIgnoreCase));
        if (file is null) erreur = $"Imprimante « {NomDeclare} » introuvable dans Windows (PHARMA_IMPRIMANTE_TICKETS).";
        return file;
    }

    private FlowDocument Construire(TicketDto t, decimal? rendu)
    {
        var largeur = LargeurMm * 96 / 25.4;
        var doc = new FlowDocument
        {
            PageWidth = largeur, ColumnWidth = largeur, PagePadding = new Thickness(6, 4, 6, 4),
            FontFamily = new FontFamily("Arial"), FontSize = LargeurMm < 70 ? 9 : 10.5
        };
        Paragraph P(string texte, bool gras = false, TextAlignment alignement = TextAlignment.Left, double? taille = null)
        {
            var p = new Paragraph(new Run(texte)) { Margin = new Thickness(0, 0, 0, 1), TextAlignment = alignement };
            if (gras) p.FontWeight = FontWeights.Bold;
            if (taille is { } s) p.FontSize = s;
            doc.Blocks.Add(p);
            return p;
        }
        void Separateur() => doc.Blocks.Add(new Paragraph(new Run(new string('-', LargeurMm < 70 ? 32 : 44))) { Margin = new Thickness(0, 2, 0, 2), TextAlignment = TextAlignment.Center });
        void Montant(string libelle, decimal montant, bool gras = false)
        {
            var table = new Table { CellSpacing = 0, Margin = new Thickness(0) };
            table.Columns.Add(new TableColumn { Width = new GridLength(3, GridUnitType.Star) });
            table.Columns.Add(new TableColumn { Width = new GridLength(2, GridUnitType.Star) });
            var groupe = new TableRowGroup();
            var ligne = new TableRow();
            ligne.Cells.Add(new TableCell(new Paragraph(new Run(libelle)) { Margin = new Thickness(0) }));
            ligne.Cells.Add(new TableCell(new Paragraph(new Run(Da(montant))) { Margin = new Thickness(0), TextAlignment = TextAlignment.Right }));
            if (gras) ligne.FontWeight = FontWeights.Bold;
            groupe.Rows.Add(ligne);
            table.RowGroups.Add(groupe);
            doc.Blocks.Add(table);
        }

        P(t.Pharmacie ?? "Pharmacie", gras: true, TextAlignment.Center, doc.FontSize + 2);
        if (t.Adresse is { Length: > 0 } a) P(a, alignement: TextAlignment.Center);
        if (t.Telephone is { Length: > 0 } tel) P($"Tél. {tel}", alignement: TextAlignment.Center);
        if (t.Nif is { Length: > 0 } nif) P($"NIF {nif}", alignement: TextAlignment.Center);
        Separateur();
        // Reçu d'un encaissement de dette (numéro RC-…) : titre propre (2026-11-15)
        P(t.Numero.StartsWith("RC-", StringComparison.Ordinal) ? $"Reçu d’encaissement de dette {t.Numero}" : $"Ticket {t.Numero}", gras: true);
        P($"{t.Date.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Fr)} · {t.Vendeur}{(t.Poste is null ? "" : $" · {t.Poste}")}");
        if (t.Client is { Length: > 0 } client) P($"Client : {client}");
        if (t.Statut == "annulee") P("VENTE ANNULÉE", gras: true, TextAlignment.Center);
        Separateur();
        foreach (var l in t.Lignes)
        {
            P(l.Designation, gras: true);   // prestation : avec son taux de TVA (script 105)
            Montant($"  {l.Quantite} × {Da(l.PrixUnitaire)}", l.Montant);
            // Remise par produit (ou don partiel sur un médicament) : pourcentage et montant sous la ligne
            if (l.Reduction > 0) Montant($"  {(l.DonPartiel ? "Don" : "Remise")} {l.PctReduction:0} %", -l.Reduction);
        }
        Separateur();
        // Médicaments et autres produits (parapharmacie…), SHP compris dans les prix
        var autres = t.Lignes.Where(l => !l.Medicament).Sum(l => l.Montant);
        if (autres > 0)
        {
            Montant("T. médicaments", t.Lignes.Where(l => l.Medicament).Sum(l => l.Montant));
            Montant("T. autres produits", autres);
        }
        var shp = t.Lignes.Sum(l => l.Shp * l.Quantite);
        if (shp > 0) Montant("dont SHP", shp);
        // Montant initial, remises (produits) et don partiel (médicaments), puis le net
        if (t.Remise > 0 || t.DonPartiel > 0 || t.PartOrganisme > 0) Montant(t.Remise > 0 || t.DonPartiel > 0 ? "Montant initial" : "Total", t.MontantBrut);
        if (t.Remise > 0) Montant("Remises", -t.Remise);
        if (t.DonPartiel > 0) Montant("Don partiel", -t.DonPartiel);
        if (t.PartOrganisme > 0)
            Montant($"Part {t.Organisme ?? "organisme"}{(t.TauxTiersPayant is { } taux ? $" ({taux:0.##} %)" : "")}", -t.PartOrganisme);
        if (t.EcartChifa > 0) Montant("Écart Chifa payé par le patient", t.EcartChifa);
        // Majoration Chifa (D-3) : ajoutée à la part de l'organisme, jamais payée par le patient
        if (t.MajorationChifa > 0) P($"Majoration Chifa à la charge de {t.Organisme ?? "l’organisme"} : {Da(t.MajorationChifa)}");
        Montant("À PAYER", t.PartPatient, gras: true);
        // Part payée par l'avoir du patient (script 106)
        if (t.PayeParAvoir > 0) Montant("Payé par avoir", t.PayeParAvoir);
        if (t.Mode is { } mode && t.PayeParAvoir < t.PartPatient) P($"Réglé : {Mode(mode)}");
        if (rendu is > 0) Montant("Rendu", rendu.Value);
        if (t.Retours is { Count: > 0 } retours)
        {
            Separateur();
            foreach (var ret in retours) P(ret);
        }
        Separateur();
        P("Merci de votre visite.", alignement: TextAlignment.Center);
        return doc;
    }

    private static string Mode(string mode) => mode switch
    {
        "especes" => "espèces",
        "carte" => "carte",
        "cheque" => "chèque",
        "credit" => "crédit (à régler)",
        "avoir" => "avoir du patient",
        _ => mode
    };

    private static string Da(decimal montant) => montant.ToString("N2", Fr) + " DA";
}
