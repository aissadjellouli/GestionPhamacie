// Nettoyage des scripts SQL à la livraison (lot P1 de la protection du code, décision du titulaire du 2026-11-27 : « nettoyer à la
// livraison, pas dans git ») : retire les commentaires (« -- … » et « /* … */ », imbriqués) des scripts copiés dans le paquet ; le dépôt
// garde les versions commentées. Lecture du SQL comme PostgreSQL la fait : rien n'est touché dans les textes ('…', E'…' avec \', "…"),
// les commandes psql (lignes « \… ») et la ligne d'en-tête « 001 à NNN » (contrôlée par creer_base_officine.ps1) ; dans les corps
// $…$ (fonctions, blocs DO), les commentaires sont retirés de la même façon, textes compris dans ces corps respectés.
// Chargé par scripts\mep\outils_mep.ps1 (Add-Type) et compilé dans les tests d'architecture : C# 5 seulement (Windows PowerShell 5.1).
using System;
using System.Text;
using System.Text.RegularExpressions;

namespace PharmaOfficine.Livraison
{
    public static class NettoyageSql
    {
        // « 001 à NNN » (à écrit à : le fichier se compile aussi avec l'ancien compilateur de Windows PowerShell 5.1)
        private static readonly Regex EnteteInit = new Regex(@"001 à \d{3}");

        /// <summary>Script sans commentaires (idempotent : nettoyer deux fois donne le même texte).</summary>
        public static string Nettoyer(string sql)
        {
            if (sql == null) throw new ArgumentNullException("sql");
            return Lire(sql, false);
        }

        private static string Lire(string sql, bool dansCorps)
        {
            var sortie = new StringBuilder(sql.Length);
            int i = 0, n = sql.Length;
            while (i < n)
            {
                char c = sql[i];
                // Commande psql (\set, \if, \i, \echo…) en début de ligne : ligne gardée telle quelle
                if (!dansCorps && c == '\\' && DebutDeLigne(sql, i))
                {
                    int fin = FinDeLigne(sql, i);
                    sortie.Append(sql, i, fin - i);
                    i = fin;
                    continue;
                }
                if (c == '-' && i + 1 < n && sql[i + 1] == '-')
                {
                    int fin = FinDeLigne(sql, i);   // position du saut de ligne (non compris)
                    string commentaire = sql.Substring(i, fin - i);
                    if (!dansCorps && EnteteInit.IsMatch(commentaire))
                    {
                        // En-tête « 001 à NNN » gardé (lu par les outils de mise à jour), réduit à cette mention : la suite de la ligne
                        // (notes de conception) ne part pas en officine
                        Match m = EnteteInit.Match(commentaire);
                        sortie.Append(commentaire.Substring(0, m.Index + m.Length));
                        i = fin;
                        continue;
                    }
                    bool ligneVide = RetirerBlancsDeFin(sortie);
                    i = fin;
                    if (ligneVide) i = SauterSautDeLigne(sql, i);   // la ligne ne contenait que le commentaire : elle disparaît
                    continue;
                }
                if (c == '/' && i + 1 < n && sql[i + 1] == '*')
                {
                    int profondeur = 1, j = i + 2;
                    while (j < n && profondeur > 0)
                    {
                        if (sql[j] == '/' && j + 1 < n && sql[j + 1] == '*') { profondeur++; j += 2; }
                        else if (sql[j] == '*' && j + 1 < n && sql[j + 1] == '/') { profondeur--; j += 2; }
                        else j++;
                    }
                    if (profondeur > 0) throw new FormatException("Commentaire /* non fermé à la position " + i);
                    bool ligneVide = RetirerBlancsDeFin(sortie);
                    i = j;
                    if (ligneVide && ResteDeLigneVide(sql, i)) i = SauterSautDeLigne(sql, FinDeLigne(sql, i));
                    else if (!ligneVide && i < n && !char.IsWhiteSpace(sql[i])) sortie.Append(' ');   // « a/*x*/b » : jetons séparés
                    continue;
                }
                if (c == '\'')
                {
                    bool echappements = i > 0 && (sql[i - 1] == 'E' || sql[i - 1] == 'e') && (i < 2 || !CaractereIdentifiant(sql[i - 2]));
                    int fin = FinTexte(sql, i, '\'', echappements);
                    sortie.Append(sql, i, fin - i);
                    i = fin;
                    continue;
                }
                if (c == '"')
                {
                    int fin = FinTexte(sql, i, '"', false);
                    sortie.Append(sql, i, fin - i);
                    i = fin;
                    continue;
                }
                if (c == '$' && (i == 0 || !CaractereIdentifiant(sql[i - 1])))
                {
                    string etiquette = EtiquetteDollar(sql, i);
                    if (etiquette.Length > 0)
                    {
                        int debutCorps = i + etiquette.Length;
                        int finCorps = sql.IndexOf(etiquette, debutCorps, StringComparison.Ordinal);
                        if (finCorps < 0) throw new FormatException("Corps " + etiquette + " non fermé à la position " + i);
                        sortie.Append(etiquette);
                        // Corps de fonction ou de bloc DO (« AS $$ », « DO $$ ») ou SQL dynamique (« EXECUTE $$ ») : du code, nettoyé à son tour. Tout autre texte entre
                        // dollars est une donnée (valeur insérée, texte passé à une fonction) : recopié tel quel, un « -- » n'y est pas un commentaire.
                        if (CorpsDeCode(sql, i)) sortie.Append(Lire(sql.Substring(debutCorps, finCorps - debutCorps), true));
                        else sortie.Append(sql, debutCorps, finCorps - debutCorps);
                        sortie.Append(etiquette);
                        i = finCorps + etiquette.Length;
                        continue;
                    }
                }
                sortie.Append(c);
                i++;
            }
            return sortie.ToString();
        }

        private static bool CaractereIdentifiant(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '$';
        }

        private static bool DebutDeLigne(string sql, int i)
        {
            for (int k = i - 1; k >= 0; k--)
            {
                if (sql[k] == '\n') return true;
                if (sql[k] != ' ' && sql[k] != '\t' && sql[k] != '\r') return false;
            }
            return true;
        }

        private static int FinDeLigne(string sql, int i)
        {
            int fin = sql.IndexOf('\n', i);
            if (fin < 0) return sql.Length;
            return fin > i && sql[fin - 1] == '\r' ? fin - 1 : fin;
        }

        private static int SauterSautDeLigne(string sql, int i)
        {
            if (i < sql.Length && sql[i] == '\r') i++;
            if (i < sql.Length && sql[i] == '\n') i++;
            return i;
        }

        private static bool ResteDeLigneVide(string sql, int i)
        {
            for (int k = i; k < sql.Length && sql[k] != '\n'; k++)
                if (sql[k] != ' ' && sql[k] != '\t' && sql[k] != '\r') return false;
            return true;
        }

        /// <summary>Retire les espaces de fin déjà écrits ; vrai si la ligne courante est alors vide (commentaire seul sur sa ligne).</summary>
        private static bool RetirerBlancsDeFin(StringBuilder sortie)
        {
            int k = sortie.Length;
            while (k > 0 && (sortie[k - 1] == ' ' || sortie[k - 1] == '\t')) k--;
            sortie.Length = k;
            return k == 0 || sortie[k - 1] == '\n';
        }

        private static int FinTexte(string sql, int i, char guillemet, bool echappements)
        {
            int j = i + 1;
            while (j < sql.Length)
            {
                char c = sql[j];
                if (echappements && c == '\\') { j += 2; continue; }
                if (c == guillemet)
                {
                    if (j + 1 < sql.Length && sql[j + 1] == guillemet) { j += 2; continue; }   // '' ou "" : guillemet doublé
                    return j + 1;
                }
                j++;
            }
            throw new FormatException("Texte " + guillemet + " non fermé à la position " + i);
        }

        /// <summary>Le texte entre dollars qui commence en <paramref name="i"/> suit-il « AS », « DO », « EXECUTE » ou « DO LANGUAGE nom » ?</summary>
        private static bool CorpsDeCode(string sql, int i)
        {
            string mot = MotAvant(sql, ref i);
            if (mot.Equals("AS", StringComparison.OrdinalIgnoreCase) || mot.Equals("DO", StringComparison.OrdinalIgnoreCase)
                || mot.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase)) return true;   // EXECUTE : SQL dynamique, du code aussi
            return MotAvant(sql, ref i).Equals("LANGUAGE", StringComparison.OrdinalIgnoreCase) && MotAvant(sql, ref i).Equals("DO", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Mot (lettres, chiffres, _) qui précède la position, blancs sautés ; la position recule devant ce mot. Vide si autre chose précède.</summary>
        private static string MotAvant(string sql, ref int i)
        {
            int fin = i;
            while (fin > 0 && char.IsWhiteSpace(sql[fin - 1])) fin--;
            int debut = fin;
            while (debut > 0 && CaractereIdentifiant(sql[debut - 1])) debut--;
            i = debut;
            return sql.Substring(debut, fin - debut);
        }

        /// <summary>« $$ » ou « $etiquette$ » commençant en i ; chaîne vide sinon (ex. $1, paramètre positionnel).</summary>
        private static string EtiquetteDollar(string sql, int i)
        {
            int j = i + 1;
            if (j < sql.Length && sql[j] == '$') return "$$";
            if (j >= sql.Length || !(char.IsLetter(sql[j]) || sql[j] == '_')) return string.Empty;
            while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_')) j++;
            return j < sql.Length && sql[j] == '$' ? sql.Substring(i, j - i + 1) : string.Empty;
        }
    }
}
