# CI/CD operations

The `build-test-deploy.yml` GitHub Actions workflow validates pull requests and deploys merged code using GitHub-hosted Linux runners.

## Delivery flow

| Event | Result |
| --- | --- |
| Pull request into `develop` or `main` | Restore, release build, all test suites, and Docker build validation |
| Push/merge to `develop` | Repeat validation, scan and publish an immutable image, deploy to `dev` |
| Push/merge to `main` | Repeat validation, scan and publish an immutable image, deploy to `prd` |
| Manual dispatch | Repeat validation and deploy the selected `dev` or `prd` environment |

Deployments use the image digest rather than a mutable tag. GitHub Actions concurrency cancels superseded development runs and serializes production runs.

## GitHub configuration

Create GitHub Environments named `dev` and `prd`. Define these variables in each environment:

| Variable | Example | Purpose |
| --- | --- | --- |
| `AZURE_CLIENT_ID` | Application/client UUID | OIDC deployment identity |
| `AZURE_TENANT_ID` | Microsoft Entra tenant UUID | Azure login |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription UUID | Azure login |
| `ACR_LOGIN_SERVER` | `acrolgadevmalaysiaweste.azurecr.io` | Image registry |
| `AZURE_RESOURCE_GROUP` | `rg-olga-dev-malaysiawest` | Container App resource group |
| `CONTAINER_APP_NAME` | `ca-olga-nlp-api-dev` | Deployment target |
| `NLP_WORKER_CONTAINER_APP_NAME` | `ca-olga-nlp-worker-dev` | Worker deployment target |
| `AZURE_OPENAI_ENDPOINT` | `https://<resource>.openai.azure.com/` | Azure OpenAI resource endpoint |
| `AZURE_OPENAI_DEPLOYMENT_NAME` | `olga-text-embedding-3-small` | Deployment backed by `text-embedding-3-small` |
| `AZURE_OPENAI_MODEL_VERSION` | `azure-text-embedding-3-small-1536-v1` | Stable application model version; must match the active database record |
| `AZURE_OPENAI_MANAGED_IDENTITY_CLIENT_ID` | Runtime identity client UUID | User-assigned managed identity; leave empty for a system-assigned identity |

No long-lived Azure client secret is required. This repository uses GitHub's immutable OIDC subject format. Configure the deployment identity's federated credentials with issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`, and these exact subjects:

- `dev`: `repo:Ol-gaTechnologies@306667340/olga-nlp-api@1356082344:environment:dev`
- `prd`: `repo:Ol-gaTechnologies@306667340/olga-nlp-api@1356082344:environment:prd`

The `job_workflow_ref` claim is not the federated credential subject. Grant the deployment identity only `AcrPush` on the relevant registry and the minimum Container App update permission on the target resource group or app.

Protect `prd` with required reviewers, prevent self-review, restrict it to `main`, and disable administrator bypass. Restrict `dev` to `develop`. Keep environment variables scoped to their environment.

Protect `main` and `develop` with pull requests, at least one approval, resolved conversations, dismissal of stale approvals, no force pushes/deletions, and the required `Build and test` status check. Require branches to be current before merge. Configure a ruleset if repository administrators must also follow the policy.

## Development runtime

The `dev` deployment runs in `malaysiawest` with these provisioned resources:

| Resource | Name |
| --- | --- |
| Resource group | `rg-olga-dev-malaysiawest` |
| Container registry | `acrolgadevmalaysiaweste` |
| Container Apps environment | `cae-olga-dev-devmalaysiaweste` |
| NLP API Container App | `ca-olga-nlp-api-dev` |
| NLP worker Container App | `ca-olga-nlp-worker-dev` |
| Azure OpenAI deployment | `olga-text-embedding-3-small` (`text-embedding-3-small`, 1,536 dimensions) |
| PostgreSQL Flexible Server | `psql-olga-devmalaysiaweste.postgres.database.azure.com` |
| PostgreSQL database | `olga_connect_dev` |
| Key Vault | `kv-olga-devmalaysiaweste` |
| Runtime managed identity | `id-olga-nlp-dev` |

The PostgreSQL server has public network access disabled. It uses the delegated subnet `snet-postgresql` and private DNS zone `private.postgres.database.azure.com`. The Container Apps environment uses `snet-container-apps` for VNet integration. The NLP API has internal-only ingress.

Configure both containers with `ConnectionStrings__PostgreSql` as a Key Vault-backed Container Apps secret reference. The connection must use the server FQDN above, database `olga_connect_dev`, port `5432`, and TLS certificate verification. The deployment workflow configures the deployed API with `EmbeddingProvider=Azure` and `EmbeddingProcessing__Mode=Queued`; local Development remains `Fake`/`Inline`. The worker always requires the `AzureOpenAI` settings and validates them before it starts. The stable `AZURE_OPENAI_MODEL_VERSION` value must identify an active `nlp.nlp_model_version` row whose provider, deployment, dimensions, and preprocessing version are respectively `AZURE_OPENAI`, the configured deployment, `1536`, and `normalizer-v1`.

Assign the runtime managed identity the `Cognitive Services OpenAI User` role on the Azure OpenAI resource. Give the same identity access to the PostgreSQL/Key Vault configuration used by both containers. For the initial anonymous MVP, optionally set `Mvp__DefaultMemberId`; it defaults to `A123`. `Diagnostics__IncludeExceptionDetails` includes `stack_trace` in unhandled-error responses when enabled; the deployment workflow enables it in `dev` and disables it in `prd`, while the root exception message is returned in both environments. Requests may select any member with `X-Member-Id`, so this deployment must not be treated as suitable for public or sensitive member data. Never place secret values in GitHub variables or workflow YAML.

The current registry uses the non-ABAC permission model. Grant the GitHub deployment identity `AcrPush` on `acrolgadevmalaysiaweste`, and grant the runtime managed identity only `AcrPull` on that registry. The runtime identity also needs permission to read the referenced Key Vault secrets. PostgreSQL schema creation and upgrades must run from a trusted host with network access to the private database endpoint.

The API image listens on port `8080`. Configure Container Apps ingress and probes for that target port. Use `/health` as the process liveness endpoint and `/ready` as the readiness endpoint; `/ready` verifies PostgreSQL connectivity. The worker has no ingress and must have at least one always-on replica so queued work is continuously consumed. Published API and worker images use immutable commit tags and deployment resolves both to their digests. The workflow waits for the API revision to become healthy and for the worker revision to reach the running state.

## Runner choice

GitHub-hosted `ubuntu-latest` runners are suitable for build, test, image publication, and Azure Container Apps deployment. Use a self-hosted runner only for private-network work such as database migrations against the private PostgreSQL endpoint. Put such a runner in a dedicated runner group, use ephemeral instances, allow only selected repositories, and never run untrusted pull-request code on it.

## Rollback

Container Apps keeps revisions, and every deployment is traceable to a commit SHA and digest. Roll back by activating the last known-good revision in Azure, then revert the offending commit so source control and runtime state converge. Do not repoint or reuse an existing image tag.
