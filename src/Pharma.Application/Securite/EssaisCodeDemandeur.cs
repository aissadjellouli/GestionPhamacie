namespace Pharma.Application.Securite;

/// <summary>
/// Codes comptoir faux saisis pour une autorisation sur place, comptés par DEMANDEUR (l'employé connecté qui fait valider), en
/// mémoire (un seul serveur par pharmacie ; un redémarrage de l'API remet les compteurs à zéro). Revue du 2026-11-15 (I2) :
/// au-delà de <see cref="EssaisMax"/> codes faux en <see cref="Fenetre"/>, c'est le demandeur qui est bloqué pour l'autorisation
/// par code, pas le valideur (un employé ne peut plus bloquer volontairement le code d'un collègue). Le blocage progressif du
/// valideur ne s'applique que si plusieurs demandeurs différents se trompent sur son code dans la fenêtre.
/// </summary>
public sealed class EssaisCodeDemandeur
{
    public const int EssaisMax = 5;
    public static readonly TimeSpan Fenetre = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Blocage = TimeSpan.FromMinutes(15);
    /// <summary>Limite haute du nombre de demandeurs ou de valideurs suivis : au-delà, la demande est refusée plutôt que la mémoire grossir.</summary>
    public const int TailleMax = 10_000;

    private sealed class Etat
    {
        public readonly List<DateTime> Essais = [];
        public DateTime? BloqueJusqu;
    }

    private readonly object _verrou = new();
    private readonly Dictionary<long, Etat> _demandeurs = [];
    /// <summary>Valideur → demandeur → dernier code faux.</summary>
    private readonly Dictionary<long, Dictionary<long, DateTime>> _valideurs = [];

    /// <summary>Résultat de la réservation d'un essai : numéro de l'essai, ou blocage (avec sa fin) ; Sature : mémoire pleine.</summary>
    public readonly record struct Reservation(int? Essai, DateTime? BloqueJusqu, bool Sature = false, bool VientDeBloquer = false);

    /// <summary>
    /// Réserve un essai pour ce demandeur, de façon atomique, avant la vérification du code (comme le compteur du valideur) :
    /// l'essai qui atteint le maximum pose le blocage ; un bon code rend l'essai (<see cref="NoterSucces"/>).
    /// </summary>
    public Reservation Reserver(long demandeurId, DateTime maintenantUtc)
    {
        lock (_verrou)
        {
            if (!_demandeurs.TryGetValue(demandeurId, out var e))
            {
                if (_demandeurs.Count >= TailleMax) Purger(maintenantUtc);
                if (_demandeurs.Count >= TailleMax) return new Reservation(null, null, Sature: true);
                _demandeurs[demandeurId] = e = new Etat();
            }
            if (e.BloqueJusqu is { } b && b > maintenantUtc) return new Reservation(null, b);
            if (e.BloqueJusqu is not null) { e.BloqueJusqu = null; e.Essais.Clear(); }
            e.Essais.RemoveAll(t => maintenantUtc - t > Fenetre);
            e.Essais.Add(maintenantUtc);
            var bloque = e.Essais.Count >= EssaisMax;
            if (bloque) e.BloqueJusqu = maintenantUtc + Blocage;
            return new Reservation(e.Essais.Count, e.BloqueJusqu, VientDeBloquer: bloque);
        }
    }

    /// <summary>Bon code : l'essai réservé est rendu (et le blocage qu'il avait posé levé).</summary>
    public void NoterSucces(long demandeurId)
    {
        lock (_verrou)
        {
            if (!_demandeurs.TryGetValue(demandeurId, out var e)) return;
            if (e.Essais.Count > 0) e.Essais.RemoveAt(e.Essais.Count - 1);
            if (e.Essais.Count < EssaisMax) e.BloqueJusqu = null;
            if (e.Essais.Count == 0) _demandeurs.Remove(demandeurId);
        }
    }

    /// <summary>
    /// Code faux sur ce valideur : nombre de demandeurs DIFFÉRENTS qui se sont trompés sur son code dans la fenêtre (celui-ci compris).
    /// À partir de deux, le blocage progressif du valideur s'applique.
    /// </summary>
    public int NoterEchecValideur(long valideurId, long demandeurId, DateTime maintenantUtc)
    {
        lock (_verrou)
        {
            if (!_valideurs.TryGetValue(valideurId, out var d))
            {
                if (_valideurs.Count >= TailleMax) Purger(maintenantUtc);
                // Mémoire pleine : on compte comme une attaque concertée (le valideur reste protégé par son propre compteur)
                if (_valideurs.Count >= TailleMax) return 2;
                _valideurs[valideurId] = d = [];
            }
            foreach (var (k, t) in d.ToList()) if (maintenantUtc - t > Fenetre) d.Remove(k);
            d[demandeurId] = maintenantUtc;
            return d.Count;
        }
    }

    /// <summary>Nombre de demandeurs et de valideurs suivis (tests).</summary>
    public int Nombre { get { lock (_verrou) return _demandeurs.Count + _valideurs.Count; } }

    private void Purger(DateTime maintenantUtc)
    {
        foreach (var (k, e) in _demandeurs.ToList())
            if (e.BloqueJusqu is { } b ? b <= maintenantUtc : e.Essais.All(t => maintenantUtc - t > Fenetre))
                _demandeurs.Remove(k);
        foreach (var (k, d) in _valideurs.ToList())
            if (d.Values.All(t => maintenantUtc - t > Fenetre))
                _valideurs.Remove(k);
    }
}
