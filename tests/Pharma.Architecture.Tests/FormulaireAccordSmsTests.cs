using MigraDoc.DocumentObjectModel;
using Pharma.Application.Achats;
using Pharma.Application.Administration;
using Pharma.Infrastructure.Impression;
using PdfSharp.Pdf.IO;

namespace Pharma.Architecture.Tests;

/// <summary>Formulaire d'accord SMS (2026-11-16) : PDF A4 produit sans erreur, nom du patient et mentions présents.</summary>
public class FormulaireAccordSmsTests
{
    private static ContenuFormulaireAccordSms Contenu(string texte) =>
        new(new IdentitePharmacie("Pharmacie Fictive", "Dr Fictif", "1 rue Fictive", "000", null, null, "fr"), "Patient Fictif", "0550000000",
            texte, new DateOnly(2026, 11, 16));

    private static string Texte(Document doc)
    {
        var morceaux = new List<string>();
        void Lire(System.Collections.IEnumerable elements)
        {
            foreach (var e in elements)
                switch (e)
                {
                    case Paragraph p: Lire(p.Elements); break;
                    case FormattedText f: Lire(f.Elements); break;
                    case Text t: morceaux.Add(t.Content ?? ""); break;
                }
        }
        foreach (var o in doc.Sections) if (o is Section s) Lire(s.Elements);
        return string.Join(" ", morceaux);
    }

    [Fact]
    public void Pdf_genere_avec_le_nom_du_patient_et_la_mention()
    {
        var contenu = Contenu(RegleFormulaireAccordSms.TexteParDefaut);

        var pdf = new DocumentsPdf().FormulaireAccordSms(contenu);
        using var lu = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        Assert.True(lu.PageCount >= 1);

        var texte = Texte(DocumentsPdf.DocumentFormulaireAccordSms(contenu));
        Assert.Contains("Patient Fictif", texte);
        Assert.Contains("0550000000", texte);
        Assert.Contains("commande est disponible", texte);
        Assert.Contains("18-07", texte);
        Assert.Contains("Signature du patient", texte);
        Assert.Contains("Cachet", texte);
    }

    private static string Racine()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } racine) return racine;
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    /// <summary>2026-11-16 : bandeau de la Caisse relu toutes les 5 minutes, minuterie arrêtée à la sortie ; « Patients en attente » ouvert depuis la Caisse.</summary>
    [Fact]
    public void Caisse_actualise_le_bandeau_toutes_les_5_minutes_et_ouvre_les_patients_en_attente()
    {
        var caisse = File.ReadAllText(Path.Combine(Racine(), "src", "Pharma.Desktop", "Composants", "Pages", "Caisse.razor"));
        Assert.Contains("IntervalleAlertesDispo = TimeSpan.FromMinutes(5)", caisse);
        Assert.Contains("new PeriodicTimer(IntervalleAlertesDispo)", caisse);
        var dispose = caisse[caisse.IndexOf("public async ValueTask DisposeAsync()", StringComparison.Ordinal)..];
        Assert.Contains("arretAlertesDispo.Cancel();", dispose);
        Assert.Contains("<OngletPatientsAttente EnFenetre=\"true\"", caisse);
        Assert.DoesNotContain("href=\"achats?onglet=patients-attente\"", caisse);
    }

    [Fact]
    public void Texte_regle_par_le_titulaire_sinon_texte_par_defaut()
    {
        Assert.Equal(RegleFormulaireAccordSms.TexteParDefaut, RegleFormulaireAccordSms.Texte(null));
        Assert.Equal(RegleFormulaireAccordSms.TexteParDefaut, RegleFormulaireAccordSms.Texte("  "));
        Assert.Contains("Texte réglé", Texte(DocumentsPdf.DocumentFormulaireAccordSms(Contenu(RegleFormulaireAccordSms.Texte(" Texte réglé ")))));    }
}
