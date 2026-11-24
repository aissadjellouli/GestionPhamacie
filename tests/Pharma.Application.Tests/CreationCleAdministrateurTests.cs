using Pharma.Desktop.Services;

namespace Pharma.Application.Tests;

/// <summary>
/// Création de la clé du poste en administrateur (D4, option 1) : ligne de commande du processus relancé par la demande d'élévation de Windows.
/// La création réelle de la clé est remplacée par une doublure : aucune clé ni dossier créé.
/// </summary>
public class CreationCleAdministrateurTests
{
    private const string NomValide = "PharmaOfficine-Poste-0123456789ab";

    [Fact]
    public void Demarrage_normal_sans_argument_n_est_pas_le_mode_cle()
    {
        Assert.Null(CreationCleAdministrateur.Traiter([], _ => throw new InvalidOperationException("ne doit pas créer")));
        Assert.Null(CreationCleAdministrateur.Traiter(["--autre"], _ => throw new InvalidOperationException("ne doit pas créer")));
    }

    [Fact]
    public void Nom_valide_cree_la_cle_et_rend_le_code_de_la_creation()
    {
        string? cree = null;
        Assert.Equal(CreationCleAdministrateur.Reussi, CreationCleAdministrateur.Traiter([CreationCleAdministrateur.Argument, NomValide], n => { cree = n; return 0; }));
        Assert.Equal(NomValide, cree);
        Assert.Equal(CreationCleAdministrateur.Echec, CreationCleAdministrateur.Traiter([CreationCleAdministrateur.Argument, NomValide], _ => CreationCleAdministrateur.Echec));
    }

    [Theory]
    [InlineData("PharmaOfficine-Poste-0123456789AB")]     // majuscules : jamais produites par le poste
    [InlineData("PharmaOfficine-Poste-0123456789a")]      // trop court
    [InlineData("PharmaOfficine-Poste-0123456789abc")]    // trop long
    [InlineData("AutreCle")]
    [InlineData("PharmaOfficine-Poste-0123456789ab x")]
    [InlineData("")]
    public void Nom_de_cle_hors_forme_est_refuse_sans_rien_creer(string nom)
    {
        Assert.False(CreationCleAdministrateur.NomAccepte(nom));
        Assert.Equal(CreationCleAdministrateur.ArgumentsInvalides,
            CreationCleAdministrateur.Traiter([CreationCleAdministrateur.Argument, nom], _ => throw new InvalidOperationException("ne doit pas créer")));
    }

    [Fact]
    public void Argument_sans_nom_ou_en_trop_est_refuse()
    {
        Assert.Equal(CreationCleAdministrateur.ArgumentsInvalides, CreationCleAdministrateur.Traiter([CreationCleAdministrateur.Argument], _ => throw new InvalidOperationException("ne doit pas créer")));
        Assert.Equal(CreationCleAdministrateur.ArgumentsInvalides,
            CreationCleAdministrateur.Traiter([CreationCleAdministrateur.Argument, NomValide, "--plus"], _ => throw new InvalidOperationException("ne doit pas créer")));
    }

    [Fact]
    public void Lancer_refuse_un_nom_hors_forme_sans_demande_d_elevation()
    {
        Assert.Throws<InvalidOperationException>(() => CreationCleAdministrateur.Lancer("cle arbitraire"));
    }

    [Fact]
    public void Le_nom_produit_par_le_poste_est_accepte()
    {
        var nom = "PharmaOfficine-Poste-" + Guid.NewGuid().ToString("N")[..12];
        Assert.True(CreationCleAdministrateur.NomAccepte(nom));
    }
}
