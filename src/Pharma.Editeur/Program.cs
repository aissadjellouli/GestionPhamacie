using System.Globalization;
using System.Security.Cryptography;
using Pharma.Application.Offre;

namespace Pharma.Editeur;

/// <summary>
/// Outil de l'éditeur (docs/offre-et-options.md, script 172) : génère la clé de signature (fichier chiffré par un mot de passe, hors de
/// tout dépôt), signe les codes d'activation d'une pharmacie et relit un code. Le mot de passe est demandé au clavier (sans écho) ou lu
/// dans la variable d'environnement PHARMA_EDITEUR_MDP. Jamais livré aux pharmacies.
/// </summary>
public static class Program
{
    public const string FichierCle = "cle-editeur.pem", FichierPublique = "cle-editeur-publique.txt", VariableMotDePasse = "PHARMA_EDITEUR_MDP";
    public const int MotDePasseMin = 12;

    public static int Main(string[] args)
    {
        try
        {
            return (args.FirstOrDefault() ?? "aide") switch
            {
                "generer-cle" => GenererCle(Option(args, "--dossier") ?? throw new ArgumentException("Option --dossier <dossier> obligatoire.")),
                "cle-publique" => ClePublique(Exiger(args, "--cle")),
                "signer" => Signer(args),
                "lire" => Lire(Exiger(args, "--code"), Option(args, "--cle-publique")),
                _ => Aide()
            };
        }
        catch (Exception e) when (e is ArgumentException or ActivationInvalideException or CryptographicException or IOException)
        {
            Console.Error.WriteLine($"Refusé : {e.Message}");
            return 1;
        }
    }

    private static int Aide()
    {
        Console.WriteLine("""
            Pharma.Editeur : activations de l'offre (signées par l'éditeur)

              generer-cle  --dossier <dossier>
                  Crée la clé de signature : cle-editeur.pem (chiffrée par le mot de passe, à copier aussi sur une clé USB rangée à part)
                  et cle-editeur-publique.txt (à intégrer au programme : CleEditeur.PubliqueBase64). Refuse d'écraser une clé existante.
              cle-publique --cle <cle-editeur.pem>
                  Affiche la clé publique d'une clé existante.
              signer --cle <cle-editeur.pem> --officine <code de l'officine> --nif <NIF> --postes <n>
                     [--packs A,B,…|tous] [--scanners <n>] [--fin AAAA-MM-JJ] [--transition]
                  Signe un code d'activation (--transition : code « tout compris » de la mise en place, --fin obligatoire).
              lire --code <code> [--cle-publique <base64>]
                  Affiche le contenu d'un code ; avec la clé publique, contrôle aussi sa signature.

            Mot de passe : demandé au clavier, ou variable d'environnement PHARMA_EDITEUR_MDP.
            """);
        return 0;
    }

    /// <summary>Crée la paire de clés ECDSA P-256 ; la clé privée est chiffrée (PBKDF2-SHA256, 600 000 itérations, AES-256).</summary>
    public static int GenererCle(string dossier, string? motDePasse = null)
    {
        var fichier = Path.Combine(dossier, FichierCle);
        if (File.Exists(fichier)) throw new ArgumentException($"Une clé existe déjà : {fichier} (jamais écrasée).");
        var mdp = motDePasse ?? MotDePasse("Mot de passe de la clé", confirmer: true);
        if (mdp.Length < MotDePasseMin) throw new ArgumentException($"Mot de passe trop court ({MotDePasseMin} caractères au moins).");
        Directory.CreateDirectory(dossier);
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = ecdsa.ExportEncryptedPkcs8PrivateKeyPem(mdp, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));
        File.WriteAllText(fichier, pem);
        var publique = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        File.WriteAllText(Path.Combine(dossier, FichierPublique), publique + Environment.NewLine);
        Console.WriteLine($"Clé créée : {fichier}");
        Console.WriteLine("Copiez ce fichier sur une clé USB rangée à part. Sans lui ou sans son mot de passe, plus aucun code ne peut être signé.");
        Console.WriteLine($"Clé publique (à intégrer au programme, CleEditeur.PubliqueBase64) :{Environment.NewLine}{publique}");
        return 0;
    }

    private static int ClePublique(string fichier)
    {
        using var ecdsa = Charger(fichier, MotDePasse("Mot de passe de la clé"));
        Console.WriteLine(Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
        return 0;
    }

    private static int Signer(string[] args)
    {
        var activation = Activation(args, DateTime.UtcNow);
        CodeActivation.Controler(activation);
        using var ecdsa = Charger(Exiger(args, "--cle"), MotDePasse("Mot de passe de la clé"));
        var code = Signer(activation, ecdsa);
        Console.WriteLine($"Pharmacie {activation.Officine} · NIF {activation.Nif} · {activation.Postes} poste(s) · packs "
                          + $"{(activation.Packs.Count == 0 ? "aucun" : string.Join(",", activation.Packs))} · scanners {activation.ScannersMobiles} · "
                          + $"{(activation.Fin is { } f ? $"jusqu'au {f:dd/MM/yyyy}" : "définitive")}{(activation.Transition ? " · transition" : "")}");
        Console.WriteLine(code);
        return 0;
    }

    /// <summary>Signe et relit aussitôt le code avec la clé publique (un code qui ne se relit pas n'est jamais rendu).</summary>
    public static string Signer(ActivationOffre activation, ECDsa clePrivee)
    {
        var contenu = CodeActivation.Contenu(activation);
        var code = CodeActivation.Composer(contenu, clePrivee.SignData(contenu, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        CodeActivation.Lire(code, clePrivee.ExportSubjectPublicKeyInfo());
        return code;
    }

    /// <summary>Activation décrite par les options de la commande « signer » (règles d'arguments pures, testées).</summary>
    public static ActivationOffre Activation(string[] args, DateTime maintenantUtc)
    {
        var officine = Guid.TryParse(Exiger(args, "--officine"), out var g) ? g : throw new ArgumentException("Code de l'officine invalide (attendu : son identifiant, ex. 3f2a…).");
        var nif = CodeActivation.NormaliserNif(Exiger(args, "--nif"));
        var transition = args.Contains("--transition");
        var packsTexte = Option(args, "--packs");
        IReadOnlyList<string> packs = transition || packsTexte is "tous"
            ? PacksOffre.Tous.Select(p => p.Code).ToList()
            : (packsTexte ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => p.ToUpperInvariant()).ToList();
        var postes = int.TryParse(Exiger(args, "--postes"), out var n) ? n : throw new ArgumentException("Nombre de postes invalide.");
        var scanners = Option(args, "--scanners") is { } s ? int.TryParse(s, out var m) ? m : throw new ArgumentException("Nombre de scanners invalide.") : 0;
        DateOnly? fin = Option(args, "--fin") is { } f
            ? DateOnly.TryParseExact(f, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : throw new ArgumentException("Date de fin invalide (AAAA-MM-JJ).")
            : null;
        return new ActivationOffre(officine, nif, DateTime.SpecifyKind(maintenantUtc, DateTimeKind.Utc), packs, postes, scanners, fin, transition);
    }

    private static int Lire(string code, string? clePublique)
    {
        var a = clePublique is null ? CodeActivation.LireSansControle(code) : CodeActivation.Lire(code, Convert.FromBase64String(clePublique));
        Console.WriteLine($"Officine : {a.Officine}{Environment.NewLine}NIF : {a.Nif}{Environment.NewLine}Signé le : {a.EmiseLeUtc:yyyy-MM-dd HH:mm} UTC");
        Console.WriteLine($"Postes : {a.Postes}{Environment.NewLine}Scanners mobiles : {a.ScannersMobiles}");
        Console.WriteLine($"Packs : {(a.Packs.Count == 0 ? "aucun" : string.Join(", ", a.Packs.Select(p => $"{p} {PacksOffre.Libelle(p)}")))}");
        Console.WriteLine($"Fin : {(a.Fin is { } f ? f.ToString("yyyy-MM-dd") : "définitive")}{(a.Transition ? " (transition)" : "")}");
        Console.WriteLine(clePublique is null ? "Signature NON contrôlée (ajoutez --cle-publique)." : "Signature contrôlée : émise par l'éditeur.");
        return 0;
    }

    public static ECDsa Charger(string fichier, string motDePasse)
    {
        if (!File.Exists(fichier)) throw new ArgumentException($"Clé introuvable : {fichier}");
        var ecdsa = ECDsa.Create();
        try { ecdsa.ImportFromEncryptedPem(File.ReadAllText(fichier), motDePasse); }
        catch (CryptographicException) { ecdsa.Dispose(); throw new ArgumentException("Mot de passe faux ou clé illisible."); }
        return ecdsa;
    }

    private static string MotDePasse(string invite, bool confirmer = false)
    {
        if (Environment.GetEnvironmentVariable(VariableMotDePasse) is { Length: > 0 } v) return v;
        var mdp = Saisir($"{invite} : ");
        if (confirmer && Saisir("Confirmez le mot de passe : ") != mdp) throw new ArgumentException("Les deux mots de passe diffèrent.");
        return mdp;
    }

    private static string Saisir(string invite)
    {
        Console.Write(invite);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var texte = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (texte.Length > 0) texte.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) texte.Append(k.KeyChar);
        }
        Console.WriteLine();
        return texte.ToString();
    }

    private static string? Option(string[] args, string nom)
    {
        var i = Array.IndexOf(args, nom);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string Exiger(string[] args, string nom) =>
        Option(args, nom) is { Length: > 0 } v ? v : throw new ArgumentException($"Option {nom} obligatoire.");
}
