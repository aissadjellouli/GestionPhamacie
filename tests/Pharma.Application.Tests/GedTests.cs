using Pharma.Application.Ged;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class GedEnMemoire : IGed
{
    public List<(string Type, string Nom, int Taille)> Deposes { get; } = [];
    public Task<string> DeposerAsync(string type, string nomFichier, byte[] contenu, CancellationToken ct = default)
    {
        Deposes.Add((type, nomFichier, contenu.Length));
        return Task.FromResult(Path.Combine(type, nomFichier));
    }
}

public class GedTests
{
    private static readonly byte[] Pdf = "%PDF-1.4 test"u8.ToArray();

    [Fact]
    public async Task Depot_inscrit_au_journal_sans_le_contenu()
    {
        var journal = new JournalEspion();
        await new DeposerDocumentGed(new GedEnMemoire(), journal).ExecuterAsync(7, "Registre", "registre.pdf", Pdf);
        Assert.Equal(["document_depose_ged"], journal.Actions);
        var d = journal.Details.Single();
        Assert.Equal("Registre", d["type"]);
        Assert.Equal("registre.pdf", d["fichier"]);
        Assert.Equal(Pdf.Length, d["taille"]);
        Assert.Equal(64, ((string)d["sha256"]!).Length);
    }

    [Fact]
    public async Task Type_reel_controle_extension_et_signature()
    {
        var ged = new GedEnMemoire();
        var cas = new DeposerDocumentGed(ged, new JournalEspion());
        byte[] executable = [0x4D, 0x5A, 0x90, 0x00, 0x03];
        // Exécutable renommé en PDF, extension interdite, pas d'extension, texte qui cache un binaire : refusés, rien n'est rangé
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(1, "Factures", "facture.pdf", executable));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(1, "Factures", "setup.exe", executable));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(1, "Factures", "page.html", "<html>"u8.ToArray()));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(1, "Factures", "sans_extension", Pdf));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(1, "Rapports", "export.csv", [0x41, 0x00, 0x42]));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(1, "Rapports", "tableau.xlsx", Pdf));
        Assert.Empty(ged.Deposes);
        // Types légitimes acceptés
        await cas.ExecuterAsync(1, "Rapports", "export.csv", "a;b;c"u8.ToArray());
        await cas.ExecuterAsync(1, "Rapports", "tableau.XLSX", [0x50, 0x4B, 0x03, 0x04, 0x00]);
        await cas.ExecuterAsync(1, "Financements", "scan.jpg", [0xFF, 0xD8, 0xFF, 0xE0]);
        await cas.ExecuterAsync(1, "Financements", "scan.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]);
        Assert.Equal(4, ged.Deposes.Count);
    }
    [Fact]
    public async Task Document_range_dans_le_dossier_de_son_type()
    {
        var ged = new GedEnMemoire();
        var chemin = await new DeposerDocumentGed(ged, new JournalEspion()).ExecuterAsync(1, "factures", "F-2026-0021.pdf", Pdf);
        Assert.Equal(Path.Combine("Factures", "F-2026-0021.pdf"), chemin);   // type écrit comme le dossier de la GED
        Assert.Equal(("Factures", "F-2026-0021.pdf", Pdf.Length), ged.Deposes.Single());
    }

    [Fact]
    public async Task Type_inconnu_document_vide_ou_nom_absent_refuses()
    {
        var cas = new DeposerDocumentGed(new GedEnMemoire(), new JournalEspion());
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(1, "Sauvegardes", "base.dump", [1]));   // réservé au serveur
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(1, "Factures", "F.pdf", []));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(1, "Factures", "  ", Pdf));
    }

    [Fact]
    public void Nom_sans_chemin_ni_caractere_interdit()
    {
        Assert.Equal("rapport.pdf", DeposerDocumentGed.NomSur(@"..\..\Windows\rapport.pdf"));
        Assert.Equal("ab.pdf", DeposerDocumentGed.NomSur("a<b>.pdf"));
        Assert.Equal(150, DeposerDocumentGed.NomSur(new string('x', 200) + ".pdf").Length);
        Assert.EndsWith(".pdf", DeposerDocumentGed.NomSur(new string('x', 200) + ".pdf"));
    }

    [Fact]
    public void Dossier_d_un_type_sous_la_racine()
    {
        var ged = new ReglagesGed(@"C:\ProgramData\PharmaOfficine\GED");
        Assert.Equal(@"C:\ProgramData\PharmaOfficine\GED\Ordonnances", ged.Dossier(TypesGed.Ordonnances));
        Assert.Equal(7, ged.RetentionSauvegardesJours);
    }
}
