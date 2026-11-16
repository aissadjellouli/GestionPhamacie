using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using Pharma.Application.Pilotage;

namespace Pharma.Infrastructure.Impression;

/// <summary>Tableau de bord financier du titulaire (lot 11) en PDF : chiffres lus, aucun recalcul fiscal.</summary>
public sealed partial class DocumentsPdf : IDocumentsPilotageFinancier
{
    public byte[] TableauFinancier(TableauFinancier t, IReadOnlyList<MargeRegroupee> marges, IReadOnlyList<ProduitDormant> dormants, string? pharmacie)
    {
        const string titre = "Tableau de bord financier";
        var doc = NouveauDocument(titre, pharmacie);
        var section = doc.LastSection;
        Titre(section, titre);
        var infos = section.AddParagraph($"Période du {t.Actuel.Du.ToString("dd/MM/yyyy", Fr)} au {t.Actuel.Au.ToString("dd/MM/yyyy", Fr)}"
            + (t.Precedent is { } p ? $" · comparée au {p.Du.ToString("dd/MM/yyyy", Fr)} – {p.Au.ToString("dd/MM/yyyy", Fr)}" : ""));
        infos.Format.SpaceAfter = Unit.FromPoint(8);

        static string Pct(decimal? v) => v is { } x ? $"{(x > 0 ? "+" : "")}{x.ToString("0.0", Fr)} %" : "—";

        SousTitre(section, "Ventes et marge");
        var ventes = Tableau(section, 6.5, 3.5, 3.5, 2.5);
        Entete(ventes, "Indicateur", "Période", "Précédente", "Écart");
        Lignes(ventes, "Chiffre d'affaires TTC", Da(t.ChiffreAffaires.Actuel), t.ChiffreAffaires.Precedent is { } a ? Da(a) : "—", Pct(t.ChiffreAffaires.VariationPct));
        Lignes(ventes, "Nombre de ventes", t.NombreVentes.Actuel.ToString("0", Fr), t.NombreVentes.Precedent?.ToString("0", Fr) ?? "—", Pct(t.NombreVentes.VariationPct));
        Lignes(ventes, "Panier moyen", t.PanierMoyen is { } pm ? Da(pm.Actuel) : "—", t.PanierMoyen?.Precedent is { } pp ? Da(pp) : "—", Pct(t.PanierMoyen?.VariationPct));
        Lignes(ventes, "Marge brute", Da(t.MargeBrute.Actuel), t.MargeBrute.Precedent is { } m ? Da(m) : "—", Pct(t.MargeBrute.VariationPct));
        Lignes(ventes, "Taux de marge (CA HT)", Pct2(t.Actuel.Marge.Taux), Pct2(t.Precedent?.Marge.Taux), "");

        SousTitre(section, "Répartition du chiffre");
        var rep = Tableau(section, 10, 4);
        Ligne(rep, "Comptant (espèces, carte, chèque, avoir)", Da(t.Repartition.Comptant));
        Ligne(rep, "Crédit patients", Da(t.Repartition.Credit));
        Ligne(rep, "Part Chifa (CNAS, CASNOS)", Da(t.Repartition.Chifa));
        Ligne(rep, "Part MDN", Da(t.Repartition.Mdn));
        if (t.Repartition.AutresOrganismes != 0) Ligne(rep, "Autres organismes", Da(t.Repartition.AutresOrganismes));

        SousTitre(section, "Créances à encaisser");
        var cr = Tableau(section, 10, 4);
        Ligne(cr, $"Bordereaux non réglés ({t.Creances.Bordereaux.Count})", Da(t.Creances.TotalBordereaux), gras: true);
        foreach (var x in t.Creances.BordereauxParAge) Ligne(cr, $"   {x.Tranche} ({x.Nombre})", Da(x.Montant));
        Ligne(cr, "Part organismes pas encore sur un bordereau", Da(t.Creances.HorsBordereau));
        Ligne(cr, $"Crédits patients ({t.Creances.PatientsDebiteurs} patient(s))", Da(t.Creances.CreditsPatients), gras: true);
        foreach (var x in t.Creances.CreditsParAge) Ligne(cr, $"   ventes à crédit {x.Tranche} ({x.Nombre})", Da(x.Montant));

        SousTitre(section, "Stock");
        var st = Tableau(section, 10, 4);
        Ligne(st, "Valeur au prix d'achat HT", Da(t.Stock.ValeurAchat));
        Ligne(st, "Rotation annuelle (coût des ventes / stock)", t.Stock.RotationAnnuelle?.ToString("0.0", Fr) ?? "—");
        Ligne(st, "Couverture", t.Stock.CouvertureJours is { } cj ? $"{cj.ToString("0", Fr)} jours" : "—");
        Ligne(st, $"Stock dormant (sans vente depuis {t.Stock.SeuilDormantJours} jours) : {t.Stock.Dormants} produit(s)", Da(t.Stock.ValeurDormante));
        Ligne(st, "Ruptures à l'officine / chez un grossiste", $"{t.Stock.RupturesOfficine} / {t.Stock.RupturesGrossistes}");

        if (t.Achats.Count > 0)
        {
            SousTitre(section, "Achats par grossiste");
            var ac = Tableau(section, 7, 2.5, 3.5, 3.5);
            Entete(ac, "Grossiste", "Réceptions", "HT", "TTC");
            foreach (var x in t.Achats) Lignes(ac, x.Grossiste, x.Receptions.ToString(Fr), Da(x.MontantHt), Da(x.MontantTtc));
        }

        SousTitre(section, "Rejets Chifa et MDN (bordereaux déposés sur la période)");
        var rj = Tableau(section, 7, 2.5, 3.5, 3.5);
        Entete(rj, "Motif", "Rejets", "Montant", "Taux");
        foreach (var x in t.Rejets.ParMotif) Lignes(rj, x.Libelle, x.Nombre.ToString(Fr), Da(x.Montant), Pct2(t.Rejets.TauxMotif(x)));
        Lignes(rj, $"Total sur {t.Rejets.VentesDeposees} vente(s) déposée(s)", t.Rejets.Rejets.ToString(Fr), Da(t.Rejets.MontantRejete), Pct2(t.Rejets.TauxRejet));

        SousTitre(section, "TVA par taux (journal des ventes)");
        var tv = Tableau(section, 3, 3.5, 3.5, 3.5, 3);
        Entete(tv, "Taux", "TTC", "HT", "TVA", "Événements");
        foreach (var x in t.Tva) Lignes(tv, $"{x.Taux.ToString("0.##", Fr)} %", Da(x.Ttc), Da(x.Ht), Da(x.Tva), x.Evenements.ToString(Fr));
        if (t.Tva.Count == 0) section.AddParagraph("Aucune ligne au journal des ventes sur la période.");

        if (marges.Count > 0)
        {
            SousTitre(section, $"Marge par produit ({marges.Count} premiers)");
            var mg = Tableau(section, 6.5, 1.8, 3, 3, 2.2);
            Entete(mg, "Produit", "Boîtes", "CA HT", "Marge", "Taux");
            foreach (var x in marges) Lignes(mg, x.Cle, x.Quantite.ToString(Fr), Da(x.ChiffreAffairesHt), Da(x.Marge), Pct2(x.Taux));
        }
        if (dormants.Count > 0)
        {
            SousTitre(section, $"Stock dormant ({dormants.Count} premiers, par valeur)");
            var dm = Tableau(section, 7, 1.8, 3.2, 2.5, 2);
            Entete(dm, "Produit", "Stock", "Valeur HT", "Dernière vente", "Jours");
            foreach (var x in dormants)
                Lignes(dm, x.Produit, x.Stock.ToString(Fr), Da(x.Valeur), x.DerniereVente?.ToLocalTime().ToString("dd/MM/yyyy", Fr) ?? "jamais",
                    x.JoursSansVente?.ToString(Fr) ?? "—");
        }

        var note = section.AddParagraph("Chiffres lus dans les rapports et le journal des ventes, sans recalcul fiscal. Rotation calculée sur la valeur actuelle du stock.");
        note.Format.SpaceBefore = Unit.FromPoint(10);
        note.Format.Font.Size = 8;
        note.Format.Font.Color = Color.FromRgb(0x47, 0x54, 0x67);
        return Rendre(doc);
    }

    private static string Pct2(decimal? v) => v is { } x ? $"{x.ToString("0.0", Fr)} %" : "—";

    private static void Lignes(Table t, params string[] valeurs)
    {
        var row = t.AddRow();
        for (var i = 0; i < valeurs.Length && i < row.Cells.Count; i++) row.Cells[i].AddParagraph(valeurs[i]);
    }
}
