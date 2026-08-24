# Microsoft Agent Framework Comparison

## Scope

The Microsoft Agent Framework implementation is available beside the existing custom runtime. Select it with:

```json
{
  "App": {
    "AgentRuntime": "MicrosoftAgentFramework"
  }
}
```

The framework runtime uses `AIAgent`, `AgentSession`, Microsoft.Extensions.AI function invocation, the OpenAI-compatible SDK adapter for OpenRouter, and OllamaSharp for Ollama. It does not use `OllamaClient` or `OpenRouterClient`. Classification and SQL repair still use the existing application algorithms through an `ILlmClient` adapter over `IChatClient`.

Both runtimes use the same `ValidatedSqlExecutor`. Model SQL must still pass SQL approval, SqExpress parsing and allow-list validation, read-only checks, HarborFlow security rewriting, and SQL Server execution. Runtime SQL errors are returned to the existing repair loop.

## Real-session comparison

The comparison was run on August 19, 2026 with:

- OpenRouter model `z-ai/glm-4.7-flash`
- local SQL Server `HarborFlow`
- startup user `0` for unrestricted demo visibility
- full tool scope
- 10 sessions per runtime and 5 user messages per session
- the same shipment, order, invoice, inventory, customer, employee, product, ambiguity, off-topic, and monthly-trend scenarios

After aligning reasoning behavior, all 20 sessions processed all 5 messages. Results are nondeterministic and should be treated as a behavioral sample, not a performance guarantee.

| Metric | Custom | Microsoft Agent Framework |
|---|---:|---:|
| Sessions / user messages | 10 / 50 | 10 / 50 |
| Total elapsed time | 502.4 s | 770.9 s |
| Mean session time | 50.2 s | 77.1 s |
| Median session time | 47.7 s | 46.4 s |
| Model requests | 224 | 297 |
| Rendered result tables | 29 | 31 |
| SQL approval failures | 5 | 75 |
| SQL runtime failures | 0 | 28 |
| SQL returned as assistant text | 12 | 0 |

The framework had slightly better median latency and perfect tool-envelope adherence in this sample: GLM never emitted SQL as Markdown or ordinary assistant text. Its mean latency and repair counts were worse because three difficult scenarios became outliers. In those cases GLM repeatedly submitted overcomplicated SQL, especially `UNION`, alias, and date-expression variants. One monthly-shipment session produced no result table.

The benchmark exposed that framework function invocation has its own internal loop. The final implementation now binds `FunctionInvokingChatClient.MaximumIterationsPerRequest` to `App:MaxAgentSteps`; the aggregate table above predates that final cap and therefore records the behavior that motivated it. Focused post-cap regression runs are recorded below.

### Focused post-cap regression

The three Framework scenarios with the largest repair loops were repeated after adding the native function-invocation cap:

| Scenario | Before cap | After cap | Model requests after cap | Outcome after cap |
|---|---:|---:|---:|---|
| Inventory risk | 258.1 s | 35.6 s | 19 | Returned three result tables, then asked an unnecessary clarification instead of the requested final summary. |
| Ambiguous sales performance | 173.1 s | 22.0 s | 20 | Returned and summarized the branch comparison. |
| Monthly shipment trend | 93.2 s | 13.6 s | 15 | Terminated quickly but still failed to produce a result table. |

The cap prevents a weak model from turning one user request into a long sequence of SQL submissions. It improves cost and latency, not model reasoning quality; failed or unnecessary clarification remains visible to the caller rather than being hidden behind more automatic retries.

## Assessment

Microsoft Agent Framework is viable here and provides a cleaner standard agent/session abstraction, native tool orchestration, and provider adapters without custom wire-protocol code. It does not replace the valuable parts of this project: SQL approval, SqExpress parsing, the allow-list, security rewriting, execution controls, and model-specific recovery remain application responsibilities.

Keep `Custom` as the default for now. In this GLM/OpenRouter sample it was more predictable and cheaper in model calls, while the Framework runtime was stronger at tool-contract adherence. Keeping both behind `AgentRuntime` provides a useful reference implementation and allows future models/framework versions to be compared without changing the database safety layer.

## Local Ollama profiling

A follow-up profile used local Ollama `qwen3.5:4b`, full tools, disabled reasoning, and the same three-message shipment flow for both runtimes. Both produced the same correct results and made exactly 11 model calls.

The initial timings were 12.85 seconds for `Custom` and 15.88 seconds for `MicrosoftAgentFramework`. Wire timing showed that Ollama generated the first classifier response in 0.64 seconds while the Framework transport took 2.68 seconds. The Framework factory had created separate provider clients and connection pools for model discovery, classification/repair, and the main agent. Each new `localhost` connection incurred another roughly two-second delay on this machine.

Two changes removed the gap:

- The Framework factory now reuses one provider client and connection pool across discovery, structured operations, and the main agent.
- The local development override uses `http://127.0.0.1:11434`; on this machine `/api/tags` averaged about 4 ms through `127.0.0.1` versus 2,072 ms through `localhost` because of address-family fallback.

After both fixes, the same flow completed in 9.70 seconds with `Custom` and 10.28 seconds with `MicrosoftAgentFramework`. The remaining 0.58-second difference is normal run-to-run/model variation rather than an additional framework tool loop.
