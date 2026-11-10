using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Pharma.Domain.Achats;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Stock;
using Pharma.Domain.Ventes;

namespace Pharma.Infrastructure.Persistence;

/// <summary>
/// Le schéma est géré par les scripts SQL du dossier database/ (tables, vues, déclencheurs).
/// EF Core ne fait que lire et écrire : pas de migrations EF.
/// </summary>
public sealed partial class PharmaDbContext(DbContextOptions<PharmaDbContext> options) : DbContext(options)
{
    public DbSet<Produit> Produits => Set<Produit>();
    public DbSet<Dci> Dcis => Set<Dci>();
    public DbSet<Fournisseur> Fournisseurs => Set<Fournisseur>();
    public DbSet<Reception> Receptions => Set<Reception>();
    public DbSet<Lot> Lots => Set<Lot>();
    public DbSet<Boite> Boites => Set<Boite>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Vente> Ventes => Set<Vente>();
    public DbSet<LigneVente> LignesVente => Set<LigneVente>();
    public DbSet<Utilisateur> Utilisateurs => Set<Utilisateur>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Produit>().Ignore(p => p.EstSousRegistre);
        b.Entity<Produit>().Property(p => p.Classe)
            .HasConversion(v => v.Code(), c => CodesMetier.ClasseDepuisCode(c));

        b.Entity<Vente>().HasMany(v => v.Lignes).WithOne().HasForeignKey(l => l.VenteId);
        b.Entity<Vente>().Navigation(v => v.Lignes).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Entity<Vente>().Property(v => v.Type)
            .HasConversion(v => v.Code(), c => CodesMetier.TypeVenteDepuisCode(c));
        b.Entity<Vente>().Property(v => v.Statut)
            .HasConversion(v => v.Code(), c => CodesMetier.StatutVenteDepuisCode(c));

        b.Entity<Lot>().HasOne(l => l.Produit).WithMany().HasForeignKey(l => l.ProduitId);
        b.Entity<Lot>().HasOne(l => l.Fournisseur).WithMany().HasForeignKey(l => l.FournisseurId);

        b.Entity<Boite>().HasOne(x => x.Lot).WithMany().HasForeignKey(x => x.LotId);
        b.Entity<Boite>().Property(x => x.Statut)
            .HasConversion(v => v.Code(), c => CodesMetier.StatutBoiteDepuisCode(c));

        foreach (var entite in b.Model.GetEntityTypes())
        {
            entite.SetTableName(SnakeCase(entite.ClrType.Name));
            foreach (var propriete in entite.GetProperties())
                propriete.SetColumnName(SnakeCase(propriete.Name));
        }
    }

    // LigneVente -> ligne_vente, DatePeremption -> date_peremption
    internal static string SnakeCase(string nom) => Majuscule().Replace(nom, "_$1").ToLowerInvariant();

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex Majuscule();
}
