using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using Pharma.Application.Caisse;
using Pharma.Domain.Caisse;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Impression;

/// <summary>Documents PDF produits avec PDFsharp-MigraDoc (licence MIT), polices Windows du serveur.</summary>
public sealed class DocumentsPdf : IDocumentsPdf, Pharma.Application.Rapports.IDocumentsRapports, Pharma.Application.Stock.IDocumentsRegistre,
    Pharma.Application.Factures.IDocumentsFactures, Pharma.Application.Stock.IDocumentsInventaire, Pharma.Application.Caisse.IDocumentsCredit, Pharma.Application.Caisse.IDocumentsTicket,
    Pharma.Application.Caisse.IDocumentsBonSortie
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    static DocumentsPdf()
    {
        if (OperatingSystem.IsWindows())
            GlobalFontSettings.UseWindowsFontsUnderWindows = true;
    }

    public byte[] RapportCloture(DetailSession s, bool avecEcart, decimal seuilEcart, string? pharmacie)
    {
        var doc = NouveauDocument(s.TitreRapport, pharmacie);
        var section = doc.LastSection;

        Titre(section, s.TitreRapport);
        var infos = section.AddParagraph();
        infos.Format.SpaceAfter = Unit.FromPoint(12);
        infos.AddText($"Caisse ouverte par : {s.Employe}   ·   {s.Poste}\n");
        // Caisse partagée : tous ceux qui ont vendu pendant la session
        if (s.Vendeurs is { Count: > 0 } vendeurs)
            infos.AddText($"Vendeurs : {string.Join(", ", vendeurs)}\n");
        infos.AddText($"Ouverture : {s.OuverteLe.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Fr)}   ·   ");
        infos.AddText($"{s.LibelleFin} : {s.FermeeLe?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Fr) ?? "—"}\n");
        infos.AddText($"Ventes de la session : {s.NombreVentes}, pour {Da(s.TotalVentes)} (tous modes de paiement)");
        // Avoirs des patients utilisés en paiement (script 106) : séparés des espèces, jamais dans l'attendu
        if (s.AvoirsUtilises > 0) infos.AddText($"\nPayé par avoir des patients : {Da(s.AvoirsUtilises)} (hors tiroir, non compté dans les espèces attendues)");
        if (s.Releve) infos.AddText("\nRelève : la caisse reste ouverte ; le montant compté devient le fond de la session suivante.");

        SousTitre(section, "Espèces comptées");
        var comptage = Tableau(section, 9.5, 3, 4);
        Entete(comptage, "Coupure", "Nombre", "Montant");
        foreach (var l in s.Comptage)
        {
            var libelle = Coupures.Toutes.FirstOrDefault(c => c.Code == l.Coupure).Libelle ?? l.Coupure;
            Ligne(comptage, libelle, l.Coupure == "autres" ? "—" : l.Nombre.ToString(Fr), Da(l.Montant));
        }
        Ligne(comptage, "Total compté", "", Da(s.Compte ?? 0), gras: true);

        if (avecEcart)
        {
            SousTitre(section, "Résultat");
            var resultat = Tableau(section, 12.5, 4);
            Ligne(resultat, "Fond de caisse", Da(s.FondCaisse));
            if (s.LignesTiroir is { Count: > 0 } tiroir)
            {
                // Sorties et entrées du tiroir (script 096) en lignes séparées : comprises dans l'attendu, aucun écart dû à elles
                Ligne(resultat, "Espèces encaissées (ventes, retours, crédits…)", Da(RecapitulatifTiroir.Encaissees(s.VentesEspeces ?? 0, tiroir)));
                foreach (var l in tiroir)
                    Ligne(resultat, $"{(l.Sens == RegleTiroir.Sortie ? "Sortie du tiroir" : "Entrée au tiroir")} : {l.Motif}",
                        (l.Sens == RegleTiroir.Sortie ? "− " : "+ ") + Da(l.Montant));
            }
            else
                Ligne(resultat, "Ventes en espèces", Da(s.VentesEspeces ?? 0));
            Ligne(resultat, "Attendu", Da(s.Attendu ?? 0), gras: true);
            Ligne(resultat, "Compté", Da(s.Compte ?? 0));
            Ligne(resultat, "Écart", Da(s.Ecart ?? 0), gras: true);
            if (s.Ecart is { } e && Math.Abs(e) > seuilEcart)
            {
                // Manque en rouge, excédent en orange : les deux sont inscrits au journal au-delà du seuil
                var p = section.AddParagraph($"{NatureEcart.Libelle(e)} de {Da(Math.Abs(e))}, au-delà du seuil de {Da(seuilEcart)} : inscrit au journal des actions sensibles.");
                p.Format.SpaceBefore = Unit.FromPoint(8);
                p.Format.Font.Color = NatureEcart.EstManque(e) ? Color.FromRgb(0x91, 0x20, 0x18) : Color.FromRgb(0x93, 0x5F, 0x0B);
            }
        }
        else
        {
            var p = section.AddParagraph("Comptage aveugle : l’attendu et l’écart sont consultables par le titulaire.");
            p.Format.SpaceBefore = Unit.FromPoint(12);
            p.Format.Font.Color = Color.FromRgb(0x47, 0x54, 0x67);
        }

        Signatures(section);
        return Rendre(doc);
    }

    public byte[] RapportActivite(Application.Rapports.RapportActivite r, string? pharmacie)
    {
        var doc = NouveauDocument("Rapport d’activité", pharmacie);
        var section = doc.LastSection;
        Titre(section, $"Rapport d’activité · {r.Periode.Libelle}");

        SousTitre(section, "Synthèse");
        var synthese = Tableau(section, 11.5, 5);
        Ligne(synthese, "Chiffre d’affaires TTC (remises et retours déduits)", Da(r.Synthese.ChiffreAffaires), gras: true);
        Ligne(synthese, $"Dont retours clients de la période ({r.Synthese.Retours.ToString(Fr)})", "- " + Da(r.Synthese.MontantRetours));
        Ligne(synthese, "Nombre de ventes", r.Synthese.NombreVentes.ToString(Fr));
        Ligne(synthese, "Part organismes (Chifa) à recevoir", Da(r.Synthese.PartOrganismes));
        Ligne(synthese, "Crédits clients accordés", Da(r.Synthese.CreditsAccordes));
        Ligne(synthese, "Remises accordées (produits hors médicaments)", Da(r.Synthese.Remises));
        if (r.Synthese.DonsPartiels > 0) Ligne(synthese, "Dons partiels du reste à charge (médicaments)", Da(r.Synthese.DonsPartiels));
        if (r.Synthese.RemisesAutomatiques > 0) Ligne(synthese, "dont remises automatiques", Da(r.Synthese.RemisesAutomatiques));
        if (r.Synthese.VentesAutres > 0)
        {
            Ligne(synthese, "Ventes de médicaments (avant remises et retours)", Da(r.Synthese.VentesMedicaments));
            Ligne(synthese, "Ventes d’autres produits (avant remises et retours)", Da(r.Synthese.VentesAutres));
        }
        if (r.Synthese.Shp > 0) Ligne(synthese, "dont SHP (honoraire du pharmacien)", Da(r.Synthese.Shp));
        Ligne(synthese, "Ventes annulées", r.Synthese.Annulations.ToString(Fr));
        if (r.Marge is { } m)
        {
            Ligne(synthese, "Chiffre d’affaires HT", Da(m.ChiffreAffairesHt));
            Ligne(synthese, "Coût d’achat HT des boîtes vendues", Da(m.CoutAchat));
            Ligne(synthese, "Marge brute", $"{Da(m.Montant)}{(m.Taux is { } t ? $" ({t.ToString("0.0", Fr)} %)" : "")}", gras: true);
            if (r.ApresSorties is { } a)
            {
                Ligne(synthese, "Pertes et dons au prix d’achat HT", "- " + Da(a.PertesHt));
                Ligne(synthese, "TVA à reverser sur les pertes et dons", "- " + Da(a.TvaAReverser));
                Ligne(synthese, "Marge après pertes et dons", Da(a.Montant), gras: true);
            }
        }

        if (r.ParJour.Count > 1)
        {
            var semaines = r.Periode.ParSemaine;
            SousTitre(section, semaines ? "Ventes par semaine" : "Ventes par jour");
            var jours = Tableau(section, 8, 3, 5.5);
            Entete(jours, semaines ? "Semaine du" : "Jour", "Ventes", "Chiffre d’affaires");
            foreach (var j in r.ParJour)
                Ligne(jours, j.Date.ToString(semaines ? "dd/MM/yyyy" : "dddd dd/MM", Fr), j.NombreVentes.ToString(Fr), Da(j.ChiffreAffaires));
        }

        SousTitre(section, "Produits les plus vendus");
        var top = Tableau(section, 9, 3, 4.5);
        Entete(top, "Produit", "Boîtes", "Chiffre d’affaires");
        foreach (var p in r.TopProduits) Ligne(top, p.Produit, p.Quantite.ToString(Fr), Da(p.ChiffreAffaires));
        if (r.TopProduits.Count == 0) Ligne(top, "Aucune vente sur la période", "");

        SousTitre(section, "Clôtures de caisse");
        var clo = r.EcartsVisibles ? Tableau(section, 3.6, 2.3, 2.5, 2.5, 2.5, 1.3, 1.2) : Tableau(section, 5, 3, 3, 2, 2);
        if (r.EcartsVisibles) Entete(clo, "Employé", "Clôture", "Attendu", "Compté", "Écart", "Annul.", "Rem.");
        else Entete(clo, "Employé", "Clôture", "Compté", "Annul.", "Rem.");
        foreach (var c in r.Clotures)
        {
            var row = clo.AddRow();
            row.TopPadding = row.BottomPadding = Unit.FromPoint(3);
            var cellules = r.EcartsVisibles
                ? new[] { c.Employe, Quand(c.FermeeLe), Da(c.Attendu ?? 0), Da(c.Compte ?? 0), Da(c.Ecart ?? 0), c.Annulations.ToString(Fr), c.Remises.ToString(Fr) }
                : new[] { c.Employe, Quand(c.FermeeLe), Da(c.Compte ?? 0), c.Annulations.ToString(Fr), c.Remises.ToString(Fr) };
            for (var i = 0; i < cellules.Length; i++) row.Cells[i].AddParagraph(cellules[i]);
        }
        if (r.Clotures.Count == 0) Ligne(clo, "Aucune clôture sur la période", "");

        if (r.Retours is { Count: > 0 } retours)
        {
            SousTitre(section, "Retours clients");
            var ret = Tableau(section, 3.2, 4.8, 4, 2.3, 2.2);
            Entete(ret, "Vente", "Produits rendus", "Retour", "Montant", "Chiffre diminué");
            foreach (var x in retours)
            {
                var row = ret.AddRow();
                row.TopPadding = row.BottomPadding = Unit.FromPoint(3);
                // Une information par paragraphe dans la cellule : ticket, date et vendeur de la vente ; date, poste et employé du retour
                string[][] cellules =
                [
                    [x.Ticket, Quand(x.DateVente), x.Vendeur],
                    [x.Produits],
                    [Quand(x.DateRetour), x.Poste, x.Employe],
                    x.DetteDiminuee > 0 ? ["- " + Da(x.Montant), $"dont dette : {Da(x.DetteDiminuee)}"] : ["- " + Da(x.Montant)],
                    [x.DeduitDansLaPeriode ? "sur cette période" : $"le {x.DateRetour.ToLocalTime().ToString("dd/MM/yyyy", Fr)}, pas sur cette période"]
                ];
                for (var i = 0; i < cellules.Length; i++)
                    foreach (var texte in cellules[i]) row.Cells[i].AddParagraph(texte);
            }
        }

        if (r.Sorties is { Count: > 0 } sorties)
        {
            // Pertes et dons : boîtes sorties sans vente, en valeur d'achat HT si l'employé voit les marges
            var avecValeur = sorties.All(s => s.Valeur is not null);
            void Totaux(string titre, IEnumerable<Application.Rapports.TotalSorties> totaux)
            {
                SousTitre(section, titre);
                var t = avecValeur ? Tableau(section, 6.5, 2.3, 2.3, 2.6, 2.8) : Tableau(section, 8, 2.8, 2.8, 2.9);
                if (avecValeur) Entete(t, "", "Périmés", "Casses", "Dons", "Valeur HT");
                else Entete(t, "", "Périmés", "Casses", "Dons");
                foreach (var x in totaux)
                {
                    var row = t.AddRow();
                    row.TopPadding = row.BottomPadding = Unit.FromPoint(3);
                    var cellules = avecValeur
                        ? new[] { x.Cle, x.Perimes.ToString(Fr), x.Casses.ToString(Fr), x.Dons.ToString(Fr), Da(x.Valeur ?? 0) }
                        : new[] { x.Cle, x.Perimes.ToString(Fr), x.Casses.ToString(Fr), x.Dons.ToString(Fr) };
                    for (var i = 0; i < cellules.Length; i++) row.Cells[i].AddParagraph(cellules[i]);
                }
            }
            Totaux("Sorties de stock sans vente (pertes et dons)",
                [Application.Rapports.SortiesRapport.Total("Total de la période", sorties), .. Application.Rapports.SortiesRapport.ParEmploye(sorties)]);
            Totaux("Sorties par produit", Application.Rapports.SortiesRapport.ParProduit(sorties));

            SousTitre(section, "Détail des sorties");
            var det = Tableau(section, 2.4, 4.6, 2.4, 1.2, 2.2, 3.7);
            Entete(det, "Date", "Produit et lot", "Motif", "Boîtes", avecValeur ? "Valeur HT" : "", "Employé et raison");
            foreach (var s in sorties)
            {
                var row = det.AddRow();
                row.TopPadding = row.BottomPadding = Unit.FromPoint(3);
                string[][] cellules =
                [
                    [Quand(s.Date)],
                    [s.Produit, "lot " + s.Lot],
                    [Application.Rapports.SortiesRapport.Libelle(s.Motif)],
                    [s.Quantite.ToString(Fr)],
                    [s.Valeur is { } v ? Da(v) : ""],
                    string.IsNullOrWhiteSpace(s.Reference) ? [s.Employe] : [s.Employe, s.Reference]
                ];
                for (var i = 0; i < cellules.Length; i++)
                    foreach (var texte in cellules[i]) row.Cells[i].AddParagraph(texte);
            }
        }

        var note = section.AddParagraph("Ventes validées seulement. Un retour client diminue le chiffre du jour où il est enregistré. Sorties sans vente : boîtes périmées, cassées, données ou remises en échantillon, au prix d’achat HT retenu le jour de la sortie ; elles ne sont pas des mouvements d’argent. Marge brute : chiffre d’affaires HT moins le coût d’achat HT, au prix réel de chaque boîte identifiée, sinon au prix de son lot.");
        note.Format.SpaceBefore = Unit.FromPoint(12);
        note.Format.Font.Size = 8;
        note.Format.Font.Color = Color.FromRgb(0x47, 0x54, 0x67);
        return Rendre(doc);
    }

    private static string Quand(DateTime? d) => d?.ToLocalTime().ToString("dd/MM HH:mm", Fr) ?? "—";

    // ---- Relevé de la dette d'un patient -------------------------------------

    // ---- Ticket de caisse (vue à l'écran, imprimante « fichier ») ----------------

    /// <summary>
    /// Ticket (ou reçu de crédit) au format rouleau (2026-11-15) : page de la largeur du rouleau, hauteur selon le contenu (mise en page
    /// une première fois sur une page très haute, puis page coupée sous le dernier élément : jamais de saut de page dans un ticket).
    /// </summary>
    public byte[] Ticket(Pharma.Application.Caisse.TicketVente t, decimal? rendu, int largeurMm = FormatTicket.LargeurParDefaut)
    {
        largeurMm = FormatTicket.Borner(largeurMm);
        var premier = new PdfDocumentRenderer { Document = DocumentTicket(t, rendu, largeurMm, 2000) };
        premier.PrepareRenderPages();
        var bas = premier.DocumentRenderer.GetRenderInfoFromPage(1)
            .Select(i => (i.LayoutInfo.ContentArea.Y + i.LayoutInfo.ContentArea.Height).Point).DefaultIfEmpty(0).Max();
        var hauteurMm = Math.Max(40, bas / 72 * 25.4 + MargeTicketMm(largeurMm) + 2);
        return Rendre(DocumentTicket(t, rendu, largeurMm, hauteurMm));
    }

    private static double MargeTicketMm(int largeurMm) => FormatTicket.Etroit(largeurMm) ? 2.5 : 4;

    /// <summary>
    /// Bon de sortie (ou d'entrée) de caisse au format rouleau (script 096) : motif, montant, employé, date, bénéficiaire, validation,
    /// ligne de signature ; hauteur selon le contenu, comme le ticket.
    /// </summary>
    public byte[] BonSortie(BonSortieCaisse b, int largeurMm = FormatTicket.LargeurParDefaut)
    {
        largeurMm = FormatTicket.Borner(largeurMm);
        var premier = new PdfDocumentRenderer { Document = DocumentBon(b, largeurMm, 2000) };
        premier.PrepareRenderPages();
        var bas = premier.DocumentRenderer.GetRenderInfoFromPage(1)
            .Select(i => (i.LayoutInfo.ContentArea.Y + i.LayoutInfo.ContentArea.Height).Point).DefaultIfEmpty(0).Max();
        return Rendre(DocumentBon(b, largeurMm, Math.Max(40, bas / 72 * 25.4 + MargeTicketMm(largeurMm) + 2)));
    }

    private static Document DocumentBon(BonSortieCaisse b, int largeurMm, double hauteurMm)
    {
        var sortie = b.Sens == RegleTiroir.Sortie;
        var titre = $"{(sortie ? "Bon de sortie de caisse" : "Bon d’entrée en caisse")} {b.Numero}";
        var marge = MargeTicketMm(largeurMm);
        var utile = (largeurMm - 2 * marge) / 10.0;
        var doc = new Document();
        doc.Info.Title = titre;
        doc.Info.Author = "Pharma Officine";
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = FormatTicket.Etroit(largeurMm) ? 7 : largeurMm >= 100 ? 9 : 8;
        var section = doc.AddSection();
        section.PageSetup.PageWidth = Unit.FromMillimeter(largeurMm);
        section.PageSetup.PageHeight = Unit.FromMillimeter(hauteurMm);
        section.PageSetup.TopMargin = section.PageSetup.BottomMargin = Unit.FromMillimeter(marge);
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromMillimeter(marge);
        section.PageSetup.HeaderDistance = section.PageSetup.FooterDistance = Unit.FromMillimeter(0);

        void Centre(string texte, bool gras = false)
        {
            var p = section.AddParagraph(texte);
            p.Format.Alignment = ParagraphAlignment.Center;
            p.Format.Font.Bold = gras;
        }
        void Separateur()
        {
            var p = section.AddParagraph();
            p.Format.Borders.Bottom.Width = 0.5;
            p.Format.SpaceAfter = Unit.FromPoint(3);
        }
        var tb = new Table();
        void Paire(string a, string v, bool gras = false)
        {
            var row = tb.AddRow();
            row.Format.Font.Bold = gras;
            row.Cells[0].AddParagraph(a);
            row.Cells[1].AddParagraph(v);
        }

        if (!string.IsNullOrWhiteSpace(b.Pharmacie)) Centre(b.Pharmacie, gras: true);
        Separateur();
        Centre(titre, gras: true);
        section.Elements.Add(tb);
        tb.AddColumn(Unit.FromCentimeter(utile * 0.3));
        tb.AddColumn(Unit.FromCentimeter(utile * 0.7));
        Paire("Date", Quand(b.Le));
        Paire("Employé", b.Poste is null ? b.Employe : $"{b.Employe} · {b.Poste}");
        Paire("Motif", b.Motif);
        if (b.Beneficiaire is { } benef) Paire(sortie ? "Bénéficiaire" : "Origine", benef);
        Paire("Montant", (sortie ? "− " : "+ ") + Da(b.Montant), gras: true);
        if (b.ValidePar is { } v) Paire("Validé par", v);
        foreach (var d in b.Details) { var p = section.AddParagraph(d); p.Format.SpaceBefore = Unit.FromPoint(2); }
        if (b.Correction is { } c) { var p = section.AddParagraph(c); p.Format.Font.Bold = true; p.Format.SpaceBefore = Unit.FromPoint(2); }
        Separateur();
        var signature = section.AddParagraph(sortie ? "Signature du bénéficiaire :" : "Signature :");
        signature.Format.SpaceBefore = Unit.FromPoint(6);
        signature.Format.SpaceAfter = Unit.FromPoint(22);
        var trait = section.AddParagraph();
        trait.Format.Borders.Bottom.Width = 0.5;
        var employe = section.AddParagraph("Signature de l’employé :");
        employe.Format.SpaceBefore = Unit.FromPoint(6);
        employe.Format.SpaceAfter = Unit.FromPoint(22);
        var trait2 = section.AddParagraph();
        trait2.Format.Borders.Bottom.Width = 0.5;
        return doc;
    }

    /// <summary>Même contenu que le ticket imprimé par le poste ; colonnes réparties sur la largeur utile du rouleau.</summary>
    private static Document DocumentTicket(Pharma.Application.Caisse.TicketVente t, decimal? rendu, int largeurMm, double hauteurMm)
    {
        var recu = t.Numero.StartsWith("RC-", StringComparison.Ordinal);
        var titre = $"{(recu ? "Reçu d’encaissement de dette" : "Ticket")} {t.Numero}";
        var etroit = FormatTicket.Etroit(largeurMm);
        var marge = MargeTicketMm(largeurMm);
        var utile = (largeurMm - 2 * marge) / 10.0;   // cm
        var doc = new Document();
        doc.Info.Title = titre;
        doc.Info.Author = "Pharma Officine";
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = etroit ? 7 : largeurMm >= 100 ? 9 : 8;
        var section = doc.AddSection();
        section.PageSetup.PageWidth = Unit.FromMillimeter(largeurMm);
        section.PageSetup.PageHeight = Unit.FromMillimeter(hauteurMm);
        section.PageSetup.TopMargin = section.PageSetup.BottomMargin = Unit.FromMillimeter(marge);
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromMillimeter(marge);
        section.PageSetup.HeaderDistance = section.PageSetup.FooterDistance = Unit.FromMillimeter(0);

        void Centre(string texte, bool gras = false, double? taille = null)
        {
            var p = section.AddParagraph(texte);
            p.Format.Alignment = ParagraphAlignment.Center;
            p.Format.Font.Bold = gras;
            if (taille is { } s) p.Format.Font.Size = s;
        }
        void Separateur()
        {
            var p = section.AddParagraph();
            p.Format.Borders.Bottom.Width = 0.5;
            p.Format.SpaceAfter = Unit.FromPoint(3);
        }
        Table Colonnes(params double[] parts)
        {
            var tb = section.AddTable();
            var somme = parts.Sum();
            foreach (var p in parts) tb.AddColumn(Unit.FromCentimeter(utile * p / somme));
            for (var i = 1; i < parts.Length; i++) tb.Columns[i].Format.Alignment = ParagraphAlignment.Right;
            return tb;
        }
        void Paire(Table tb, string a, string b, bool gras = false)
        {
            var row = tb.AddRow();
            row.Format.Font.Bold = gras;
            row.Cells[0].AddParagraph(a);
            row.Cells[1].AddParagraph(b);
        }

        if (!string.IsNullOrWhiteSpace(t.Pharmacie)) Centre(t.Pharmacie, gras: true, taille: normal.Font.Size + 1);
        if (!string.IsNullOrWhiteSpace(t.Adresse)) Centre(t.Adresse);
        if (!string.IsNullOrWhiteSpace(t.Telephone)) Centre($"Tél. {t.Telephone}");
        if (!string.IsNullOrWhiteSpace(t.Nif)) Centre($"NIF {t.Nif}");
        Separateur();
        Centre(titre, gras: true);
        // Libellés courts (Date, Vendeur) : la valeur prend les trois quarts, pour ne pas passer à la ligne en 57-58 mm (2026-11-15)
        var entete = Colonnes(1, 3);
        Paire(entete, "Date", Quand(t.Date));
        Paire(entete, "Vendeur", t.Poste is null ? t.Vendeur : $"{t.Vendeur} · {t.Poste}");
        if (t.Statut != "validee") Paire(entete, "État", t.Statut == "annulee" ? "Vente annulée" : t.Statut, gras: true);
        if (t.Client is { } client) Paire(entete, "Client", client);
        if (t.Organisme is { } org) Paire(entete, "Tiers payant", t.TauxTiersPayant is { } taux ? $"{org} · {taux:0.##} %" : org);
        Separateur();

        // Une ligne de désignation, puis quantité × prix et montant (lisible même en 57 mm)
        var lignes = Colonnes(3, 2);
        foreach (var l in t.Lignes)
        {
            var nom = lignes.AddRow();
            nom.Cells[0].MergeRight = 1;
            nom.Cells[0].AddParagraph(l.Reduction > 0 ? $"{l.Designation} ({(l.DonPartiel ? "don" : "remise")} −{l.PctReduction:0.##} %)" : l.Designation);
            var montant = lignes.AddRow();
            montant.BottomPadding = Unit.FromPoint(2);
            montant.Cells[0].AddParagraph($"  {l.Quantite.ToString(Fr)} × {Da(l.PrixUnitaire)}");
            montant.Cells[1].AddParagraph(Da(l.Montant));
        }
        Separateur();

        var totaux = Colonnes(3, 2);
        Paire(totaux, "Total", Da(t.MontantBrut));
        if (t.Remise > 0) Paire(totaux, "Remises", "− " + Da(t.Remise));
        if (t.DonPartiel > 0) Paire(totaux, "Don partiel", "− " + Da(t.DonPartiel));
        if (t.PartOrganisme > 0) Paire(totaux, $"Part {t.Organisme ?? "organisme"}", Da(t.PartOrganisme));
        if (t.EcartChifa > 0) Paire(totaux, "Écart Chifa payé par le patient", Da(t.EcartChifa));
        // Majoration Chifa (D-3) : ajoutée à la part de l'organisme, jamais payée par le patient
        if (t.MajorationChifa > 0) Paire(totaux, $"Majoration Chifa ({t.Organisme ?? "organisme"})", Da(t.MajorationChifa));
        Paire(totaux, recu ? "Montant réglé" : "À payer par le patient", Da(t.PartPatient), gras: true);
        // Part payée par l'avoir du patient (script 106)
        if (t.PayeParAvoir > 0) Paire(totaux, "Payé par avoir", Da(t.PayeParAvoir));
        // D9 (2026-11-15) : « Règlement Espèces » suivi du montant payé dans ce mode (l'avoir est sur la ligne au-dessus)
        if (t.Mode is { } mode && (t.PayeParAvoir <= 0 || t.PayeParAvoir < t.PartPatient)) Paire(totaux, "Règlement " + mode switch
        {
            "especes" => "Espèces", "carte" => "Carte", "cheque" => "Chèque", "credit" => "Crédit", "virement" => "Virement", "avoir" => "Avoir du patient", _ => mode
        }, Da(t.PartPatient - Math.Max(t.PayeParAvoir, 0)));
        if (rendu is > 0) Paire(totaux, "Rendu", Da(rendu.Value), gras: true);
        foreach (var retour in t.Retours ?? []) Paire(totaux, "Retour", retour);

        var note = section.AddParagraph("Merci de votre visite.");
        note.Format.SpaceBefore = Unit.FromPoint(6);
        note.Format.Alignment = ParagraphAlignment.Center;
        return doc;
    }

    public byte[] ReleveCredit(Pharma.Application.Caisse.SituationCredit s, string? pharmacie,
        IReadOnlyList<Pharma.Application.Caisse.MouvementAvoir>? avoir = null, decimal soldeAvoir = 0)
    {
        var doc = NouveauDocument("Relevé de crédit", pharmacie);
        var section = doc.LastSection;
        Titre(section, $"Relevé de crédit · {s.Patient.Nom}");

        var synthese = Tableau(section, 11.5, 5);
        Ligne(synthese, $"Reste dû au {DateTime.Now.ToString("dd/MM/yyyy", Fr)}", Da(s.Patient.Solde), gras: true);
        if (s.DetteReprise > 0) Ligne(synthese, "Dette d’avant les ventes à crédit du logiciel, à l’origine (soldée en premier)", Da(s.DetteReprise));
        if (s.Patient.Telephone is { } tel) Ligne(synthese, "Téléphone", tel);

        SousTitre(section, "Ventes à crédit");
        var ventes = Tableau(section, 4, 4, 3, 3, 2.5);
        Entete(ventes, "Ticket", "Date", "Montant", "Réglé", "Reste dû");
        foreach (var v in s.Ventes.OrderBy(v => v.Date))
        {
            var row = ventes.AddRow();
            row.TopPadding = row.BottomPadding = Unit.FromPoint(3);
            string[] cellules = [v.Remise is { } rv ? $"{v.Numero} · {rv}" : v.Numero, Quand(v.Date), Da(v.Montant), Da(v.Regle), v.Reste > 0 ? Da(v.Reste) : "soldée"];
            for (var i = 0; i < cellules.Length; i++) row.Cells[i].AddParagraph(cellules[i]);
        }
        if (s.Ventes.Count == 0) Ligne(ventes, "Aucune vente à crédit", "");

        SousTitre(section, "Mouvements de la dette");
        var mouvements = Tableau(section, 3, 6.5, 3.5, 3.5);
        Entete(mouvements, "Date", "Mouvement", "Montant", "Dette après");
        foreach (var m in (s.Mouvements ?? []).OrderBy(m => m.Date))
        {
            var row = mouvements.AddRow();
            row.TopPadding = row.BottomPadding = Unit.FromPoint(3);
            // Régularisation Chifa (recette du 2026-11-15, D7) : « Régularisation Chifa (T-…) », ni reprise ni correction du titulaire
            var regularisation = Pharma.Domain.Ventes.RegularisationPartOrganisme.LibelleReleve(m.Reference, m.Vente);
            var libelle = regularisation ?? m.Type switch
            {
                "ouverture" => "Dette à la mise en place du relevé", "reprise" => "Dette reprise de l’ancien logiciel", "vente_credit" => "Vente à crédit",
                "remboursement" => "Encaissement de dette", "retour" => "Retour de produits", "annulation" => "Vente annulée",
                _ => m.Employe is null ? "Correction hors logiciel" : "Correction par le titulaire"
            };
            string[] cellules = [Quand(m.Date), m.Vente is null || regularisation is not null ? libelle : $"{libelle} · {m.Vente}" + (m.Remise is { } rm ? $" · {rm}" : ""), (m.Montant > 0 ? "+ " : "- ") + Da(Math.Abs(m.Montant)), Da(m.SoldeApres)];
            for (var i = 0; i < cellules.Length; i++) row.Cells[i].AddParagraph(cellules[i]);
        }

        // Avoir du patient (script 106) : argent que la pharmacie lui doit, toujours séparé de sa dette (jamais déduit du reste dû)
        if (soldeAvoir > 0 || avoir is { Count: > 0 })
        {
            SousTitre(section, $"Avoir du patient (à sa disposition) : {Da(Math.Max(soldeAvoir, 0))}");
            var lignesAvoir = Tableau(section, 3, 6.5, 3.5, 3.5);
            Entete(lignesAvoir, "Date", "Mouvement", "Montant", "Avoir après");
            foreach (var m in (avoir ?? []).OrderBy(m => m.CreeLe).ThenBy(m => m.Id))
            {
                var row = lignesAvoir.AddRow();
                row.TopPadding = row.BottomPadding = Unit.FromPoint(3);
                var libelle = Pharma.Domain.Ventes.RegleAvoir.LibelleOrigine(m.Origine) + (m.NumeroVente is { } nv ? $" · {nv}" : "")
                              + (m.BonSortie is { } b ? $" · bon {b}" : "");
                string[] cellules = [Quand(m.CreeLe), libelle, (m.Sens == Pharma.Domain.Ventes.RegleAvoir.Credit ? "+ " : "- ") + Da(m.Montant), Da(m.SoldeApres)];
                for (var i = 0; i < cellules.Length; i++) row.Cells[i].AddParagraph(cellules[i]);
            }
        }

        var note = section.AddParagraph("Un règlement solde les ventes choisies par le patient, sinon les dettes les plus anciennes d’abord ; "
            + "un avoir ou le règlement d’une facture s’impute sur les ventes de cette facture.");
        note.Format.SpaceBefore = Unit.FromPoint(12);
        note.Format.Font.Size = 8;
        return Rendre(doc);
    }

    // ---- Registre des psychotropes -----------------------------------------

    public byte[] RegistrePsychotropes(Pharma.Application.Stock.Registre r, string? pharmacie)
    {
        var doc = NouveauDocument("Registre des psychotropes et stupéfiants", pharmacie);
        var section = doc.LastSection;
        // Paysage : le registre a beaucoup de colonnes
        section.PageSetup.Orientation = Orientation.Landscape;
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(1.2);
        section.PageSetup.TopMargin = section.PageSetup.BottomMargin = Unit.FromCentimeter(1.5);
        doc.Styles[StyleNames.Normal]!.Font.Size = 7.5;
        Titre(section, "Registre des psychotropes et stupéfiants");
        var info = section.AddParagraph($"Du {r.Du.ToString("dd/MM/yyyy", Fr)} au {r.Au.ToString("dd/MM/yyyy", Fr)} · {r.Produit ?? "tous les produits"}"
            + (r.NomsMasques ? " · noms des patients et porteurs abrégés (édition hors titulaire)" : ""));
        info.Format.SpaceAfter = Unit.FromPoint(6);

        double[] largeurs = [1.1, 2.0, 3.6, 1.6, 2.6, 1.1, 1.1, 1.3, 1.2, 3.0, 2.4, 1.6, 2.4, 2.3];
        var t = section.AddTable();
        t.Borders.Color = Color.FromRgb(0xE4, 0xE7, 0xEC);
        t.Borders.Width = 0.5;
        foreach (var l in largeurs) t.AddColumn(Unit.FromCentimeter(l));
        foreach (var i in new[] { 0, 5, 6, 7, 8 }) t.Columns[i].Format.Alignment = ParagraphAlignment.Right;
        var entete = t.AddRow();
        entete.HeadingFormat = true;
        entete.Shading.Color = Color.FromRgb(0xF9, 0xFA, 0xFB);
        entete.Format.Font.Bold = true;
        string[] titres = ["N°", "Date", "Produit", "Lot", "Opération", "Entrée", "Sortie", "Solde produit", "Solde lot",
                           "Patient / porteur", "Médecin · ordonnance", "Référence", "Grossiste", "Employé"];
        for (var i = 0; i < titres.Length; i++) entete.Cells[i].AddParagraph(titres[i]);

        foreach (var l in r.Lignes)
        {
            var row = t.AddRow();
            row.TopPadding = row.BottomPadding = Unit.FromPoint(2);
            var patient = l.Patient ?? "";
            if (l.PorteurNom is not null) patient += $"\nPorteur : {l.PorteurNom}, {l.PorteurAdresse}, pièce {l.PorteurPiece}";
            string[] cellules =
            [
                l.Numero.ToString(Fr), l.Date.ToLocalTime().ToString("dd/MM/yy HH:mm", Fr), l.Produit, l.NumeroLot,
                Pharma.Application.Stock.RegistreCsv.Libelle(l.Type), l.Entree?.ToString(Fr) ?? "", l.Sortie?.ToString(Fr) ?? "",
                l.SoldeProduit?.ToString(Fr) ?? "—", l.SoldeLot.ToString(Fr), patient.Trim(),
                string.Join(" · ", new[] { l.Medecin, l.Ordonnance }.Where(x => !string.IsNullOrEmpty(x))),
                l.Reference ?? "", l.Fournisseur ?? "", l.Employe
            ];
            for (var i = 0; i < cellules.Length; i++) row.Cells[i].AddParagraph(cellules[i]);
        }
        if (r.Lignes.Count == 0) section.AddParagraph("Aucune inscription sur la période.");

        SousTitre(section, "Balance mensuelle des entrées et sorties");
        var b = Tableau(section, 8, 3, 3, 3, 3, 4);
        Entete(b, "Produit", "Mois", "Solde reporté", "Entrées", "Sorties", "Solde fin de mois");
        foreach (var m in r.Balance)
        {
            var row = b.AddRow();
            row.TopPadding = row.BottomPadding = Unit.FromPoint(2);
            string[] c = [m.Produit, $"{m.Mois:00}/{m.Annee}", m.SoldeReporte == 0 ? "—" : m.SoldeReporte.ToString(Fr),
                m.Entrees.ToString(Fr), m.Sorties.ToString(Fr), m.SoldeFin?.ToString(Fr) ?? "—"];
            for (var i = 0; i < c.Length; i++) row.Cells[i].AddParagraph(c[i]);
        }

        // Cachet et griffe : dernière ligne (sans bordure) du tableau de la balance, gardée avec la ligne qui la précède :
        // elle ne part plus seule sur une page presque vide (recette 2026-11-15)
        if (b.Rows.Count > 0) b.Rows[b.Rows.Count - 1].KeepWith = 1;
        var cachet = b.AddRow();
        cachet.Borders.Visible = false;
        cachet.TopPadding = Unit.FromCentimeter(0.8);
        cachet.Cells[0].MergeRight = 5;
        cachet.Cells[0].Format.Alignment = ParagraphAlignment.Left;
        cachet.Cells[0].AddParagraph("Cachet de l’officine et griffe du pharmacien (n° d’inscription à l’Ordre) : ________________________________");
        return Rendre(doc);
    }

    // ---- État d'inventaire (inventaire complet) ----------------------------

    public byte[] EtatInventaire(Pharma.Application.Stock.EtatInventaire e, string? pharmacie)
    {
        var c = e.Campagne;
        var doc = NouveauDocument("État d’inventaire", pharmacie);
        var section = doc.LastSection;
        doc.Styles[StyleNames.Normal]!.Font.Size = 9;
        Titre(section, $"État d’inventaire au {c.ClotureLe?.ToLocalTime().ToString("dd/MM/yyyy", Fr)}");
        section.AddParagraph($"Commencé le {c.CommenceLe.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Fr)} par {c.CommencePar} · "
            + $"clôturé le {c.ClotureLe?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Fr)} par {c.CloturePar} · "
            + (c.Mode == "fermee" ? "pharmacie fermée pendant le comptage" : "pharmacie ouverte pendant le comptage")).Format.SpaceAfter = Unit.FromPoint(6);

        var synthese = Tableau(section, 11.5, 5.5);
        Ligne(synthese, "Lots en stock", (c.NombreLots ?? 0).ToString(Fr));
        Ligne(synthese, "Boîtes en stock", (c.NombreBoites ?? 0).ToString("N0", Fr));
        Ligne(synthese, "Valeur du stock au prix d’achat HT", Da(c.ValeurStock ?? 0), gras: true);
        Ligne(synthese, "Écarts corrigés pendant l’inventaire", Accord.Texte($"{e.Ecarts} ligne(s) · {Da(e.ValeurEcarts)}"));

        foreach (var rayon in e.Lignes.GroupBy(l => l.Rayon))
        {
            SousTitre(section, $"Rayon {rayon.Key}");
            var t = Tableau(section, 6.3, 2.6, 2, 1.5, 2.3, 2.3);
            Entete(t, "Produit", "Lot", "Péremption", "Qté", "P.A. HT", "Valeur HT");
            t.Columns[1].Format.Alignment = ParagraphAlignment.Left;
            foreach (var l in rayon)
            {
                var row = t.AddRow();
                row.TopPadding = row.BottomPadding = Unit.FromPoint(1.5);
                string[] v = [l.Produit, l.NumeroLot, l.DatePeremption.ToString("MM/yyyy", Fr), l.Quantite.ToString(Fr), l.PrixAchatHt.ToString("N2", Fr), l.Valeur.ToString("N2", Fr)];
                for (var i = 0; i < v.Length; i++) row.Cells[i].AddParagraph(v[i]);
            }
            var total = t.AddRow();
            total.Format.Font.Bold = true;
            total.Cells[0].AddParagraph($"Total rayon {rayon.Key}");
            total.Cells[3].AddParagraph(rayon.Sum(l => l.Quantite).ToString(Fr));
            total.Cells[5].AddParagraph(rayon.Sum(l => l.Valeur).ToString("N2", Fr));
        }
        if (c.Commentaire is { } com) section.AddParagraph(com).Format.SpaceBefore = Unit.FromPoint(8);
        var signature = section.AddParagraph($"Arrêté le présent état d’inventaire à la somme de : {Pharma.Domain.Factures.MontantEnLettres.Dinars(c.ValeurStock ?? 0)} hors taxes.");
        signature.Format.SpaceBefore = Unit.FromPoint(10);
        Signatures(section);
        return Rendre(doc);
    }

    // ---- Facture client ----------------------------------------------------

    public byte[] Facture(Pharma.Application.Factures.FactureDetail f, Pharma.Application.Administration.IdentitePharmacie ph)
    {
        var r = f.Resume;
        var avoir = r.Type == "avoir";
        var doc = NouveauDocument($"{(avoir ? "Avoir" : "Facture")} {r.Numero}", ph.Nom);
        var section = doc.LastSection;

        // Émetteur (gauche) et destinataire (droite)
        var haut = section.AddTable();
        haut.AddColumn(Unit.FromCentimeter(8.5));
        haut.AddColumn(Unit.FromCentimeter(8.5));
        var ligne = haut.AddRow();
        var emetteur = ligne.Cells[0].AddParagraph();
        emetteur.AddFormattedText(ph.Nom ?? "Pharmacie", TextFormat.Bold);
        foreach (var x in new[] { ph.Titulaire, ph.Adresse, ph.Telephone is null ? null : "Tél. " + ph.Telephone,
                                  ph.Rc is null ? null : "RC : " + ph.Rc, ph.Nif is null ? null : "NIF : " + ph.Nif, ph.Nis is null ? null : "NIS : " + ph.Nis,
                                  ph.ArticleImposition is null ? null : "AI : " + ph.ArticleImposition })
            if (x is not null) { emetteur.AddLineBreak(); emetteur.AddText(x); }
        var dest = ligne.Cells[1].AddParagraph();
        dest.Format.LeftIndent = Unit.FromCentimeter(1);
        dest.AddFormattedText(r.Destinataire switch { "organisme" => "Organisme", "entreprise" => "Client", _ => "Patient" }, TextFormat.Italic);
        dest.AddLineBreak();
        dest.AddFormattedText(r.DestinataireNom, TextFormat.Bold);
        foreach (var x in (f.DestinataireInfos ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)) { dest.AddLineBreak(); dest.AddText(x); }

        Titre(section, $"{(avoir ? "AVOIR" : "FACTURE")} N° {r.Numero}{(r.Version > 1 ? $" (version {r.Version})" : "")}");
        var info = section.AddParagraph($"Date d’établissement : {r.Date.ToString("dd/MM/yyyy", Fr)}"
            + (r.Echeance is { } echeance ? $" · date d’échéance : {echeance.ToString("dd/MM/yyyy", Fr)}" : "")
            + (f.Du is { } du && f.Au is { } au ? $" · période du {du.ToString("dd/MM/yyyy", Fr)} au {au.ToString("dd/MM/yyyy", Fr)}" : "")
            + (r.OrigineNumero is { } o ? $" · sur la facture {o}" : "")
            + (r.RemplaceNumero is { } rem ? $" · remplace la facture {rem}" : ""));
        info.Format.SpaceAfter = Unit.FromPoint(8);
        if (r.Statut != "emise")
        {
            var etat = section.AddParagraph(r.Statut == "rejetee" ? $"Facture rejetée{(r.MotifRejet is { } m ? " : " + m : "")}"
                : $"Facture remplacée par {r.RemplaceeParNumero} : non comptabilisée");
            etat.Format.Font.Bold = true;
            etat.Format.Font.Color = Color.FromRgb(0xB4, 0x23, 0x18);
            etat.Format.SpaceAfter = Unit.FromPoint(6);
        }

        var t = Tableau(section, 8.4, 1.6, 2.5, 1.5, 3);
        t.Columns[1].Format.Alignment = ParagraphAlignment.Right;
        Entete(t, "Désignation", "Qté", "P.U. TTC", "TVA", "Montant TTC");
        foreach (var l in f.Lignes)
        {
            var row = t.AddRow();
            row.TopPadding = row.BottomPadding = Unit.FromPoint(2);
            string[] c = [l.Designation, l.Quantite.ToString("0.##", Fr), l.PrixUnitaireTtc.ToString("N2", Fr), l.TauxTva.ToString("0.##", Fr) + " %", l.MontantTtc.ToString("N2", Fr)];
            for (var i = 0; i < c.Length; i++) row.Cells[i].AddParagraph(c[i]);
        }

        var tot = section.AddTable();
        tot.AddColumn(Unit.FromCentimeter(11));
        tot.AddColumn(Unit.FromCentimeter(6));
        tot.Columns[1].Format.Alignment = ParagraphAlignment.Right;
        void Total(string a, string b, bool gras = false)
        {
            var row = tot.AddRow();
            row.Format.Font.Bold = gras;
            row.Cells[0].AddParagraph(a).Format.Alignment = ParagraphAlignment.Right;
            row.Cells[1].AddParagraph(b);
        }
        tot.AddRow();
        Total("Total HT", Da(f.Ht));
        Total("TVA", Da(f.Tva));
        Total(avoir ? "Montant de l’avoir TTC" : "Total TTC", Da(r.Ttc), gras: true);
        if (!avoir && (r.DejaPaye > 0 || f.RegleParDette > 0 || r.Regle > 0 || r.Avoirs > 0))
        {
            // Ce qui a déjà été payé, puis le reste à payer (même calcul que l'écran des factures)
            if (r.DejaPaye > 0) Total("Déjà payé au comptoir", Da(r.DejaPaye));
            if (f.RegleParDette > 0) Total("Déjà réglé sur la dette du patient", Da(f.RegleParDette));
            if (r.Regle > 0) Total("Règlements reçus", Da(r.Regle));
            if (r.Avoirs > 0) Total("Avoirs", "− " + Da(r.Avoirs));
            Total("Reste à payer", Da(r.Reste), gras: true);
        }

        var lettres = section.AddParagraph($"Arrêté{(avoir ? " le présent avoir" : "e la présente facture")} à la somme de : "
            + Pharma.Domain.Factures.MontantEnLettres.Dinars(r.Ttc) + " toutes taxes comprises.");
        lettres.Format.SpaceBefore = Unit.FromPoint(10);
        if (f.Commentaire is { } com) section.AddParagraph(com).Format.SpaceBefore = Unit.FromPoint(6);
        var signature = section.AddParagraph("Cachet et signature du pharmacien");
        signature.Format.SpaceBefore = Unit.FromCentimeter(1.5);
        signature.Format.Alignment = ParagraphAlignment.Right;
        return Rendre(doc);
    }

    // ---- Mise en page commune --------------------------------------------

    private static Document NouveauDocument(string titre, string? pharmacie)
    {
        var doc = new Document();
        doc.Info.Title = titre;
        doc.Info.Author = "Pharma Officine";
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = 10;
        var section = doc.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = section.PageSetup.BottomMargin = Unit.FromCentimeter(2);
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(2);
        var pied = section.Footers.Primary.AddParagraph($"{(pharmacie is null ? "" : pharmacie + " · ")}Pharma Officine · édité le {DateTime.Now.ToString("dd/MM/yyyy HH:mm", Fr)}");
        pied.Format.Font.Size = 8;
        pied.Format.Font.Color = Color.FromRgb(0x47, 0x54, 0x67);
        return doc;
    }

    private static void Titre(Section s, string texte) => LogoPdf.Titre(s, texte);

    private static void SousTitre(Section s, string texte)
    {
        var p = s.AddParagraph(texte);
        p.Format.Font.Size = 12;
        p.Format.Font.Bold = true;
        p.Format.SpaceBefore = Unit.FromPoint(12);
        p.Format.SpaceAfter = Unit.FromPoint(6);
        p.Format.KeepWithNext = true;   // jamais seul en bas de page, loin de son tableau (recette 2026-11-15)
    }

    private static Table Tableau(Section s, params double[] largeursCm)
    {
        var t = s.AddTable();
        t.Borders.Color = Color.FromRgb(0xE4, 0xE7, 0xEC);
        t.Borders.Width = 0.5;
        foreach (var l in largeursCm) t.AddColumn(Unit.FromCentimeter(l));
        for (var i = 1; i < largeursCm.Length; i++) t.Columns[i].Format.Alignment = ParagraphAlignment.Right;
        return t;
    }

    private static void Entete(Table t, params string[] textes)
    {
        var row = t.AddRow();
        // En-tête répété en haut de chaque page quand le tableau continue sur la suivante (recette 2026-11-15)
        row.HeadingFormat = true;
        row.Shading.Color = Color.FromRgb(0xF9, 0xFA, 0xFB);
        row.Format.Font.Bold = true;
        for (var i = 0; i < textes.Length; i++) row.Cells[i].AddParagraph(textes[i]);
    }

    private static void Ligne(Table t, string a, string b, string? c = null, bool gras = false)
    {
        var row = t.AddRow();
        row.TopPadding = row.BottomPadding = Unit.FromPoint(3);
        row.Format.Font.Bold = gras;
        row.Cells[0].AddParagraph(a);
        row.Cells[1].AddParagraph(b);
        if (c is not null) row.Cells[2].AddParagraph(c);
    }

    private static void Signatures(Section s)
    {
        var p = s.AddParagraph("Signature de l’employé : ____________________        Signature du titulaire : ____________________");
        p.Format.SpaceBefore = Unit.FromCentimeter(2);
    }

    private static string Da(decimal montant) => montant.ToString("N2", Fr) + " DA";

    private static byte[] Rendre(Document doc)
    {
        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var flux = new MemoryStream();
        renderer.PdfDocument.Save(flux, false);
        return flux.ToArray();
    }
}
