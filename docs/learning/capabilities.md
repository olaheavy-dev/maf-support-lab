# Capability ownership

The sequential ticket order is the default learning path. These additional gates show the earlier implementation capabilities a task actually consumes.
A capability is available only after its owning ticket meets its acceptance criteria; the initial board does not claim any capability is already implemented.

| Ticket | Requires implementation from | Produces |
| --- | --- | --- |
| [L01](tickets/L01.md) | None | `version_plan`, `source_audit` |
| [L02](tickets/L02.md) | [L01](tickets/L01.md) | `support_contract_v1`, `synthetic_fixtures`, `risk_model` |
| [L03](tickets/L03.md) | [L01](tickets/L01.md), [L02](tickets/L02.md) | `dotnet_host`, `apphost`, `dev_identity_boundary`, `offline_mode` |
| [L04](tickets/L04.md) | [L03](tickets/L03.md), [L02](tickets/L02.md) | `offline_ci_dotnet` |
| [L05](tickets/L05.md) | [L03](tickets/L03.md), [L04](tickets/L04.md), [L02](tickets/L02.md) | `dotnet_order_query`, `dotnet_devui` |
| [L43](tickets/L43.md) | [L05](tickets/L05.md) | `dotnet_layered_modules` |
| [L06](tickets/L06.md) | [L05](tickets/L05.md), [L03](tickets/L03.md) | `dotnet_otel` |
| [L07](tickets/L07.md) | [L05](tickets/L05.md), [L06](tickets/L06.md), [L02](tickets/L02.md) | `dotnet_triage`, `dotnet_workflow_otel` |
| [L08](tickets/L08.md) | [L07](tickets/L07.md), [L03](tickets/L03.md), [L02](tickets/L02.md) | `dotnet_readonly_specialists` |
| [L09](tickets/L09.md) | [L08](tickets/L08.md), [L03](tickets/L03.md) | `dotnet_sessions` |
| [L10](tickets/L10.md) | [L09](tickets/L09.md), [L03](tickets/L03.md), [L02](tickets/L02.md) | `dotnet_approved_mutations` |
| [L11](tickets/L11.md) | [L10](tickets/L10.md), [L09](tickets/L09.md), [L02](tickets/L02.md) | `dotnet_durable_operations` |
| [L12](tickets/L12.md) | [L11](tickets/L11.md), [L02](tickets/L02.md), [L06](tickets/L06.md) | `dotnet_streaming` |
| [L13](tickets/L13.md) | [L03](tickets/L03.md), [L09](tickets/L09.md), [L11](tickets/L11.md) | `dotnet_access_regressions` |
| [L14](tickets/L14.md) | [L13](tickets/L13.md), [L10](tickets/L10.md) | `dotnet_injection_regressions` |
| [L15](tickets/L15.md) | [L11](tickets/L11.md), [L12](tickets/L12.md), [L06](tickets/L06.md) | `dotnet_resilience` |
| [L17](tickets/L17.md) | [L03](tickets/L03.md), [L02](tickets/L02.md) | `python_host`, `offline_ci_python`, `python_dev_identity` |
| [L18](tickets/L18.md) | [L17](tickets/L17.md), [L02](tickets/L02.md) | `python_readonly_specialists`, `python_triage`, `python_devui` |
| [L19](tickets/L19.md) | [L18](tickets/L18.md), [L17](tickets/L17.md), [L09](tickets/L09.md) | `python_sessions` |
| [L37](tickets/L37.md) | [L19](tickets/L19.md), [L10](tickets/L10.md), [L02](tickets/L02.md) | `python_approved_mutations` |
| [L38](tickets/L38.md) | [L37](tickets/L37.md), [L19](tickets/L19.md), [L11](tickets/L11.md), [L02](tickets/L02.md) | `python_durable_operations` |
| [L39](tickets/L39.md) | [L38](tickets/L38.md), [L18](tickets/L18.md), [L13](tickets/L13.md) | `python_access_regressions` |
| [L20](tickets/L20.md) | [L38](tickets/L38.md), [L39](tickets/L39.md), [L12](tickets/L12.md) | `python_streaming` |
| [L40](tickets/L40.md) | [L20](tickets/L20.md), [L18](tickets/L18.md), [L03](tickets/L03.md), [L06](tickets/L06.md) | `python_otel` |
| [L41](tickets/L41.md) | [L39](tickets/L39.md), [L37](tickets/L37.md), [L40](tickets/L40.md), [L14](tickets/L14.md) | `python_injection_regressions` |
| [L42](tickets/L42.md) | [L20](tickets/L20.md), [L40](tickets/L40.md), [L41](tickets/L41.md), [L38](tickets/L38.md), [L15](tickets/L15.md) | `python_resilience` |
| [L21](tickets/L21.md) | [L13](tickets/L13.md), [L14](tickets/L14.md), [L15](tickets/L15.md), [L39](tickets/L39.md), [L42](tickets/L42.md), [L20](tickets/L20.md), [L40](tickets/L40.md), [L41](tickets/L41.md) | `backend_contract_verified`, `application_projects_built` |
| [L29](tickets/L29.md) | [L21](tickets/L21.md), [L02](tickets/L02.md), [L01](tickets/L01.md) | `evaluation_projects`, `evaluation_specification` |
| [L16](tickets/L16.md) | [L21](tickets/L21.md), [L29](tickets/L29.md), [L15](tickets/L15.md), [L42](tickets/L42.md) | `evaluation_harness`, `dotnet_baseline`, `python_baseline`, `backend_parity` |
| [L30](tickets/L30.md) | [L16](tickets/L16.md), [L29](tickets/L29.md), [L01](tickets/L01.md) | `dotnet_native_evaluation` |
| [L31](tickets/L31.md) | [L16](tickets/L16.md), [L30](tickets/L30.md), [L17](tickets/L17.md) | `python_native_evaluation` |
| [L32](tickets/L32.md) | [L30](tickets/L30.md), [L31](tickets/L31.md), [L16](tickets/L16.md) | `quality_evaluation_lane` |
| [L33](tickets/L33.md) | [L32](tickets/L32.md), [L16](tickets/L16.md) | `evaluation_reports` |
| [L34](tickets/L34.md) | [L21](tickets/L21.md), [L33](tickets/L33.md) | `markdown_documentation` |
| [L25](tickets/L25.md) | [L16](tickets/L16.md), [L33](tickets/L33.md) | `fault_load_evidence` |
| [L26](tickets/L26.md) | [L25](tickets/L25.md), [L02](tickets/L02.md), [L04](tickets/L04.md), [L17](tickets/L17.md) | `security_review` |
| [L27](tickets/L27.md) | [L16](tickets/L16.md), [L25](tickets/L25.md), [L26](tickets/L26.md), [L34](tickets/L34.md) | `portfolio_runbook` |
| [L28](tickets/L28.md) | [L27](tickets/L27.md), [L16](tickets/L16.md) | `interview_evidence` |
