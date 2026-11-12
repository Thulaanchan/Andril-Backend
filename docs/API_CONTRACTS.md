# API Contract Workflow

The published `contracts/openapi/andril-api.v1.yaml` currently documents **only the scaffold `/health` route**. User-facing routes are not implemented.

When adding a feature: document request/response types, module-context authorization, status codes, error envelopes, pagination and compatibility in the backend-owned OpenAPI contract. Review with Flutter and Angular owners. Old Android clients may coexist with newer backend releases; avoid breaking public endpoints without a migration plan.

Cross-repository publication/versioning mechanism remains **P-19 PENDING** in BRD v2.4-R1.
