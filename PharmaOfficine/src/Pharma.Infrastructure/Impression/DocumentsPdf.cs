using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using Pharma.Application.Caisse;
using Pharma.Domain.Caisse;

namespace Pharma.Infrastructure.Impression;

/// <summary>Documents PDF produits avec PDFsharp-MigraDoc (licence MIT), polices Windows du serveur.</summary>
public sealed class DocumentsPdf : IDocumentsPdf, Pharma.Application.Rapports.IDocumentsRapports, Pharma.Application.Stock.IDocumentsRegistre,
    Pharma.Application.Factures.IDocumentsFactures, Pharma.Application.Stock.IDocumentsInventaire
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    static DocumentsPdf()
    {
        if (OperatingSystem.IsWindows())
            GlobalFontSettings.UseWindowsFontsUnderWindows = true;
    }

    public byte[] RapportCloture(DetailSession s, bool avecEcart, decimal seuilEcart, string? pharmacie)
    {
        var doc = NouveauDocument("Rapport de clôture de caisse", pharmacie);
        var section = doc.LastSection;

        Titre(section, "Rapport de clôture de caisse");
        var infos = section.AddParagraph();
        infos.Format.SpaceAfter = Unit.FromPoint(12);
        infos.AddText($"Caisse ouverte par : {s.Employe}   ·   {s.Poste}\n");
        // Caisse partagée : tous ceux qui ont vendu pendant la session
        if (s.Vendeurs is { Count: > 0 } vendeurs)
            infos.AddText($"Vendeurs : {string.Join(", ", vendeurs)}\n");
        infos.AddText($"Ouverture : {s.OuverteLe.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Fr)}   ·   ");
        infos.AddText($"Clôture : {s.FermeeLe?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Fr) ?? "—"}\n");
        infos.AddText($"Ventes de la session : {s.NombreVentes}, pour {Da(s.TotalVentes)} (tous modes de paiement)");

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
            Ligne(resultat, "Ventes en espèces", Da(s.VentesEspeces ?? 0));
            Ligne(resultat, "Attendu", Da(s.Attendu ?? 0), gras: true);
            Ligne(resultat, "Compté", Da(s.Compte ?? 0));
            Ligne(resultat, "Écart", Da(s.Ecart ?? 0), gras: true);
            if (s.Ecart is { } e && Math.Abs(e) > seuilEcart)
            {
                var p = section.AddParagraph($"Écart supérieur au seuil de {Da(seuilEcart)} : inscrit au journal des actions sensibles.");
                p.Format.SpaceBefore = Unit.FromPoint(8);
                p.Format.Font.Color = Color.FromRgb(0x91, 0x20, 0x18);
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
        Ligne(synthese, "Chiffre d’affaires TTC (remises déduites)", Da(r.Synthese.ChiffreAffaires), gras: true);
        Ligne(synthese, "Nombre de ventes", r.Synthese.NombreVentes.ToString(Fr));
        Ligne(synthese, "Part organismes (Chifa) à recevoir", Da(r.Synthese.PartOrganismes));
        Ligne(synthese, "Crédits clients accordés", Da(r.Synthese.CreditsAccordes));
        Ligne(synthese, "Remises accordées", Da(r.Synthese.Remises));
        Ligne(synthese, "Ventes annulées", r.Synthese.Annulations.ToString(Fr));
        if (r.Marge is { } m)
        {
            Ligne(synthese, "Chiffre d’affaires HT", Da(m.ChiffreAffairesHt));
            Ligne(synthese, "Coût d’achat HT des boîtes vendues", Da(m.CoutAchat));
            Ligne(synthese, "Marge brute", $"{Da(m.Montant)}{(m.Taux is { } t ? $" ({t.ToString("0.0", Fr)} %)" : "")}", gras: true);
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

        var note = section.AddParagraph("Ventes validées seulement. Marge brute : chiffre d’affaires HT moins le coût d’achat HT, au prix réel de chaque boîte identifiée, sinon au prix de son lot.");
        note.Format.SpaceBefore = Unit.FromPoint(12);
        note.Format.Font.Size = 8;
        note.Format.Font.Color = Color.FromRgb(0x47, 0x54, 0x67);
        return Rendre(doc);
    }

    private static string Quand(DateTime? d) => d?.ToLocalTime().ToString("dd/MM HH:mm", Fr) ?? "—";

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
        var b = Tableau(section, 8, 3, 3, 3, 4);
        Entete(b, "Produit", "Mois", "Entrées", "Sorties", "Solde fin de mois");
        foreach (var m in r.Balance)
        {
            var row = b.AddRow();
            row.TopPadding = row.BottomPadding = Unit.FromPoint(2);
            string[] c = [m.Produit, $"{m.Mois:00}/{m.Annee}", m.Entrees.ToString(Fr), m.Sorties.ToString(Fr), m.SoldeFin?.ToString(Fr) ?? "—"];
            for (var i = 0; i < c.Length; i++) row.Cells[i].AddParagraph(c[i]);
        }

        var signature = section.AddParagraph("Cachet de l’officine et griffe du pharmacien (n° d’inscription à l’Ordre) : ________________________________");
        signature.Format.SpaceBefore = Unit.FromCentimeter(1);
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
        Ligne(synthese, "Écarts corrigés pendant l’inventaire", $"{e.Ecarts} ligne(s) · {Da(e.ValeurEcarts)}");

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
        var info = section.AddParagraph($"Date : {r.Date.ToString("dd/MM/yyyy", Fr)}"
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
        if (r.DejaPaye > 0) { Total("Déjà payé au comptoir", Da(r.DejaPaye)); Total("Net à payer", Da(Math.Max(r.Ttc - r.DejaPaye, 0)), gras: true); }

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
