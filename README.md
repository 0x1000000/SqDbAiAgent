# SqDbAiAgent Demo

This repository contains a demo application that shows how the [SqExpress](https://github.com/0x1000000/SqExpress) library can be utilized as a tool for an AI agent.

The application sends database metadata to the agent, accepts SQL proposed by the agent, parses that SQL with SqExpress, validates it, optionally rewrites it with security predicates, and only then sends the resulting query to SQL Server.

## Contents

- [What the demo shows](#what-the-demo-shows)
- [HarborFlow demo database](#harborflow-demo-database)
- [HarborFlow schema layout](#harborflow-schema-layout)
- [HarborFlow entity overview](#harborflow-entity-overview)
- [Security model in the demo](#security-model-in-the-demo)
- [Configuration](#configuration)
- [LLM provider choice](#llm-provider-choice)
- [Example conversation](#example-conversation)
- [Files of interest](#files-of-interest)

## What the demo shows

- It provides a list of tables, columns, and relationships to the agent.
- The agent is supposed to generate SQL code.
- The SQL is parsed by SqExpress.
- SqExpress validates that the provided T-SQL is syntactically correct for the supported parser surface.
- SqExpress validates that only existing and allowed tables and columns are used in the query.
- SqExpress validates that the query is read-only when the active security context has only read access.
- If the security policy implies row-level visibility restrictions, SqExpress adds additional security predicates to the expression that is sent to the database, so the user does not see data that should not be visible.
- The security logic is applied per database. In this demo, that logic is implemented by [HarborFlowSecurityFilter.cs](./SqDbAiAgent.Console/SecurityFilters/HarborFlow/HarborFlowSecurityFilter.cs).

## HarborFlow demo database

This repository also contains a demo SQL Server database, `HarborFlow`, which illustrates how database-specific security policy can be applied.

HarborFlow is a small operational sales and fulfillment domain. It models:

- branches
- employees
- customers
- product categories and products
- warehouses and inventory balances
- sales orders and sales order lines
- shipments and shipment lines
- invoices and payments
- application users and security access mappings

## HarborFlow schema layout

The demo schema is intentionally split into three areas:

- `ref`
  Reference and master data that is safe to expose to the AI agent for query generation. This schema contains business entities such as branches, employees, customers, products, warehouses, and inventory balances.
- `ops`
  Operational transaction data that the AI agent can read. This schema contains sales orders, order lines, shipments, shipment lines, invoices, and payments.
- `sec`
  Security and permission data that is not intended for normal AI prompt exposure. This schema contains application users and access-mapping tables used by the security filter.

## HarborFlow entity overview

The main relationships in the demo database are:

- a branch owns employees, customers, and warehouses
- a customer belongs to a branch and may prefer a warehouse
- a product belongs to a product category
- inventory balance tracks stock per warehouse and product
- a sales order belongs to a branch, customer, and sales representative
- a sales order has one or more sales order lines
- each sales order line points to a product and warehouse
- a shipment belongs to a sales order and warehouse
- a shipment has shipment lines linked to sales order lines
- an invoice belongs to a sales order
- a payment belongs to an invoice
- an application user may be linked to an employee and may have branch/customer visibility through security tables

## Security model in the demo

The demo uses HarborFlow-specific security rules to show how SqExpress expressions can be modified before execution.

At a high level:

- the agent works only with the public part of the schema
- the app validates the generated query against the allowed table model
- the HarborFlow security filter inspects the query expression
- for every visited query specification, it adds row-level `EXISTS` predicates when the selected user is restricted
- the final secured expression is exported to SQL and executed

This keeps the security policy in application code instead of in the prompt, and allows the same general agent flow to be reused for different databases with different security filters.

## Configuration

The console application is configured through [appsettings.json](./SqDbAiAgent.Console/appsettings.json).

For local development overrides, you can also use `SqDbAiAgent.Console/appsettings.Development.json`. This file is git-ignored so it is a safe place for machine-local settings such as API keys. The application loads it automatically after the base `appsettings.json`, so values in it override the shared defaults without requiring extra environment setup. A tracked starter file is available at [appsettings.Development.example.json](./SqDbAiAgent.Console/appsettings.Development.example.json).

The main configuration areas are:

- `App`
  General application behavior such as the database connection string, active LLM provider, retry limits, prompt-size limits, and logging.
- `Ollama`
  Settings for a local Ollama endpoint, including base URL, model name, and timeout.
- `OpenRouter`
  Settings for OpenRouter, including base URL, API key, model name, and timeout.

### `App` section

- `ConnectionString`
  SQL Server connection string used by the demo application.
- `LlmProvider`
  Selects the active LLM provider. Supported values are currently `Ollama` and `OpenRouter`.
- `LlmLogFilePath`
  Path to the file where raw LLM request and response payloads are written when interaction logging is enabled.
- `MaxAgentSteps`
  Maximum number of agent loop steps for a single user request before the app stops the loop.
- `MaxAgentVisibleCells`
  Maximum number of result cells rendered into the bounded Markdown table that is sent back to the agent after query execution.
- `MaxSqlFixAttempts`
  Maximum number of SQL approval and repair attempts before the app gives up.
- `MaxSqlRuntimeFixAttempts`
  Maximum number of additional repair attempts after SQL Server rejects an approved query at execution time.
- `MaxClassificationAttempts`
  Maximum number of attempts to get a valid structured response from the model for agent-action or message-analysis calls.
- `ThinkAfterAttempt`
  In `Auto` reasoning mode, low reasoning is enabled only after this attempt number has already passed.
- `Reasoning`
  Global reasoning behavior for LLM calls. Supported values are:
  - `Auto`
    Start with no reasoning and enable low reasoning only on later retries.
  - `Enabled`
    Always request reasoning.
  - `Disabled`
    Never request reasoning.
- `MaxFixResponseAttempts`
  Maximum number of attempts to get one valid SQL-fix JSON response for a single repair step.
- `MaxUnchangedSqlResponses`
  Maximum number of times the fixer may return the same SQL text again before the repair step is aborted.
- `MaxPromptChars`
  Character budget for one prompt, including system prompt, current request, history, and tool-result context.
- `PromptSafetyChars`
  Reserved character budget to reduce the risk of overrunning the effective prompt limit.

### `Ollama` section

- `BaseUrl`
  Base address of the local Ollama server, for example `http://localhost:11434`.
- `Model`
  Name of the local Ollama model to use.
- `TimeoutSeconds`
  HTTP timeout for Ollama requests.

### `OpenRouter` section

- `BaseUrl`
  Base address of the OpenRouter API, normally `https://openrouter.ai/api/v1`.
- `ApiKey`
  OpenRouter API key.
- `Model`
  OpenRouter model identifier.
- `Referer`
  Optional HTTP referer header. This is mainly useful when identifying a hosted app or project page.
- `Title`
  Optional OpenRouter request title.
- `TimeoutSeconds`
  HTTP timeout for OpenRouter requests.

### LLM provider choice

For debugging and local experimentation, it makes sense to use a local LLM through Ollama.

In practice, `qwen3.5:4b` is a reasonable starting point for this demo. It provides acceptable results on consumer hardware and was usable on an RTX 3070 during development, especially for simpler requests and validation flow testing.

For better query quality, especially on harder analytical requests, a premium cloud LLM is usually required. That is why this project also includes an OpenRouter client, so the same application can be pointed at a stronger hosted model when higher-quality results are needed.

In short:

- use `Ollama` for local debugging, development, and fast iteration
- use `OpenRouter` when you want access to stronger hosted models

## Example conversation

Below is a shortened example of how the demo application can interact with a user. This example was captured with full access selected at startup by entering `0` as the user id.

```text
System:
Enter user id or /exit: 0
No user was selected. The app will show all available data.
```

```text
User:
What can you do?
```

```text
LLM:
I can help you write and run read-only SQL queries against the HarborFlow database.
For example:
1) List recently created sales orders (by OrderDate)
2) Find the top customers by total invoiced amount this year
3) Show sales by product category for a date range
4) Report invoice totals and payment totals by invoice status
5) Rank sales reps by number of orders or total sales in a period
6) Show shipment counts and quantities by carrier and shipment date
```

```text
User:
Show me all active branches
```

```text
LLM:
```

| BranchId | BranchCode | BranchName | Region | City | IsActive | CreatedUtc |
|---|---|---|---|---|---|---|
| 3 | CAL | Calgary Branch | Alberta | Calgary | True | 29.03.2026 20:56:39 |
| 2 | OTT | Ottawa Branch | Ontario | Ottawa | True | 29.03.2026 20:56:39 |
| 1 | TOR | Toronto Branch | Ontario | Toronto | True | 29.03.2026 20:56:39 |

```text
LLM:
Rows: 3
Active branches found: Calgary Branch (Alberta, Calgary), Ottawa Branch (Ontario, Ottawa), and Toronto Branch (Ontario, Toronto). Want results limited to a specific region or city?
```

```text
User:
How many people work in the branches?
```

```text
LLM:
```

| TotalEmployees |
|---|
| 13 |

```text
LLM:
Rows: 1
There are 13 employees working in branches (employees with a non-null BranchId).
```

```text
User:
Show 5 recent sales orders
```

```text
LLM:
```

| SalesOrderId | OrderNumber | OrderDate | OrderStatus | CurrencyCode | Notes |
|---|---|---|---|---|---|
| 40 | SO-2026-0040 | 12.03.2026 0:00:00 | Approved | CAD | Lab headset refresh |
| 39 | SO-2026-0039 | 11.03.2026 0:00:00 | Submitted | CAD | Archive workstation add-on |
| 38 | SO-2026-0038 | 10.03.2026 0:00:00 | Draft | CAD | Router spare pool |
| 37 | SO-2026-0037 | 09.03.2026 0:00:00 | Closed | CAD | Follow-up branch order |
| 36 | SO-2026-0036 | 08.03.2026 0:00:00 | Closed | CAD | Late quarter accessories |

```text
LLM:
Rows: 5
Recent 5 sales orders (newest first): SO-2026-0040 (Approved, CAD, 2026-03-12), SO-2026-0039 (Submitted, CAD, 2026-03-11), SO-2026-0038 (Draft, CAD, 2026-03-10), SO-2026-0037 (Closed, CAD, 2026-03-09), SO-2026-0036 (Closed, CAD, 2026-03-08). Which date/status range should I pull next?
```

## Files of interest

- [db_create.sql](./db_create.sql)  
  Creates and seeds the HarborFlow demo database.
- [Program.cs](./SqDbAiAgent.Console/Program.cs)  
  Application startup and provider wiring.
- [DbChatSession.cs](./SqDbAiAgent.Console/Services/DbChatSession.cs)  
  Main agent loop.
- [SqlApprovalSession.cs](./SqDbAiAgent.Console/Services/SqlApprovalSession.cs)  
  SQL approval and repair loop.
- [MessageAnalyzeSession.cs](./SqDbAiAgent.Console/Services/MessageAnalyzeSession.cs)  
  LLM-based message classification and topic detection.
- [HarborFlowSecurityFilter.cs](./SqDbAiAgent.Console/SecurityFilters/HarborFlow/HarborFlowSecurityFilter.cs)  
  HarborFlow-specific row-security logic.
