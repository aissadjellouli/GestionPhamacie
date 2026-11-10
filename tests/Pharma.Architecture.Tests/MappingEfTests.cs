using Microsoft.EntityFrameworkCore;
using Pharma.Domain.Stock;
using Pharma.Domain.Ventes;
using Pharma.Infrastructure.Persistence;

namespace Pharma.Architecture.Tests;

/// <summary>Le modèle EF se construit sans base : noms de tables et colonnes alignés sur les scripts SQL.</summary>
public class MappingEfTests
{
    private static readonly Microsoft.EntityFrameworkCore.Metadata.IModel Modele =
        new PharmaDbContext(new DbContextOptionsBuilder<PharmaDbContext>()
            .UseNpgsql("Host=localhost;Database=inutilisee").Options).Model;

    [Theory]
    [InlineData(typeof(Lot), "lot")]
    [InlineData(typeof(Boite), "boite")]
    [InlineData(typeof(LigneVente), "ligne_vente")]
    public void Tables_en_minuscules_avec_soulignes(Type entite, string table) =>
        Assert.Equal(table, Modele.FindEntityType(entite)!.GetTableName());

    [Theory]
    [InlineData(typeof(Lot), nameof(Lot.DateAVerifier), "date_a_verifier")]
    [InlineData(typeof(Lot), nameof(Lot.PrixAchatHt), "prix_achat_ht")]
    [InlineData(typeof(Vente), nameof(Vente.NumFactureChifa), "num_facture_chifa")]
    public void Colonnes_en_minuscules_avec_soulignes(Type entite, string propriete, string colonne) =>
        Assert.Equal(colonne, Modele.FindEntityType(entite)!.FindProperty(propriete)!.GetColumnName());

    [Fact]
    public void Statuts_convertis_en_codes_texte()
    {
        var statut = Modele.FindEntityType(typeof(Boite))!.FindProperty(nameof(Boite.Statut))!;
        Assert.Equal(typeof(string), statut.GetValueConverter()!.ProviderClrType);
    }

    [Fact]
    public void Lignes_de_vente_lues_par_le_champ_prive()
    {
        var lignes = Modele.FindEntityType(typeof(Vente))!.FindNavigation(nameof(Vente.Lignes))!;
        Assert.Equal("_lignes", lignes.FieldInfo?.Name);
    }
}
