# Horizun Hub and Horizun Revit MCP

Horizun Revit MCP is the open-source, organisation-neutral Revit automation
layer in the [Horizun Hub](https://horizunhub.com) ecosystem. The MCP provides
typed Revit operations, transport and safety guarantees; the Hub supplies the
specialised training, applications, catalogues and delivery workflows built on
top of that generic bridge.

The separation is deliberate. Client standards, project names, proprietary
catalogues and audit criteria are inputs or downstream workflows, not values
compiled into the public gateway. This keeps the server reusable and keeps
private delivery knowledge out of its source and release artifacts.

## What belongs where

| Capability | Horizun Revit MCP | Horizun Hub |
| --- | --- | --- |
| Read and query a Revit model | Yes | Can use it |
| Typed, verified model edits | Yes | Can orchestrate them |
| Generic audit engine and user-supplied rules | Yes | Can publish and manage rule packs |
| Generic BIM Standards Pack examples | Yes, optional and editable | Can extend and curate them |
| Project/company standards and proprietary catalogs | No | Yes, under the customer's governance |
| Advanced delivery workflows and report templates | Generic primitives only | Yes |
| Power BI dashboards and portfolio reporting | Technical integrations | Yes |
| Central policy, identity and enterprise administration | Local machine controls | Yes, when provisioned |
| Enterprise support and rollout services | Community/source distribution | Yes |

The dividing line is not a weaker bridge. It is a safety and governance boundary:
the public code executes explicit, reviewable inputs; it must not silently embed a
customer's policy or confidential project knowledge.
