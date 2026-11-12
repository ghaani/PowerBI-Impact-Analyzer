# Power BI Impact Analyzer

A [Tabular Editor 2](https://github.com/TabularEditor/TabularEditor) script that answers one question before you change a model:

> **If I change this measure, column or table, what else is affected?**

The script reads the dependency information of the connected semantic model, builds a dependency graph, and reports, for every table, column and measure, everything that depends on it, directly or indirectly, with the dependency level and the shortest dependency path.

**Version:** 1.0  
**Script:** `ImpactAnalyzer.csx`

## What you get

Each run creates one folder, `Desktop\ImpactAnalysis\Run_<timestamp>\`, with two files:

| File | Purpose |
| --- | --- |
| `ImpactReport_<model>.html` | Self-contained interactive report. Open it in any browser. |
| `ImpactReport_<model>.csv` | Flat table for Excel or Power BI: one row per (target, affected object). |

**HTML report**

- **Explorer tab:** search any measure, column or table, filter by type, and optionally hide objects without dependents. Selecting an object shows what it depends on directly, its structural references (relationships, sort-by), and every affected object with its level and shortest path.
- **Paths & log tab:** the full dependency paths for the objects you selected before running the script, plus the run log (inventory, graph statistics, warnings, skipped objects).

**CSV columns**

| Column | Meaning |
| --- | --- |
| `Target` | The object you would change, e.g. `Sales[Amount]` |
| `TargetType` | `Measure`, `Column` or `Table` |
| `TotalAffected` | Number of objects affected by changing the target (direct and indirect) |
| `Affected` | One affected object |
| `AffectedType` | Type of the affected object |
| `Level` | `1` = depends directly on the target, `2` = depends on a level-1 object, and so on |
| `ShortestPath` | Shortest dependency chain from the target to the affected object |

Objects that nothing in the model depends on are still listed, with one row and an empty `Affected` column, so they can be filtered easily.

## What is analyzed

| Source | Covered |
| --- | --- |
| Measures (DAX) | Yes |
| Calculated columns (DAX) | Yes |
| Calculated tables (DAX) and the columns they generate | Yes |
| Sort-by-column links | Yes |
| Relationships | Reported as notes on the columns that use them (not as graph edges) |

Skipped on purpose: Power BI auto date/time tables (`LocalDateTable_*`, `DateTableTemplate_*`) and `RowNumber` columns.

## Requirements

- Tabular Editor 2 (free)
- A model to connect to: a Power BI Desktop model, or an SSAS Tabular / Azure Analysis Services database

## How to run

1. Open the model in Power BI Desktop and connect Tabular Editor 2 to it (for example through *External Tools*).
2. In Tabular Editor 2, open the **Advanced Scripting** tab and load `ImpactAnalyzer.csx`.
3. *(Optional)* Select one or more measures, columns or tables in the model tree before running. Their full dependency paths will appear in the **Paths & log** tab.
4. Run the script. The HTML report opens automatically when the export is finished.

### Power BI report connected live to SSAS

A PBIX that is connected live to SSAS does not contain the model, so Tabular Editor 2 shows *"This instance does not contain any databases"* when launched from Power BI Desktop. Connect directly to the SSAS database instead: **File → Open → From DB**, enter the server, choose the database, then run the script.

The analysis then covers the SSAS model itself. Measures defined only inside the report (report-level measures) are not part of that model and are not seen.

## Settings

Settings are variables at the top of the script.

| Setting | Default | Meaning |
| --- | --- | --- |
| `cfgDebug` | `false` | Run built-in synthetic self tests and add the results to the log tab |
| `cfgMaxPaths` | `2000` | Maximum number of DFS paths listed per selected target |
| `cfgDfsTargets` | empty | Objects to list full paths for, e.g. `"Sales[Total Sales]"` (in addition to the tree selection) |
| `cfgExportHtml` | `true` | Create the HTML report |
| `cfgExportCsv` | `true` | Create the CSV file |
| `cfgOpenFolder` | `true` | Open the report when finished |
| `cfgOutputRoot` | empty | Output folder. Empty = `Desktop\ImpactAnalysis` |

## How it works

- Every table, column and measure becomes a node. Keys are built from object type, owner and name, so objects with similar names cannot collide.
- An edge points from a dependency to the object that depends on it, using the dependency information that Tabular Editor exposes for measures, calculated columns and calculated tables.
- A breadth-first search from each object gives all affected objects, their level and the shortest path.
- A depth-first search lists complete dependency paths, only for the objects you select, with a configurable path limit.

## Scope and known limitations

- **Model level only.** Tabular Editor sees the semantic model, not the report. These are **not** analyzed: report visuals, filters, bookmarks and conditional formatting; row-level security filter expressions; calculation items; KPIs; Power Query (M) lineage.
- "No dependents" means that no other object **in the model** depends on it. It does not mean the object is unused in report visuals, so check the report before deleting anything.
- Relationships are listed as notes and are not followed as dependency edges.
- Objects or expressions for which Tabular Editor does not expose dependency information are listed in the log tab (unknown types and warnings) instead of being silently ignored.
- Very large models produce large reports: the size grows with the number of (object, affected object) pairs.

## Validation status

Tested on two Import-mode Power BI models and one model connected live to SSAS. The built-in synthetic tests (`cfgDebug = true`) cover key boundaries, cycle detection, DFS path limits and BFS/DFS consistency. This is not a claim of universal validation: other Tabular Editor or Power BI versions, and models with unusual structures, have not been verified. Issues and feedback are welcome.

<!--
Screenshots: after uploading the images to the repository, uncomment and adjust the paths.

![Explorer tab](docs/report-explorer.png)
![Paths and log tab](docs/report-paths-log.png)
-->
