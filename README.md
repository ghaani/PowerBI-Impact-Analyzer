# Power BI Impact Analyzer

A model-agnostic dependency inventory and impact-analysis script for Power BI semantic models, designed to run in **Tabular Editor 2**.

## Initial public baseline

**Version:** 0.9  
**Script:** `ImpactAnalyzer_Dynamic_v0.9.csx`

This release packages the validated 0.8.1 development state with consistent 0.9 release metadata. The dependency-analysis logic has not been intentionally changed as part of this packaging step.

## Current capabilities

- Discovers model tables, columns, and measures dynamically.
- Excludes Power BI internal objects whose runtime type is `CalculatedTable`.
- Builds dependency edges from dependencies exposed by `Model.AllMeasures` and `Measure.DependsOn`.
- Uses object type, owner, and object name to construct distinct registry keys.
- Runs breadth-first search (BFS) to report impact levels.
- Runs depth-first search (DFS) to enumerate simple impact paths, with a configurable path limit.
- Performs graph endpoint, registry, and BFS/DFS consistency checks.
- Includes synthetic checks for key boundaries, cycles, and DFS path-limit enforcement.
- Exports a timestamped text report to the user's Desktop under `ImpactAnalysis_Logs`.

## Compatibility and tested environment

The development validation recorded for this baseline used:

- Power BI Desktop RS — September 2026
- Tabular Editor — 2.29.0
- DAX Studio — 3.6.0

The script uses Tabular Editor's model object APIs. Compatibility with other Tabular Editor or Power BI versions has not yet been established.

## How to run

1. Open the target Power BI model in Power BI Desktop.
2. Open the model in Tabular Editor 2.
3. Open `ImpactAnalyzer_Dynamic_v0.9.csx` in the Advanced Scripting editor.
4. Execute the script.
5. Review the report displayed by Tabular Editor.
6. Find the exported report in `Desktop/ImpactAnalysis_Logs`.

The script does not require table, column, measure, PBIX, or database names to be configured in its core logic.

## Scope and known limitations

- The dependency graph is based on dependencies exposed through `Measure.DependsOn`.
- The inventory discovers tables, columns, and measures, but inventory discovery does not imply that every discovered object type has dependency edges.
- This release does **not** claim complete dependency coverage for every semantic-model expression type. In particular, dependencies expressed through calculated-column M expressions are outside the current graph extraction scope.
- Power BI internal `CalculatedTable` objects are intentionally skipped rather than classified as user tables.
- The initial validation was performed on one test model. A second, structurally different model is still needed to strengthen the model-agnostic validation claim.
- Synthetic tests validate selected graph behaviors; they do not prove that every real-world model edge case is covered.

## Release status

This is an initial public baseline, not a claim of universal production validation. Further model testing and feature work should be released as subsequent versions, with changes recorded in `Version_History.txt`.
