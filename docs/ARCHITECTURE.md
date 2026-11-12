# ANDRIL Backend Architecture — Proposal

**Status:** proposed folder/technical layering, not master BRD approval.

- One ASP.NET Core modular monolith serves separately deployed Android and Angular Admin clients.
- `Andril.API`: HTTP controllers / hubs and composition root. **No domain business decisions in controllers.**
- `Andril.Shared`: dependency-free common abstractions, results, identities and policy contracts.
- `Andril.Common`, `Andril.Learning`, `Andril.Hobbies`, `Andril.Social`, `Andril.Groups`, `Andril.Communication`, `Andril.Economy`, `Andril.AI`: domain/application modules.
- `Andril.Persistence`: implementations of repository interfaces owned by feature modules. Feature modules must not depend on Persistence or API.
- `Andril.API/Integrations/Mapbox`: optional reserved backend-side adapter, **not required for Flutter Android Mapbox map rendering**.
- Authentication and privacy enforcement are essential before any user data endpoint is published.

See ANDRIL BRD v2.4-R1 §§02, 07–09, 13–17 and C# Architecture Blueprint v1.1. All feature IDs use current normalized IDs in those documents.
