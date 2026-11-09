// ============================================================
// POWER BI IMPACT ANALYZER
// VERSION 0.9 - INITIAL PUBLIC BASELINE
// Tabular Editor 2.29.0
// ============================================================

using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;

// ------------------------------------------------------------
// 1. CONFIGURATION AND STATE
// ------------------------------------------------------------

int maxPaths = 10000;
var report = new StringBuilder();
var registry = new Dictionary<string, string>();
var keyTypes = new Dictionary<string, string>();
var keyOwners = new Dictionary<string, string>();
var forwardGraph = new Dictionary<string, HashSet<string>>();
var inventory = new List<string>();
var unknownTypes = new List<string>();
var missingOwners = new List<string>();
var collisions = new List<string>();
int registeredCount = 0;

int discoveredTableCount = 0;
int discoveredColumnCount = 0;
int discoveredMeasureCount = 0;

// ------------------------------------------------------------
// 2. KEY AND OBJECT HELPERS
// ------------------------------------------------------------

Func<string, string, string, string> makeKey =
    delegate(string type, string owner, string name)
{
    if (type == null) type = "";
    if (owner == null) owner = "";
    if (name == null) name = "";
    return type + "|" + owner.Length + ":" + owner + "|" + name.Length + ":" + name;
};

Func<object, string, object> readProperty =
    delegate(object obj, string propertyName)
{
    if (obj == null) return null;
    try
    {
        PropertyInfo property = obj.GetType().GetProperty(propertyName);
        if (property == null) return null;
        return property.GetValue(obj, null);
    }
    catch
    {
        return null;
    }
};

Func<object, string, string> readName =
    delegate(object obj, string propertyName)
{
    object value = readProperty(obj, propertyName);
    if (value == null) return "";
    object nameValue = readProperty(value, "Name");
    return nameValue == null ? "" : (Convert.ToString(nameValue) ?? "");
};

Func<object, string[]> describeObject =
    delegate(object obj)
{
    if (obj == null)
        return new string[] { "", "", "", "", "<null>" };

    string rawType = obj.GetType().Name;
    string type = "";
    string owner = "";
    object nameValue = readProperty(obj, "Name");
    string name = nameValue == null ? "" : (Convert.ToString(nameValue) ?? "");

    if (rawType == "Measure" || rawType.EndsWith("Measure", StringComparison.Ordinal))
    {
        type = "Measure";
        owner = readName(obj, "Table");
    }
    else if (rawType == "Table")
    {
        type = "Table";
    }
    else if (rawType.IndexOf("Column", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        type = "Column";
        owner = readName(obj, "Table");
    }
    else
    {
        return new string[] { "", "", name, "", rawType };
    }

    string display = type == "Table"
        ? "Table:" + name
        : type + ":" + owner + "[" + name + "]";
    string keyType = type == "Measure" ? "M" : (type == "Table" ? "T" : "C");
    string key = makeKey(keyType, owner, name);
    return new string[] { type, owner, name, key, display };
};

Func<string, string, string> displayForKey =
    delegate(string key, string fallback)
{
    return registry.ContainsKey(key) ? registry[key] : fallback;
};

// ------------------------------------------------------------
// 3. REGISTRY AND EDGE HELPERS
// ------------------------------------------------------------

Action<string, string, string, string> registerKey =
    delegate(string key, string display, string type, string owner)
{
    if (registry.ContainsKey(key))
    {
        if (registry[key] != display)
            collisions.Add(key + " | Existing: " + registry[key] + " | New: " + display);
        return;
    }
    registry.Add(key, display);
    keyTypes.Add(key, type);
    keyOwners.Add(key, owner);
    registeredCount++;
};

Action<string, string> addEdge =
    delegate(string source, string destination)
{
    if (!forwardGraph.ContainsKey(source))
        forwardGraph.Add(source, new HashSet<string>());
    forwardGraph[source].Add(destination);
};

// ------------------------------------------------------------
// 4. BUILD REAL MODEL DEPENDENCY GRAPH
// ------------------------------------------------------------


// 4.1 Discover user tables only.
foreach (var table in Model.Tables)
{
    // Skip Power BI internal calculated tables.
    if (table.GetType().Name == "CalculatedTable")
        continue;

    string[] info = describeObject(table);

    if (info[0] == "")
    {
        unknownTypes.Add("Table: " + info[4]);
        continue;
    }

    registerKey(info[3], info[4], info[0], info[1]);
    discoveredTableCount++;
}

// 4.2 Discover every column, including columns
// that are not referenced by any measure.
foreach (var column in Model.AllColumns)
{
    string[] info = describeObject(column);

    if (info[0] == "")
    {
        unknownTypes.Add("Column: " + info[4]);
        continue;
    }

    if (info[1] == "")
        missingOwners.Add(info[4] + ":" + info[2]);

    registerKey(
        info[3],
        info[4],
        info[0],
        info[1]
    );

    discoveredColumnCount++;
}

// 4.3 Discover every measure, including measures
// that have no dependencies.
foreach (var measure in Model.AllMeasures)
{
    string[] info = describeObject(measure);

    if (info[0] == "")
    {
        unknownTypes.Add("Measure: " + info[4]);
        continue;
    }

    if (info[1] == "")
        missingOwners.Add(info[4] + ":" + info[2]);

    registerKey(
        info[3],
        info[4],
        info[0],
        info[1]
    );

    discoveredMeasureCount++;
}

// 4.4 Extract direct dependencies from measures.
foreach (var measure in Model.AllMeasures)
{
    string[] measureInfo = describeObject(measure);

    if (measureInfo[0] != "Measure")
        continue;

    foreach (var dependency in measure.DependsOn)
    {
        object dependencyObject = dependency.Key;

        string[] dependencyInfo =
            describeObject(dependencyObject);

        if (dependencyInfo[0] == "")
        {
            unknownTypes.Add(
                "Dependency: " + dependencyInfo[4]
            );
            continue;
        }

        if (dependencyInfo[1] == "" &&
            dependencyInfo[0] != "Table")
        {
            missingOwners.Add(
                dependencyInfo[4] + ":" +
                dependencyInfo[2]
            );
        }

        registerKey(
            dependencyInfo[3],
            dependencyInfo[4],
            dependencyInfo[0],
            dependencyInfo[1]
        );

        // Dependency -> Dependent Measure
        addEdge(
            dependencyInfo[3],
            measureInfo[3]
        );

        inventory.Add(
            measureInfo[4] +
            " <- " +
            dependencyInfo[4]
        );
    }
}

// ------------------------------------------------------------
// 5. DETERMINISTIC BFS
// ------------------------------------------------------------

Func<Dictionary<string, HashSet<string>>, List<string>, Dictionary<string, string>, Dictionary<string, int>> runBfs =
    delegate(Dictionary<string, HashSet<string>> graph, List<string> sources, Dictionary<string, string> names)
{
    var levels = new Dictionary<string, int>();
    var queue = new Queue<string>();
    foreach (string source in sources)
    {
        if (!levels.ContainsKey(source))
        {
            levels.Add(source, 0);
            queue.Enqueue(source);
        }
    }
    while (queue.Count > 0)
    {
        string current = queue.Dequeue();
        if (!graph.ContainsKey(current)) continue;
        foreach (string next in graph[current]
            .OrderBy(delegate(string k) { return names.ContainsKey(k) ? names[k] : k; }, StringComparer.Ordinal)
            .ThenBy(delegate(string k) { return k; }, StringComparer.Ordinal))
        {
            if (!levels.ContainsKey(next))
            {
                levels.Add(next, levels[current] + 1);
                queue.Enqueue(next);
            }
        }
    }
    return levels;
};

// ------------------------------------------------------------
// 6. DETERMINISTIC DFS - ALL SIMPLE PATH PREFIXES
// ------------------------------------------------------------

Func<Dictionary<string, HashSet<string>>, string, int, Dictionary<string, string>, object[]> runDfs =
    delegate(Dictionary<string, HashSet<string>> graph, string source, int pathLimit, Dictionary<string, string> names)
{
    var paths = new List<string>();
    var currentPath = new List<string>();
    var visited = new HashSet<string>();
    var reached = new HashSet<string>();
    bool truncated = false;
    bool cycleDetected = false;
    Action<string> dfs = null;

    dfs = delegate(string current)
    {
        if (truncated) return;
        visited.Add(current);
        reached.Add(current);
        currentPath.Add(names.ContainsKey(current) ? names[current] : current);

        if (currentPath.Count > 1)
        {
            if (paths.Count < pathLimit)
                paths.Add(String.Join(" -> ", currentPath.ToArray()));
            else
            {
                truncated = true;
                currentPath.RemoveAt(currentPath.Count - 1);
                visited.Remove(current);
                return;
            }
        }

        if (graph.ContainsKey(current))
        {
            foreach (string next in graph[current]
                .OrderBy(delegate(string k) { return names.ContainsKey(k) ? names[k] : k; }, StringComparer.Ordinal)
                .ThenBy(delegate(string k) { return k; }, StringComparer.Ordinal))
            {
                if (visited.Contains(next))
                {
                    cycleDetected = true;
                    continue;
                }
                if (truncated) break;
                dfs(next);
                if (truncated) break;
            }
        }
        currentPath.RemoveAt(currentPath.Count - 1);
        visited.Remove(current);
    };

    dfs(source);
    return new object[] { paths, truncated, cycleDetected, reached };
};

// ------------------------------------------------------------
// 7. DYNAMIC TARGET DISCOVERY
// ------------------------------------------------------------

var targets = forwardGraph.Keys
    .OrderBy(delegate(string k) { return registry.ContainsKey(k) ? registry[k] : k; }, StringComparer.Ordinal)
    .ThenBy(delegate(string k) { return k; }, StringComparer.Ordinal)
    .ToList();

var bfsAffectedByTarget = new Dictionary<string, HashSet<string>>();
var dfsAffectedByTarget = new Dictionary<string, HashSet<string>>();
var dfsTruncatedByTarget = new Dictionary<string, bool>();

// ------------------------------------------------------------
// 8. REPORT HEADER AND INVENTORY
// ------------------------------------------------------------

report.AppendLine("==========================================");
report.AppendLine("       POWER BI IMPACT ANALYZER");
report.AppendLine("       VERSION 0.9");
report.AppendLine("       DYNAMIC GRAPH REGRESSION TEST");
report.AppendLine("==========================================");
report.AppendLine();

report.AppendLine("Scope: Direct dependencies discovered through Model.AllMeasures and Measure.DependsOn.");
report.AppendLine("The model inventory includes all discovered tables, columns, and measures.");
report.AppendLine("Dependency graph coverage is limited to dependencies exposed by Measure.DependsOn.");
report.AppendLine("This does not guarantee coverage of every dependency type, including calculated-column M expressions.");
report.AppendLine("No dependency semantics are synthesized for unrelated object types.");
report.AppendLine();

report.AppendLine("------------------------------------------");
report.AppendLine("[1] MODEL INVENTORY");
report.AppendLine("------------------------------------------");

report.AppendLine("Discovered Tables: " + discoveredTableCount);
report.AppendLine("Discovered Columns: " + discoveredColumnCount);
report.AppendLine("Discovered Measures: " + discoveredMeasureCount);
report.AppendLine("Total Registered Objects: " + registeredCount);
report.AppendLine();

report.AppendLine("------------------------------------------");
report.AppendLine("[2] DEPENDENCY GRAPH");
report.AppendLine("------------------------------------------");

report.AppendLine("Graph Nodes With Outgoing Edges: " + forwardGraph.Count);
report.AppendLine("Dependency Records: " + inventory.Count);
report.AppendLine("Graph Roots With Outgoing Edges: " + targets.Count);
report.AppendLine();

// ------------------------------------------------------------
// 9. TARGET TESTS: BFS + DFS
// ------------------------------------------------------------

report.AppendLine();
report.AppendLine("------------------------------------------");
report.AppendLine("[3] DYNAMIC TARGET TESTS");
report.AppendLine("------------------------------------------");
foreach (string root in targets)
{
    string targetDisplay = displayForKey(root, root);
    report.AppendLine();
    report.AppendLine("TARGET: " + targetDisplay);
    Dictionary<string, int> levels = runBfs(forwardGraph, new List<string> { root }, registry);
    var bfsAffected = new HashSet<string>(levels.Keys.Where(delegate(string k) { return k != root; }));
    bfsAffectedByTarget[root] = bfsAffected;
    report.AppendLine("BFS Affected Objects: " + bfsAffected.Count);
    foreach (var item in levels
        .Where(delegate(KeyValuePair<string, int> x) { return x.Key != root; })
        .OrderBy(delegate(KeyValuePair<string, int> x) { return x.Value; })
        .ThenBy(delegate(KeyValuePair<string, int> x) { return registry.ContainsKey(x.Key) ? registry[x.Key] : x.Key; }, StringComparer.Ordinal)
        .ThenBy(delegate(KeyValuePair<string, int> x) { return x.Key; }, StringComparer.Ordinal))
    {
        report.AppendLine("Level " + item.Value + ": " + displayForKey(item.Key, item.Key));
    }

    object[] dfsResult = runDfs(forwardGraph, root, maxPaths, registry);
    List<string> paths = (List<string>)dfsResult[0];
    bool truncated = (bool)dfsResult[1];
    bool cycleDetected = (bool)dfsResult[2];
    HashSet<string> dfsReachedAll = (HashSet<string>)dfsResult[3];
    var dfsAffected = new HashSet<string>(dfsReachedAll.Where(delegate(string k) { return k != root; }));
    dfsAffectedByTarget[root] = dfsAffected;
    dfsTruncatedByTarget[root] = truncated;
    report.AppendLine("DFS Paths: " + paths.Count);
    foreach (string path in paths)
        report.AppendLine("Path: " + path);
    report.AppendLine("DFS Truncated: " + truncated);
    report.AppendLine("Cycle Encountered: " + cycleDetected);
}

// ------------------------------------------------------------
// 10. TABLE IMPACT, MULTI-SOURCE, AND VALIDATION
// ------------------------------------------------------------

report.AppendLine();
report.AppendLine("------------------------------------------");
report.AppendLine("[4] TABLE-LEVEL DIRECT IMPACT");
report.AppendLine("------------------------------------------");
var tableKeys = registry.Keys
    .Where(delegate(string k) { return keyTypes.ContainsKey(k) && keyTypes[k] == "Table"; })
    .OrderBy(delegate(string k) { return registry[k]; }, StringComparer.Ordinal)
    .ThenBy(delegate(string k) { return k; }, StringComparer.Ordinal)
    .ToList();
report.AppendLine("Discovered Table Objects: " + tableKeys.Count);
foreach (string tableKey in tableKeys)
{
    int directCount = 0;
    report.AppendLine("TABLE: " + displayForKey(tableKey, tableKey));
    if (forwardGraph.ContainsKey(tableKey))
    {
        foreach (string dependent in forwardGraph[tableKey]
            .OrderBy(delegate(string k) { return displayForKey(k, k); }, StringComparer.Ordinal)
            .ThenBy(delegate(string k) { return k; }, StringComparer.Ordinal))
        {
            directCount++;
            report.AppendLine("  " + displayForKey(dependent, dependent));
        }
    }
    report.AppendLine("Direct Impact Count: " + directCount);
}
if (tableKeys.Count == 0)
    report.AppendLine("NOT TESTED - No Table objects were discovered in the dependency graph.");

report.AppendLine();
report.AppendLine("------------------------------------------");
report.AppendLine("[5] MULTI-SOURCE TEST");
report.AppendLine("------------------------------------------");
var columnSources = targets.Where(delegate(string k) { return keyTypes.ContainsKey(k) && keyTypes[k] == "Column"; }).ToList();
var selectedSources = new List<string>();
for (int i = 0; i < columnSources.Count && selectedSources.Count == 0; i++)
{
    for (int j = i + 1; j < columnSources.Count; j++)
    {
        string ownerA = keyOwners.ContainsKey(columnSources[i]) ? keyOwners[columnSources[i]] : "";
        string ownerB = keyOwners.ContainsKey(columnSources[j]) ? keyOwners[columnSources[j]] : "";
        if (ownerA == ownerB)
        {
            selectedSources.Add(columnSources[i]);
            selectedSources.Add(columnSources[j]);
            break;
        }
    }
}
if (selectedSources.Count == 0 && targets.Count >= 2)
{
    selectedSources.Add(targets[0]);
    selectedSources.Add(targets[1]);
}
if (selectedSources.Count < 2)
{
    report.AppendLine("NOT TESTED - Fewer than two graph source objects are available.");
}
else
{
    report.AppendLine("Sources: " + displayForKey(selectedSources[0], selectedSources[0]) + " + " + displayForKey(selectedSources[1], selectedSources[1]));
    Dictionary<string, int> multiLevels = runBfs(forwardGraph, selectedSources, registry);
    int distinctReachable = multiLevels.Count - selectedSources.Distinct().Count();
    report.AppendLine("Distinct Reachable Objects: " + distinctReachable);
    foreach (var item in multiLevels
        .Where(delegate(KeyValuePair<string, int> x) { return x.Value > 0; })
        .OrderBy(delegate(KeyValuePair<string, int> x) { return x.Value; })
        .ThenBy(delegate(KeyValuePair<string, int> x) { return displayForKey(x.Key, x.Key); }, StringComparer.Ordinal)
        .ThenBy(delegate(KeyValuePair<string, int> x) { return x.Key; }, StringComparer.Ordinal))
        report.AppendLine("Level " + item.Value + ": " + displayForKey(item.Key, item.Key));
}

report.AppendLine();
report.AppendLine("------------------------------------------");
report.AppendLine("[6] SYNTHETIC KEY VALIDATION");
report.AppendLine("------------------------------------------");
string keyA = makeKey("C", "A|1", "B");
string keyB = makeKey("C", "A", "1|B");
string keyC = makeKey("M", "A|1", "B");
bool ownerBoundaryPassed = keyA != keyB;
bool typeBoundaryPassed = keyA != keyC;
bool sameKeyPassed = makeKey("C", "Owner", "Item") == makeKey("C", "Owner", "Item");
report.AppendLine("Different Owner/Name Boundary: " + (ownerBoundaryPassed ? "PASS" : "FAIL"));
report.AppendLine("Different Object Types: " + (typeBoundaryPassed ? "PASS" : "FAIL"));
report.AppendLine("Same Object Same Key: " + (sameKeyPassed ? "PASS" : "FAIL"));

report.AppendLine();
report.AppendLine("------------------------------------------");
report.AppendLine("[7] SYNTHETIC CYCLE TEST");
report.AppendLine("------------------------------------------");
var cycleGraph = new Dictionary<string, HashSet<string>>();
cycleGraph.Add("A", new HashSet<string> { "B" });
cycleGraph.Add("B", new HashSet<string> { "A" });
var cycleNames = new Dictionary<string, string>();
cycleNames.Add("A", "A");
cycleNames.Add("B", "B");
object[] cycleResult = runDfs(cycleGraph, "A", 100, cycleNames);
bool cycleTestPassed = (bool)cycleResult[2];
report.AppendLine("Cycle Detection: " + (cycleTestPassed ? "PASS" : "FAIL"));

report.AppendLine();
report.AppendLine("------------------------------------------");
report.AppendLine("[8] SYNTHETIC PATH LIMIT TEST");
report.AppendLine("------------------------------------------");
var pathGraph = new Dictionary<string, HashSet<string>>();
pathGraph.Add("S", new HashSet<string> { "A", "B" });
pathGraph.Add("A", new HashSet<string> { "T" });
pathGraph.Add("B", new HashSet<string> { "T" });
var pathNames = new Dictionary<string, string>();
pathNames.Add("S", "S"); pathNames.Add("A", "A"); pathNames.Add("B", "B"); pathNames.Add("T", "T");
object[] limitResult = runDfs(pathGraph, "S", 1, pathNames);
bool limitTestPassed = (bool)limitResult[1];
report.AppendLine("Path Limit Enforcement: " + (limitTestPassed ? "PASS" : "FAIL"));
report.AppendLine("Paths Returned: " + ((List<string>)limitResult[0]).Count);

report.AppendLine();
report.AppendLine("------------------------------------------");
report.AppendLine("[9] GRAPH STRUCTURE AND REGISTRY DIAGNOSTICS");
report.AppendLine("------------------------------------------");
int invalidEndpoints = 0;
var invalidEndpointDetails = new List<string>();
foreach (var edgeGroup in forwardGraph)
{
    if (!registry.ContainsKey(edgeGroup.Key))
    {
        invalidEndpoints++;
        invalidEndpointDetails.Add("Missing source endpoint: " + edgeGroup.Key);
    }
    foreach (string destination in edgeGroup.Value)
    {
        if (!registry.ContainsKey(destination))
        {
            invalidEndpoints++;
            invalidEndpointDetails.Add("Missing destination endpoint: " + destination);
        }
    }
}
report.AppendLine("Structural Graph Endpoint Validity: " + (invalidEndpoints == 0 ? "PASS" : "FAIL") + " | Invalid Endpoints: " + invalidEndpoints);
foreach (string detail in invalidEndpointDetails.OrderBy(delegate(string x) { return x; }, StringComparer.Ordinal))
    report.AppendLine("INVALID ENDPOINT: " + detail);
report.AppendLine("Unknown Dependency Types: " + unknownTypes.Distinct().Count());
foreach (string item in unknownTypes.Distinct().OrderBy(delegate(string x) { return x; }, StringComparer.Ordinal))
    report.AppendLine("UNKNOWN: " + item);
report.AppendLine("Objects With Missing Owner: " + missingOwners.Distinct().Count());
foreach (string item in missingOwners.Distinct().OrderBy(delegate(string x) { return x; }, StringComparer.Ordinal))
    report.AppendLine("MISSING OWNER: " + item);
report.AppendLine("Detected Key Collisions: " + collisions.Count);
foreach (string item in collisions.OrderBy(delegate(string x) { return x; }, StringComparer.Ordinal))
    report.AppendLine("COLLISION: " + item);

report.AppendLine();
report.AppendLine("------------------------------------------");
report.AppendLine("[10] DYNAMIC CONSISTENCY CHECKS");
report.AppendLine("------------------------------------------");
report.AppendLine("Comparison: Distinct DFS-reached node keys (excluding the source) versus BFS affected keys.");
foreach (string root in targets)
{
    string label = displayForKey(root, root);
    if (dfsTruncatedByTarget[root])
        report.AppendLine(label + ": SKIPPED - DFS path cap reached; completeness cannot be compared.");
    else
    {
        bool equal = bfsAffectedByTarget[root].SetEquals(dfsAffectedByTarget[root]);
        report.AppendLine(label + ": " + (equal ? "PASS" : "FAIL") + " | BFS: " + bfsAffectedByTarget[root].Count + " | DFS distinct: " + dfsAffectedByTarget[root].Count);
    }
}
report.AppendLine("Synthetic key boundary checks: " + (ownerBoundaryPassed && typeBoundaryPassed && sameKeyPassed ? "PASS" : "FAIL"));
report.AppendLine("Synthetic cycle check: " + (cycleTestPassed ? "PASS" : "FAIL"));
report.AppendLine("Synthetic path limit check: " + (limitTestPassed ? "PASS" : "FAIL"));
report.AppendLine("Structural endpoint check: " + (invalidEndpoints == 0 ? "PASS" : "FAIL"));

// ------------------------------------------------------------
// 11. EXPORT AND FINAL REPORT
// ------------------------------------------------------------

string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
string exportFolder = Path.Combine(desktopPath, "ImpactAnalysis_Logs");
string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
string candidatePath = Path.Combine(exportFolder, "ImpactAnalyzer_v09_" + timestamp + ".txt");
string exportStatus = "FAILED";
try
{
    Directory.CreateDirectory(exportFolder);
    var completeCandidate = new StringBuilder(report.ToString());
    completeCandidate.AppendLine();
    completeCandidate.AppendLine("[11] EXPORT");
    completeCandidate.AppendLine("Export Status: SUCCESS");
    completeCandidate.AppendLine("File: " + candidatePath);
    completeCandidate.AppendLine();
    completeCandidate.AppendLine("==========================================");
    completeCandidate.AppendLine("          END OF TEST REPORT");
    completeCandidate.AppendLine("==========================================");
    File.WriteAllText(candidatePath, completeCandidate.ToString(), Encoding.UTF8);
    exportStatus = "SUCCESS";
    report.Clear();
    report.Append(completeCandidate.ToString());
}
catch (Exception ex)
{
    report.AppendLine();
    report.AppendLine("[11] EXPORT");
    report.AppendLine("Export Status: FAILED: " + ex.ToString());
    report.AppendLine("File: NOT CREATED / NOT CONFIRMED");
    report.AppendLine();
    report.AppendLine("==========================================");
    report.AppendLine("          END OF TEST REPORT");
    report.AppendLine("==========================================");
}

// Only one Info call, after the complete output is ready.
Info(report.ToString());
