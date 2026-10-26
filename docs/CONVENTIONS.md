# Règles de développement

Ces règles s'appliquent à tout le code du dépôt. Elles sont vérifiées par le compilateur et la CI chaque fois que c'est possible : une règle qu'aucun outil ne contrôle finit oubliée.

## 1. Langue

- Code, commentaires, noms de tests, messages de commit : **anglais**.
- Texte affiché à l'utilisateur final (pages Blazor) : **français**.
- Documentation du dépôt (`docs/`) : français.

## 2. Architecture

Les dépendances vont toujours vers l'intérieur :

```
Web ──► Contracts
Api ──► Infrastructure ──► Application ──► Domain
                              └──► Contracts
```

| Projet | Contenu | Interdit |
|---|---|---|
| `Pulse.Domain` | Entités et règles métier (ex. « N échecs de suite ouvrent un incident ») | Toute dépendance, I/O, horloge système |
| `Pulse.Application` | Cas d'usage (comptes, catalogue, vérifications, alertes), ports (`IPulseStore`, `IAccountStore`, `IPasswordHasher`, `ISecretProtector`, `IMailSender`, `IServiceProbe`, `IHostResolver`), validation | EF Core, `HttpClient`, ASP.NET |
| `Pulse.Infrastructure` | Adaptateurs : EF Core/SQLite, sonde HTTP, planificateur, SMTP, hachage et chiffrement | Logique métier |
| `Pulse.Api` | Endpoints HTTP, authentification par session, limitation de débit | Logique métier |
| `Pulse.Contracts` | DTO partagés entre l'API et le site web | Logique, référence au domaine |
| `Pulse.Web` | Interface Blazor, qui parle à l'API uniquement | Accès direct à la base |

Une règle métier vit à **un seul endroit**. Si deux couches l'écrivent, la seconde est un bug en attente.

## 3. Style C#

- Cible : .NET 10, C# récent, `Nullable` activé. Les avertissements sont des erreurs (`TreatWarningsAsErrors`), les analyseurs sont en `latest-recommended`.
- Espaces de noms de fichier (`namespace X;`), accolades obligatoires, `using` hors de l'espace de noms.
- Noms : `PascalCase` pour types, méthodes et propriétés ; `_camelCase` pour les champs privés ; `I` devant les interfaces ; méthodes asynchrones suffixées `Async`.
- Un nom dit ce que la chose contient ou fait, sans commentaire pour l'expliquer. `ApplyCheck`, pas `Process`.
- Fonctions courtes (idéalement moins de 40 lignes), une seule responsabilité, retours anticipés plutôt qu'imbrication de plus de 3 niveaux.
- Fichiers de moins de 400 lignes en pratique, 800 au maximum.
- Pas de nombre magique : une constante nommée ou une option de configuration.
- Commentaires : ils expliquent le **pourquoi** (contrainte, piège, décision), jamais le quoi.

## 4. Données et immutabilité

- Les DTO et les résultats sont des `record` immuables.
- Les entités du domaine changent d'état **uniquement par leurs méthodes** (`ApplyCheck`, `Resolve`) : setters privés, constructeur privé pour EF Core, fabrique statique `Create` qui valide.
- Les erreurs de saisie ne sont pas des exceptions : elles remontent en `Result` avec une erreur par champ, convertie en `400 ValidationProblem`.
- Les exceptions sont réservées aux situations anormales. On n'avale jamais une exception sans la journaliser ou la transformer en résultat explicite.

## 5. Asynchronisme

- `async` de bout en bout, jamais `.Result` ni `.Wait()`.
- Chaque méthode qui peut attendre accepte un `CancellationToken` et le transmet.
- Le temps passe par `TimeProvider` (jamais `DateTime.UtcNow`) pour pouvoir le simuler dans les tests.
- Un `DbContext` n'est jamais partagé entre tâches parallèles : les sondes tournent en parallèle, les écritures en séquence.

## 6. Sécurité

- **Aucun secret dans le dépôt.** La clé de chiffrement vient de la variable d'environnement `Pulse__Accounts__EncryptionKey` ; la valeur de développement n'ouvre que l'environnement local.
- **Isolation entre clients** : toute méthode du stockage qui lit ou modifie des données d'un client prend l'identifiant de son organisation, tiré de la session et jamais de la requête. Une ressource d'un autre client est « introuvable » (404), jamais « interdite ». Toute nouvelle route protégée reçoit un test d'isolation dans `TenantIsolationTests`.
- Mots de passe : jamais en clair ni dans les journaux. Jetons de session et liens à usage unique : seul leur hachage est stocké. Secrets de webhooks : chiffrés avant la base, montrés une seule fois.
- Toute entrée est validée à la frontière (API) avant d'atteindre le domaine.
- **SSRF** : une cible n'est acceptée que si elle résout vers une adresse publique, sur un port autorisé. Le contrôle est refait à la connexion (anti « DNS rebinding »), et les redirections ne sont jamais suivies. Voir `IpAddressPolicy` et `HttpServiceProbe`.
- Les messages d'erreur stockés ou renvoyés sont courts et écrits à la main : jamais le message brut d'une exception réseau.
- Comparaison de secrets en temps constant (`CryptographicOperations.FixedTimeEquals`).
- La page de statut publique n'expose jamais l'adresse surveillée.
- Limitation de débit sur toute l'API, plus stricte sur les routes d'authentification ; en-têtes de sécurité sur l'API et le site web.
- Un message destiné à l'utilisateur est une constante de `ApiMessages` avec sa traduction dans `FrenchText` (un test échoue s'il en manque une).

## 7. Tests

- Cible : au moins **80 %** de couverture de lignes, avec priorité aux règles métier et aux frontières de sécurité.
- Nommer le comportement : `A_private_address_is_refused_at_connection_time_unless_allowed`.
- Structure Arrange / Act / Assert, un comportement par test.
- Pyramide : tests unitaires du domaine et de `IpAddressPolicy` (nombreux, rapides) ; tests d'intégration sur une vraie base SQLite en mémoire ; tests de l'API par HTTP avec `WebApplicationFactory` ; la sonde HTTP est testée contre un vrai serveur local.
- Pas de réseau réel ni d'horloge réelle dans les tests : résolveur DNS et horloge sont simulés.
- Un bug corrigé reçoit d'abord un test qui l'échoue.

## 8. Git et revue

- Commits en [Conventional Commits](https://www.conventionalcommits.org/) : `feat:`, `fix:`, `refactor:`, `test:`, `docs:`, `chore:`, `ci:`.
- Un commit = un changement cohérent qui compile et dont les tests passent.
- Avant de fusionner : build sans avertissement, tests verts, `dotnet format --verify-no-changes` propre.
- Revue : sécurité d'abord (entrées, secrets, accès aux données), puis lisibilité, puis style.

## 9. Définition de « terminé »

Une fonctionnalité est terminée quand :

- [ ] elle compile sans avertissement ;
- [ ] ses règles métier et ses cas d'erreur sont testés ;
- [ ] ses entrées sont validées et ses sorties ne fuient aucune donnée interne ;
- [ ] la documentation et le README sont à jour ;
- [ ] la CI est verte.

## 10. Commandes utiles

```bash
dotnet build                                   # compile tout, avertissements = erreurs
dotnet test --project tests/Pulse.Tests        # lance les tests (runner Microsoft.Testing.Platform)
dotnet format --verify-no-changes              # vérifie le formatage
dotnet ef migrations add <Nom> --project src/Pulse.Infrastructure --startup-project src/Pulse.Api --output-dir Persistence/Migrations
```
