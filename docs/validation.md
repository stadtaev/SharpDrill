# Research increment validation

The initial increment uses the existing production projects and a shared xUnit test project. Validation combines extracted fixture facts, independently assembled Core snapshots, in-memory semantic compilations, and fresh CLI processes.

| Requirement | Evidence |
|---|---|
| Controller → application → infrastructure source bindings | `Source_bindings_link_projects_and_preserve_locations` |
| Matching, unrelated and incompatible catches; rethrow/replacement | `The_catch_around_each_call_site_decides_the_fate_of_the_repository_throw` parameter cases |
| Caught/miscaught from the shared propagation: outcomes across actions, catches above recursion | `A_miss_reports_every_fate_of_what_got_past_it_across_actions`, `A_catch_above_recursion_is_found_with_an_acyclic_example` |
| One call-site resolution for every traversal | `Call_sites_resolve_to_one_target_kind_shared_by_all_traversals` |
| Subsequent rethrow/replacement interception and uncertainty | `Changed_exception_retains_subsequent_interception`, `Changed_exception_retains_subsequent_filter_uncertainty` |
| Throws, throw expressions, rethrows, filters and region evidence | `Throw_expression_rethrow_and_filter_facts_are_retained`, `Protected_regions_filters_and_throw_runtime_precision_are_preserved` |
| Static vs exact constructed type, including explicit casts | `Cast_throw_retains_static_type_and_exact_construction_separately`, `Exact_construction_beneath_base_conversion_matches_derived_handler_and_preserves_static_type` |
| Assembly-aware and uncertain type matching | `Same_display_name_in_different_assemblies_does_not_match_handler`, `Different_constructed_generic_arguments_are_not_false_matching_handlers`, `Unbound_exception_type_does_not_skip_typed_handler` |
| Controller conventions and inherited exclusions | `Controller_convention_excludes_abstract_generic_and_inherited_opt_outs` |
| Interface boundaries, recursion that is not uncertainty, declared bindings | `Interface_without_implementation_is_uncertain_and_recursion_alone_is_not` |
| Await observation and unsupported async propagation | `Direct_await_observation_and_aliased_task_unknown_are_distinguished`, `Task_exception_requires_direct_observation_before_caller_handler`, `Async_void_and_custom_awaitable_propagation_are_unknown` |
| Skill tip: install or update when needed, quiet otherwise and for redirected output | `Skill_hint_asks_for_an_install_or_an_update_and_otherwise_stays_quiet`, `Strict_adds_a_third_party_count_and_leaves_framework_calls_out` |
| Agent skill install: repository root, refresh, foreign files kept, per-user folders, commands and codes named | `Skill_installs_at_the_repository_root_refreshes_its_own_file_and_keeps_foreign_ones`, `Skill_user_installs_for_copilot_and_claude_code` |
| Strict mode: third-party calls with no catch on the way, framework/third-party classification | `Strict_reports_third_party_calls_that_no_catch_meets_whatever_the_catch_type`, `Referenced_assemblies_outside_the_shared_framework_are_third_party`, `Strict_adds_a_third_party_count_and_leaves_framework_calls_out` |
| Escapes covered by exception middleware or global exception filters, each naming its registration | `Exceptions_name_the_global_handler_that_receives_an_escape`, `Global_handlers_cover_actions_of_the_registering_project_and_of_projects_it_references`, `Global_mvc_exception_filters_are_boundary_handling_and_other_filters_are_not` |
| Library calls, compiler-provided members, candidate following and de-duplication | `ExceptionScopeTests`, `Fixture_uncertainty_is_only_what_the_analysis_cannot_decide`, `Compiler_provided_members_are_synthesized_while_written_bodies_and_declarations_keep_their_status`, `Foreach_and_using_record_their_implicit_calls_and_only_the_async_forms_remain_gaps` |
| Exception views: every throw, caught (thrown/caught/outcome), miscaught (non-matching try) | `Default_lists_every_throw_with_method_type_and_project`, `Caught_reports_where_thrown_and_where_caught_with_the_handler_outcome`, `Miscaught_reports_the_try_whose_clauses_do_not_match_and_what_happens_next` |
| Project references, MSBuild settings and Mermaid export | `Project_map_follows_msbuild_references_and_reports_settings`, `Projects_print_the_reference_tree_with_build_settings`, `Framework_selection_uses_conditional_assets_and_rejects_ambiguity` |
| Constructors/accessors/operators and nested callable ownership | `Expression_bodied_property_and_base_accessor_have_source_and_direct_dispatch`, `Operators_conversions_and_type_only_declarations_are_not_lost`, `Nested_callables_own_calls_without_invented_parent_edges` |
| Partial/reduced generic binding and dependencies | `Partial_methods_reduced_extensions_and_nested_generic_dependencies_keep_identity` |
| Generated exclusion and evidence | `Generated_exclusion_has_explicit_evidence` |
| SDK .csproj/.sln/.slnx loading and framework selection | `Project_input_includes_source_references_and_cross_project_bindings`, `Conventional_solution_input_preserves_cross_project_source_facts`, `Framework_selection_uses_conditional_assets_and_rejects_ambiguity`, research fixture loading |
| No implicit restore, source preservation and output safeguard | `Missing_restoration_is_rejected_without_creating_assets_or_changing_source`, `Output_refuses_to_overwrite_an_input_file` |
| Implementation candidates, field/property dependencies | `Implementation_candidates_cover_interfaces_overrides_and_accessors`, `Field_and_property_dependencies_carry_kind_and_declaration_evidence_without_backing_fields`, `Interface_dispatch_has_a_declared_member_and_implementation_facts_are_candidates_only` |
| Lambda/local-function identity (no collapsed bodies) | Task 11 extraction tests in `ExtractionTests` |
| Program index lookups and ordering | `IndexTests` |
| Reason codes consistent with statuses | `Every_fixture_example_has_a_reason_code_consistent_with_its_final_status`, reason assertions in `CoreTests` |
| Layer precedence, graph edges, allowed-dependency rule (library) | `ArchitectureTests` |
| Per-command options, removed commands and options | `Invalid_requests_fail_before_loading`, `Removed_commands_are_rejected`, `Removed_options_are_rejected_before_loading`, `Option_of_another_command_names_the_command` |
| Controller classification in Core | `ConventionTests`, `Core_controller_classification_matches_extraction_on_edge_cases` |
| Argument failures before loading | `Invalid_options_fail_before_loading` |

Reproduce with `dotnet build tests/Fixtures/ResearchSolution/ResearchSolution.slnx`, `dotnet build sharpview.slnx --no-incremental`, and `dotnet test sharpview.slnx`. Reports are in `docs/samples`.

The self-analysis smoke check (on the frozen pre-refactor source) loaded SharpView's four projects and exported 953 callable facts, 2,870 call sites, 32 throw sites, four catches, 22 implementation candidates and 93 explicitly recorded gaps; before the lambda/local-function identity fix it reported 834 callables and 2,649 call sites because same-shaped lambda bodies collapsed into one fact. This checks loading/extraction on the host's own code; it does not replace an external Web API experiment.

## Regression comparison (consumer model increment)

Every task of the consumer-model plan was compared against a frozen baseline (the pre-refactor commit, built separately): 13 CLI runs covering `controllers`, `exceptions`, `chains` (declared and direct), `methods`, `inspect` (text and JSON) on the fixture, and `inspect`/`chains`/`methods`/JSON on a frozen copy of SharpView's own source. Final result: every fixture text report is byte-identical to the baseline. Intended differences only: JSON schema 3 (string IDs, new facts, `controllerActions` computed instead of stored); self-analysis counts grew because overridden members are registered (+8 callables), lambda/local bodies are no longer collapsed (+103 bodies) and field/property dependencies were added. Old-vs-new runs with a `namespace:<TypeName>` mapping equal new runs with `type:<TypeName>`.

## External Web API attempt (2026-10-17)

Input: the locally supplied `api-osa.zip`, extracted into a temporary directory outside this repository, excluding
Git metadata and existing build outputs. The archive and its source files were not modified. No external source or
package-feed credentials are included in this repository.

Source inventory (not SharpView analysis results): 13 source projects, 355 C# source files, and 12 files named
`*Controller.cs`, including a base controller. This is a useful validation target for cross-project call relationships
and interface boundaries.

Attempted prerequisite:

```sh
dotnet restore src/dunnhumby.Osa.Api/dunnhumby.Osa.Api.csproj --disable-parallel
```

The configured Artifactory source returned `NU1301` / HTTP 404 for package lookups. The restore was bounded to 50 seconds.
The API also references private packages, so replacing the feed with nuget.org would not establish a faithful build.
No semantic analysis accuracy, coverage percentages, or performance conclusions can be claimed from this attempt.

To finish: supply a working package-source configuration or a restored checkout, run `projects`, the four `exceptions` views
and `diagram` (with and without `--declared-only`) on the API project. Record extraction gaps,
dispatch/missing-body boundaries, limit flags, and exception reason counts. Check representative controller-to-service
paths and exception witnesses against source before judging the reports useful. Keep raw reports outside the repository
unless the application's source identifiers are intended for publication.
