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
    [Fact]
    public async Task Document_range_dans_le_dossier_de_son_type()
    {
        var ged = new GedEnMemoire();
        var chemin = await new DeposerDocumentGed(ged).ExecuterAsync("factures", "F-2026-0021.pdf", [1, 2, 3]);
        Assert.Equal(Path.Combine("Factures", "F-2026-0021.pdf"), chemin);   // type écrit comme le dossier de la GED
        Assert.Equal(("Factures", "F-2026-0021.pdf", 3), ged.Deposes.Single());
    }

    [Fact]
    public async Task Type_inconnu_document_vide_ou_nom_absent_refuses()
    {
        var cas = new DeposerDocumentGed(new GedEnMemoire());
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("Sauvegardes", "base.dump", [1]));   // réservé au serveur
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("Factures", "F.pdf", []));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("Factures", "  ", [1]));
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
