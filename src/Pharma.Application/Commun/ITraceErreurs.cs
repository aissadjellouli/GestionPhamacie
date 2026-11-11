namespace Pharma.Application.Commun;

/// <summary>Erreur d'un traitement d'arrière-plan qui ne doit pas bloquer l'opération (surveillance…) : inscrite au journal du serveur.</summary>
public interface ITraceErreurs
{
    void Erreur(string message, Exception exception);
}
