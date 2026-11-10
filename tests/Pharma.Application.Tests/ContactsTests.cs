using Pharma.Application.Contacts;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class ContactsEnMemoire : IContacts
{
    public bool? CoordonneesDemandees { get; private set; }

    public Task<FicheContact?> LireAsync(string type, long id, bool coordonneesEmploye, CancellationToken ct = default)
    {
        CoordonneesDemandees = coordonneesEmploye;
        return Task.FromResult<FicheContact?>(id == 404 ? null : new FicheContact(type, id, "Nom", "Précision", true, [], []));
    }
}

public class ContactsTests
{
    [Theory]
    [InlineData(" 0555 12 34 56 ", "0555 12 34 56")]
    [InlineData("+213  21-45-67-89", "+213 21-45-67-89")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Le_telephone_est_nettoye(string? saisi, string? attendu) => Assert.Equal(attendu, RegleContact.Telephone(saisi));

    [Theory]
    [InlineData("12345")]          // trop peu de chiffres
    [InlineData("0555 abc 123")]   // lettres
    public void Un_telephone_invalide_est_refuse(string saisi) => Assert.Throws<RegleMetierException>(() => RegleContact.Telephone(saisi));

    [Theory]
    [InlineData(" contact@cnas.dz ", "contact@cnas.dz")]
    [InlineData("  ", null)]
    public void L_email_est_nettoye(string? saisi, string? attendu) => Assert.Equal(attendu, RegleContact.Email(saisi));

    [Theory]
    [InlineData("contact")]
    [InlineData("contact@cnas")]
    [InlineData("con tact@cnas.dz")]
    public void Un_email_invalide_est_refuse(string saisi) => Assert.Throws<RegleMetierException>(() => RegleContact.Email(saisi));

    [Fact]
    public async Task La_fiche_d_un_patient_demande_le_droit_de_vendre()
    {
        var sans = new ConsulterContact(new ContactsEnMemoire(), new DroitsFactices());
        await Assert.ThrowsAsync<DroitRefuseException>(() => sans.ExecuterAsync("patient", 1, 9));
        var avec = new ConsulterContact(new ContactsEnMemoire(), new DroitsFactices("caisse.vendre"));
        Assert.Equal("patient", (await avec.ExecuterAsync("Patient", 1, 9)).Type);
    }

    [Fact]
    public async Task La_fiche_d_une_entreprise_demande_le_droit_de_facturer() =>
        await Assert.ThrowsAsync<DroitRefuseException>(() => new ConsulterContact(new ContactsEnMemoire(), new DroitsFactices("caisse.vendre"))
            .ExecuterAsync("client", 1, 9));

    [Fact]
    public async Task Grossiste_et_organisme_sont_ouverts_a_tout_employe()
    {
        var c = new ConsulterContact(new ContactsEnMemoire(), new DroitsFactices());
        Assert.Equal("grossiste", (await c.ExecuterAsync("grossiste", 1, 9)).Type);
        Assert.Equal("organisme", (await c.ExecuterAsync("organisme", 1, 9)).Type);
    }

    [Theory]
    [InlineData(5L, false, false, false)]   // un collègue : coordonnées masquées
    [InlineData(9L, false, false, true)]    // sa propre fiche
    [InlineData(5L, true, false, true)]     // le titulaire
    [InlineData(5L, false, true, true)]     // qui gère les utilisateurs
    public async Task Les_coordonnees_d_un_employe_sont_reservees(long fiche, bool titulaire, bool gereUtilisateurs, bool attendu)
    {
        var contacts = new ContactsEnMemoire();
        var droits = gereUtilisateurs ? new DroitsFactices("admin.utilisateurs") { Titulaire = titulaire } : new DroitsFactices { Titulaire = titulaire };
        await new ConsulterContact(contacts, droits).ExecuterAsync("employe", fiche, 9);
        Assert.Equal(attendu, contacts.CoordonneesDemandees);
    }

    [Fact]
    public async Task Type_inconnu_ou_contact_disparu_refuses()
    {
        var c = new ConsulterContact(new ContactsEnMemoire(), new DroitsFactices("caisse.vendre"));
        await Assert.ThrowsAsync<RegleMetierException>(() => c.ExecuterAsync("medecin", 1, 9));
        await Assert.ThrowsAsync<RegleMetierException>(() => c.ExecuterAsync("grossiste", 404, 9));
    }
}
