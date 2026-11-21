using Microsoft.Extensions.Options;
using Pharma.Application.Catalogue;
using Pharma.Application.Demo;
using Pharma.Application.Securite;

namespace Pharma.Api.Demo;

/// <summary>
/// Démonstration seulement (enregistrée par Program.cs quand Demo:Actif) : la nomenclature nationale ne peut être lue que par l'API
/// (fichier Excel). Si la base n'en a aucune version (premier démarrage, ou base remise à zéro depuis le gabarit), le fichier réglé
/// dans Demo:FichierNomenclature est importé au nom du compte de démonstration « titulaire », comme le ferait le titulaire depuis
/// Administration › Nomenclature. Vérifié 20 s après le démarrage, puis toutes les 2 minutes ; une erreur est notée au journal.
/// Le service de remise à zéro (deploy/demo/reinit) attend cet import pour passer le grand jeu de test 905.
/// </summary>
public sealed class ImportNomenclatureDemo(IServiceProvider services, IOptions<ReglagesDemo> reglages, ILogger<ImportNomenclatureDemo> journal)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken arret)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), arret); }
        catch (OperationCanceledException) { return; }
        using var minuterie = new PeriodicTimer(TimeSpan.FromMinutes(2));
        do
        {
            try { await ImporterSiAbsenteAsync(arret); }
            catch (OperationCanceledException) when (arret.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                journal.LogError(e, "Démonstration : import de la nomenclature nationale en échec");
            }
        }
        while (await AttendreAsync(minuterie, arret));
    }

    private static async Task<bool> AttendreAsync(PeriodicTimer minuterie, CancellationToken arret)
    {
        try { return await minuterie.WaitForNextTickAsync(arret); }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>Vrai si un import a été fait.</summary>
    public async Task<bool> ImporterSiAbsenteAsync(CancellationToken ct)
    {
        if (reglages.Value.FichierNomenclature is not { Length: > 0 } fichier) return false;
        await using var scope = services.CreateAsyncScope();
        if (await scope.ServiceProvider.GetRequiredService<INomenclature>().VersionAsync(ct) is not null) return false;
        if (!File.Exists(fichier))
        {
            journal.LogWarning("Démonstration : nomenclature nationale absente de la base et fichier introuvable ({Fichier})", fichier);
            return false;
        }
        var titulaire = await scope.ServiceProvider.GetRequiredService<IComptesLecture>().TrouverActifAsync(ModeDemo.ComptesDemo[0], ct);
        if (titulaire is null)
        {
            journal.LogWarning("Démonstration : compte de démonstration « titulaire » absent, nomenclature non importée");
            return false;
        }
        await using var flux = File.OpenRead(fichier);
        var version = await scope.ServiceProvider.GetRequiredService<ImporterNomenclature>().ExecuterAsync(flux, Path.GetFileName(fichier), titulaire.Id, ct);
        journal.LogInformation("Démonstration : nomenclature nationale importée ({Version} : {EnVigueur} en vigueur, {NonRenouveles} non renouvelés, {Retraits} retraits)",
            version.Libelle, version.EnVigueur, version.NonRenouveles, version.Retraits);
        return true;
    }
}
