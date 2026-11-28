# API Contract Workflow

The base `contracts/openapi/andril-api.v1.yaml` documents the scaffold `/health` route.
Implemented feature routes also have dedicated contracts and runtime OpenAPI; the base
file is not a complete runtime inventory. See [the Learning API inventory](M1_Learning_API_Inventory.md)
for implemented opt-ins, DTOs, authorization, frontend integration targets and explicit holds.

When adding a feature: document request/response types, module-context authorization, status codes, error envelopes, pagination and compatibility in the backend-owned OpenAPI contract. Review with Flutter and Angular owners. Old Android clients may coexist with newer backend releases; avoid breaking public endpoints without a migration plan.

Cross-repository publication/versioning mechanism remains **P-19 PENDING** in BRD v2.4-R1.
