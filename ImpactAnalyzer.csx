// ============================================================
// POWER BI IMPACT ANALYZER
// VERSION 1.0
// Tabular Editor 2 (C# script)
//
// What it does:
//   Builds a dependency graph of the connected model and, for every
//   table / column / measure, lists everything that is affected if you
//   change it (direct and indirect, with level and shortest path).
//
// Covered dependency sources:
//   - Measures                      (DAX)
//   - Calculated columns            (DAX)
//   - Calculated tables             (DAX)  -> and their generated columns
//   - Sort-by-column links
//   Relationships are listed as notes on the columns that use them.
//
// NOT covered (Tabular Editor cannot see them):
//   report visuals, filters, bookmarks, RLS filter expressions,
//   calculation items, KPIs, Power Query (M) lineage.
//
// How to use:
//   1. Open the PBIX in Power BI Desktop, then connect Tabular Editor 2
//      to it (External Tools, or connect to the local msmdsrv port).
//   2. Paste this script in "Advanced Scripting" and run it.
//      Optional: select one or more measures/columns/tables in the
//      model tree first -> their full DFS paths appear in the report.
//
// Output: one new folder per run, Desktop\ImpactAnalysis\Run_<timestamp>\, with two files:
//   ImpactReport_<model>.html  the report: search an object, see what it affects,
//                              plus a "Paths & log" tab (open in a browser)
//   ImpactReport_<model>.csv   flat table for Excel / Power BI: one row per
//                              (target, affected object) with level and path;
//                              objects with no dependents get one row, Affected empty
// ============================================================

using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Globalization;

// ------------------------------------------------------------
// 0. SETTINGS
// ------------------------------------------------------------

bool cfgDebug = false;                    // true = run synthetic self tests and add them to the log tab
int cfgMaxPaths = 2000;                   // DFS path cap per selected target
string[] cfgDfsTargets = new string[] { };// optional, e.g. "Sales[Total Sales]" or "Customer"
bool cfgExportHtml = true;
bool cfgExportCsv = true;
bool cfgOpenFolder = true;                // opens the HTML report when finished
string cfgOutputRoot = "";                // empty = Desktop\ImpactAnalysis

// ------------------------------------------------------------
// 1. STATE
// ------------------------------------------------------------

var gRegistry = new Dictionary<string, string>();          // key -> display ("Measure:Sales[Total]")
var gShort = new Dictionary<string, string>();             // key -> "Sales[Total]" / "Customer"
var gType = new Dictionary<string, string>();              // key -> Measure | Column | Table
var gOwner = new Dictionary<string, string>();             // key -> owning table name
var gForward = new Dictionary<string, HashSet<string>>();  // dependency -> dependents
var gBackward = new Dictionary<string, HashSet<string>>(); // dependent -> dependencies
var gEdgeKinds = new Dictionary<string, string>();         // src + \u0001 + dst -> kind
var gKindCounts = new Dictionary<string, int>();
var gNotes = new Dictionary<string, List<string>>();
var gWarnings = new List<string>();
var gUnknown = new List<string>();
var gCollisions = new List<string>();
var gSkippedInternal = new List<string>();

// ------------------------------------------------------------
// 2. HELPERS
// ------------------------------------------------------------

Func<string, string, string, string> makeKey = (kt, ow, nm) =>
{
    if (kt == null) kt = "";
    if (ow == null) ow = "";
    if (nm == null) nm = "";
    return kt + "|" + ow.Length + ":" + ow + "|" + nm.Length + ":" + nm;
};

Func<object, string, object> readProp = (o, pn) =>
{
    if (o == null) return null;
    try
    {
        PropertyInfo pi = o.GetType().GetProperty(pn);
        return pi == null ? null : pi.GetValue(o, null);
    }
    catch
    {
        return null;
    }
};

Func<object, string, string> readNameOf = (o, pn) =>
{
    object inner = readProp(o, pn);
    if (inner == null) return "";
    object innerName = readProp(inner, "Name");
    return innerName == null ? "" : Convert.ToString(innerName);
};

// Power BI auto date/time helper tables are internal noise.
Func<string, bool> isInternalName = tn =>
{
    return tn != null &&
        (tn.StartsWith("LocalDateTable_", StringComparison.Ordinal) ||
         tn.StartsWith("DateTableTemplate_", StringComparison.Ordinal));
};

// Returns { type, owner, name, key, display, short }. type == "" means unsupported object.
Func<object, string[]> describe = o =>
{
    if (o == null) return null;
    string raw = o.GetType().Name;
    string nm = Convert.ToString(readProp(o, "Name"));
    if (nm == null) nm = "";
    string tp;
    string ow = "";

    if (raw == "Measure" || raw.EndsWith("Measure", StringComparison.Ordinal))
    {
        tp = "Measure";
        ow = readNameOf(o, "Table");
    }
    else if (raw.IndexOf("Column", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        tp = "Column";
        ow = readNameOf(o, "Table");
    }
    else if (raw == "Table" || raw.EndsWith("Table", StringComparison.Ordinal))
    {
        tp = "Table";
    }
    else
    {
        return new string[] { "", "", nm, "", raw, "" };
    }

    string sh = tp == "Table" ? nm : ow + "[" + nm + "]";
    string kc = tp == "Measure" ? "M" : (tp == "Table" ? "T" : "C");
    return new string[] { tp, ow, nm, makeKey(kc, ow, nm), tp + ":" + sh, sh };
};

Action<string[]> register = di =>
{
    if (di == null || di[0] == "") return;
    if (gRegistry.ContainsKey(di[3]))
    {
        if (gRegistry[di[3]] != di[4])
            gCollisions.Add(di[3] + " | Existing: " + gRegistry[di[3]] + " | New: " + di[4]);
        return;
    }
    gRegistry.Add(di[3], di[4]);
    gShort.Add(di[3], di[5]);
    gType.Add(di[3], di[0]);
    gOwner.Add(di[3], di[1]);
};

Action<string, string> addNote = (nk, ntext) =>
{
    List<string> nl;
    if (!gNotes.TryGetValue(nk, out nl))
    {
        nl = new List<string>();
        gNotes.Add(nk, nl);
    }
    if (!nl.Contains(ntext)) nl.Add(ntext);
};

// Edge direction: dependency -> dependent (the object that breaks / changes if the dependency changes).
Action<string, string, string> addEdge = (ea, eb, kind) =>
{
    if (ea == eb) return;
    if (!gRegistry.ContainsKey(ea) || !gRegistry.ContainsKey(eb))
    {
        gWarnings.Add("Edge skipped (unregistered endpoint): " + ea + " -> " + eb);
        return;
    }
    string ek = ea + "\u0001" + eb;
    if (gEdgeKinds.ContainsKey(ek)) return;
    gEdgeKinds.Add(ek, kind);

    HashSet<string> fs;
    if (!gForward.TryGetValue(ea, out fs))
    {
        fs = new HashSet<string>();
        gForward.Add(ea, fs);
    }
    fs.Add(eb);

    HashSet<string> bs;
    if (!gBackward.TryGetValue(eb, out bs))
    {
        bs = new HashSet<string>();
        gBackward.Add(eb, bs);
    }
    bs.Add(ea);

    int kc;
    gKindCounts.TryGetValue(kind, out kc);
    gKindCounts[kind] = kc + 1;
};

// Reads the DependsOn collection of any object (measure, calculated column, calculated table)
// without compile-time dependence on a specific TOM wrapper type.
Func<object, string, List<object>> getDeps = (so, sdisplay) =>
{
    var result = new List<object>();
    try
    {
        IEnumerable list = readProp(so, "DependsOn") as IEnumerable;
        if (list == null) return result;
        foreach (object item in list)
        {
            object itemKey = readProp(item, "Key");
            result.Add(itemKey != null ? itemKey : item);
        }
    }
    catch (Exception ex)
    {
        gWarnings.Add("DependsOn failed for " + sdisplay + ": " + ex.Message);
    }
    return result;
};

// ------------------------------------------------------------
// 3. DISCOVER OBJECTS
// ------------------------------------------------------------

foreach (var tblObj in Model.Tables)
{
    string[] ti = describe(tblObj);
    if (ti == null) continue;
    if (ti[0] == "")
    {
        gUnknown.Add("Table: " + ti[4]);
        continue;
    }
    if (isInternalName(ti[2]))
    {
        gSkippedInternal.Add(ti[2]);
        continue;
    }
    register(ti);
}

foreach (var colObj in Model.AllColumns)
{
    string[] ci = describe(colObj);
    if (ci == null) continue;
    if (ci[0] == "")
    {
        gUnknown.Add("Column: " + ci[4]);
        continue;
    }
    if (isInternalName(ci[1])) continue;
    if (Convert.ToString(readProp(colObj, "Type")) == "RowNumber") continue;
    register(ci);
}

foreach (var meaObj in Model.AllMeasures)
{
    string[] mi = describe(meaObj);
    if (mi == null) continue;
    if (mi[0] == "")
    {
        gUnknown.Add("Measure: " + mi[4]);
        continue;
    }
    if (isInternalName(mi[1])) continue;
    register(mi);
}

int gDiscoveredTables = gType.Count(kv => kv.Value == "Table");
int gDiscoveredColumns = gType.Count(kv => kv.Value == "Column");
int gDiscoveredMeasures = gType.Count(kv => kv.Value == "Measure");

// ------------------------------------------------------------
// 4. BUILD THE DEPENDENCY GRAPH
// ------------------------------------------------------------

Action<object, string> scanDeps = (so, kindLabel) =>
{
    string[] sd = describe(so);
    if (sd == null || sd[0] == "") return;
    if (!gRegistry.ContainsKey(sd[3])) return;

    foreach (object dep in getDeps(so, sd[4]))
    {
        string[] dd = describe(dep);
        if (dd == null || dd[0] == "")
        {
            gUnknown.Add("Dependency of " + sd[4] + ": " + (dd == null ? "<null>" : dd[4]));
            continue;
        }
        if (isInternalName(dd[0] == "Table" ? dd[2] : dd[1])) continue;
        register(dd);
        addEdge(dd[3], sd[3], kindLabel);
    }
};

// 4.1 Measures
foreach (var depMeasure in Model.AllMeasures)
    scanDeps(depMeasure, "Measure");

// 4.2 Calculated columns
foreach (var depColumn in Model.AllColumns)
{
    if (depColumn.GetType().Name == "CalculatedColumn")
        scanDeps(depColumn, "CalculatedColumn");
}

// 4.3 Calculated tables (user DAX tables; auto date tables are already excluded)
foreach (var depTable in Model.Tables)
{
    if (depTable.GetType().Name == "CalculatedTable")
        scanDeps(depTable, "CalculatedTable");
}

// 4.4 Calculated table -> the columns it generates
foreach (var genColumn in Model.AllColumns)
{
    if (genColumn.GetType().Name != "CalculatedTableColumn") continue;
    string[] gi = describe(genColumn);
    if (gi == null || gi[0] == "") continue;
    string genOwnerKey = makeKey("T", "", gi[1]);
    if (!gRegistry.ContainsKey(genOwnerKey) || !gRegistry.ContainsKey(gi[3])) continue;
    addEdge(genOwnerKey, gi[3], "CalculatedTableColumn");
}

// 4.5 Sort-by-column links
foreach (var sortColumn in Model.AllColumns)
{
    object sortTarget = readProp(sortColumn, "SortByColumn");
    if (sortTarget == null) continue;
    string[] sortSelf = describe(sortColumn);
    string[] sortBy = describe(sortTarget);
    if (sortSelf == null || sortBy == null || sortSelf[0] == "" || sortBy[0] == "") continue;
    if (!gRegistry.ContainsKey(sortSelf[3]) || !gRegistry.ContainsKey(sortBy[3])) continue;
    addEdge(sortBy[3], sortSelf[3], "SortByColumn");
    addNote(sortBy[3], "Sort-by column of " + sortSelf[5]);
    addNote(sortSelf[3], "Sorted by " + sortBy[5]);
}

// 4.6 Relationships (notes only: a relationship is not an object in the graph)
foreach (object relObj in Model.Relationships)
{
    string[] relFrom = describe(readProp(relObj, "FromColumn"));
    string[] relTo = describe(readProp(relObj, "ToColumn"));
    if (relFrom == null || relTo == null || relFrom[0] == "" || relTo[0] == "") continue;
    string relActive = Convert.ToString(readProp(relObj, "IsActive"));
    string relText = "Relationship: " + relFrom[5] + " -> " + relTo[5] + (relActive == "False" ? " (inactive)" : "");
    if (gRegistry.ContainsKey(relFrom[3])) addNote(relFrom[3], relText);
    if (gRegistry.ContainsKey(relTo[3])) addNote(relTo[3], relText);
}

// Deterministic adjacency lists.
var gAdj = new Dictionary<string, List<string>>();
foreach (var fwPair in gForward)
{
    gAdj[fwPair.Key] = fwPair.Value
        .OrderBy(x => gRegistry[x], StringComparer.Ordinal)
        .ThenBy(x => x, StringComparer.Ordinal)
        .ToList();
}

// ------------------------------------------------------------
// 5. BFS (impact of every object) AND DFS (full paths)
// ------------------------------------------------------------

Action<string, Dictionary<string, int>, Dictionary<string, string>> runBfs = (bfsRoot, lv, par) =>
{
    var qu = new Queue<string>();
    lv[bfsRoot] = 0;
    qu.Enqueue(bfsRoot);
    while (qu.Count > 0)
    {
        string cur = qu.Dequeue();
        List<string> nl;
        if (!gAdj.TryGetValue(cur, out nl)) continue;
        foreach (string nx in nl)
        {
            if (lv.ContainsKey(nx)) continue;
            lv[nx] = lv[cur] + 1;
            par[nx] = cur;
            qu.Enqueue(nx);
        }
    }
};

Func<string, Dictionary<string, string>, string> buildPath = (bn, bp) =>
{
    var parts = new List<string>();
    string walkNode = bn;
    while (walkNode != null)
    {
        parts.Add(gShort[walkNode]);
        string up;
        walkNode = bp.TryGetValue(walkNode, out up) ? up : null;
    }
    parts.Reverse();
    return string.Join(" -> ", parts.ToArray());
};

// Lists complete simple paths (root -> ... -> last reachable node), capped by "limit".
// Returns { List<string> paths, bool truncated, bool cycleSeen, HashSet<string> reached }.
Func<Dictionary<string, List<string>>, Dictionary<string, string>, string, int, object[]> runDfs = (graph, names, dfsRoot, limit) =>
{
    var found = new List<string>();
    var stackNames = new List<string>();
    var onPath = new HashSet<string>();
    var reachedSet = new HashSet<string>();
    bool cut = false;
    bool loopSeen = false;
    Action<string> walk = null;
    walk = wn =>
    {
        if (cut) return;
        onPath.Add(wn);
        reachedSet.Add(wn);
        stackNames.Add(names.ContainsKey(wn) ? names[wn] : wn);
        bool extended = false;
        List<string> nxt;
        if (graph.TryGetValue(wn, out nxt))
        {
            foreach (string nn in nxt)
            {
                if (onPath.Contains(nn))
                {
                    loopSeen = true;
                    continue;
                }
                extended = true;
                walk(nn);
                if (cut) break;
            }
        }
        if (!extended && stackNames.Count > 1 && !cut)
        {
            if (found.Count < limit) found.Add(string.Join(" -> ", stackNames.ToArray()));
            else cut = true;
        }
        stackNames.RemoveAt(stackNames.Count - 1);
        onPath.Remove(wn);
    };
    walk(dfsRoot);
    return new object[] { found, cut, loopSeen, reachedSet };
};

// 5.1 Impact for EVERY registered object (objects without dependents get 0, not silence).
var gAllKeys = gRegistry.Keys
    .OrderBy(x => gRegistry[x], StringComparer.Ordinal)
    .ThenBy(x => x, StringComparer.Ordinal)
    .ToList();

var gTotal = new Dictionary<string, int>();
var gDepth = new Dictionary<string, int>();
var gAffected = new Dictionary<string, List<string[]>>();

foreach (string impactKey in gAllKeys)
{
    gTotal[impactKey] = 0;
    gDepth[impactKey] = 0;
    if (!gAdj.ContainsKey(impactKey)) continue;

    var lvMap = new Dictionary<string, int>();
    var parMap = new Dictionary<string, string>();
    runBfs(impactKey, lvMap, parMap);

    var rows = new List<string[]>();
    foreach (var pair in lvMap
        .Where(z => z.Value > 0)
        .OrderBy(z => z.Value)
        .ThenBy(z => gRegistry[z.Key], StringComparer.Ordinal)
        .ThenBy(z => z.Key, StringComparer.Ordinal))
    {
        rows.Add(new string[]
        {
            gShort[pair.Key],
            gType[pair.Key],
            pair.Value.ToString(CultureInfo.InvariantCulture),
            buildPath(pair.Key, parMap)
        });
        if (pair.Value > gDepth[impactKey]) gDepth[impactKey] = pair.Value;
    }
    gTotal[impactKey] = rows.Count;
    gAffected[impactKey] = rows;
}

// 5.2 DFS targets: names from cfgDfsTargets + whatever is selected in the model tree.
var gDfsKeys = new List<string>();
foreach (string wanted in cfgDfsTargets)
{
    foreach (string candKey in gAllKeys)
    {
        if (string.Equals(gShort[candKey], wanted, StringComparison.OrdinalIgnoreCase) && !gDfsKeys.Contains(candKey))
            gDfsKeys.Add(candKey);
    }
}

Action<object> addSelected = selObj =>
{
    string[] si = describe(selObj);
    if (si != null && si[0] != "" && gRegistry.ContainsKey(si[3]) && !gDfsKeys.Contains(si[3]))
        gDfsKeys.Add(si[3]);
};
try
{
    foreach (var selM in Selected.Measures) addSelected(selM);
    foreach (var selC in Selected.Columns) addSelected(selC);
    foreach (var selT in Selected.Tables) addSelected(selT);
}
catch (Exception selEx)
{
    gWarnings.Add("Could not read the current selection: " + selEx.Message);
}

var gPathText = new StringBuilder();
var gDfsResults = new Dictionary<string, object[]>();
if (gDfsKeys.Count == 0)
{
    gPathText.AppendLine("No DFS target selected.");
    gPathText.AppendLine("Select measures/columns/tables in the model tree (or fill cfgDfsTargets) and run again.");
}
foreach (string dfsKey in gDfsKeys)
{
    object[] dfsRes = runDfs(gAdj, gShort, dfsKey, cfgMaxPaths);
    gDfsResults[dfsKey] = dfsRes;
    var dfsPaths = (List<string>)dfsRes[0];
    gPathText.AppendLine("TARGET: " + gRegistry[dfsKey]);
    gPathText.AppendLine("Paths: " + dfsPaths.Count + " | Truncated: " + ((bool)dfsRes[1] ? "YES (cap " + cfgMaxPaths + ")" : "no") + " | Cycle: " + ((bool)dfsRes[2] ? "YES" : "no"));
    foreach (string dfsPath in dfsPaths) gPathText.AppendLine("  " + dfsPath);
    gPathText.AppendLine();
}

// ------------------------------------------------------------
// 6. EXPORT: CSV
// ------------------------------------------------------------

Func<string, string> csvq = ct => "\"" + (ct == null ? "" : ct.Replace("\"", "\"\"")) + "\"";
Func<string[], string> csvRow = cparts => string.Join(",", cparts.Select(cp => csvq(cp)).ToArray());

var gDetailCsv = new StringBuilder();
gDetailCsv.AppendLine(csvRow(new string[] { "Target", "TargetType", "TotalAffected", "Affected", "AffectedType", "Level", "ShortestPath" }));
foreach (string detKey in gAllKeys)
{
    string detTotal = gTotal[detKey].ToString(CultureInfo.InvariantCulture);
    if (!gAffected.ContainsKey(detKey))
    {
        // No dependents in the model: keep the object in the file with an empty Affected column.
        gDetailCsv.AppendLine(csvRow(new string[] { gShort[detKey], gType[detKey], detTotal, "", "", "", "" }));
        continue;
    }
    foreach (string[] detRow in gAffected[detKey])
    {
        gDetailCsv.AppendLine(csvRow(new string[] { gShort[detKey], gType[detKey], detTotal, detRow[0], detRow[1], detRow[2], detRow[3] }));
    }
}

// ------------------------------------------------------------
// 7. RUN LOG (+ optional synthetic self tests)
// ------------------------------------------------------------

var gLog = new StringBuilder();
gLog.AppendLine("POWER BI IMPACT ANALYZER v1.0 - RUN LOG");
gLog.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
gLog.AppendLine();
gLog.AppendLine("[1] INVENTORY");
gLog.AppendLine("Tables: " + gDiscoveredTables + " | Columns: " + gDiscoveredColumns + " | Measures: " + gDiscoveredMeasures + " | Registered (incl. dependency-only objects): " + gRegistry.Count);
gLog.AppendLine("Skipped internal auto-date tables: " + gSkippedInternal.Count);
foreach (string skipName in gSkippedInternal.OrderBy(x => x, StringComparer.Ordinal)) gLog.AppendLine("  " + skipName);
gLog.AppendLine();
gLog.AppendLine("[2] GRAPH");
gLog.AppendLine("Direct dependencies (edges): " + gEdgeKinds.Count);
foreach (var kindPair in gKindCounts.OrderBy(x => x.Key, StringComparer.Ordinal))
    gLog.AppendLine("  " + kindPair.Key + ": " + kindPair.Value);
int gNoDependents = gAllKeys.Count(x => gTotal[x] == 0);
gLog.AppendLine("Objects with no dependents in the model: " + gNoDependents + " (this does NOT mean unused in report visuals)");
gLog.AppendLine();
gLog.AppendLine("[3] SCOPE");
gLog.AppendLine("Covered: measures, calculated columns, calculated tables (and their columns), sort-by-column. Relationships appear as notes.");
gLog.AppendLine("Not covered: report visuals/filters/bookmarks, RLS expressions, calculation items, KPIs, Power Query (M).");
gLog.AppendLine();
gLog.AppendLine("[4] DIAGNOSTICS");
gLog.AppendLine("Unknown object types: " + gUnknown.Distinct().Count());
foreach (string unk in gUnknown.Distinct().OrderBy(x => x, StringComparer.Ordinal)) gLog.AppendLine("  UNKNOWN: " + unk);
gLog.AppendLine("Key collisions: " + gCollisions.Count);
foreach (string col in gCollisions.OrderBy(x => x, StringComparer.Ordinal)) gLog.AppendLine("  COLLISION: " + col);
gLog.AppendLine("Warnings: " + gWarnings.Distinct().Count());
foreach (string warn in gWarnings.Distinct().OrderBy(x => x, StringComparer.Ordinal)) gLog.AppendLine("  WARNING: " + warn);

if (cfgDebug)
{
    gLog.AppendLine();
    gLog.AppendLine("[5] SELF TESTS");
    bool testOwner = makeKey("C", "A|1", "B") != makeKey("C", "A", "1|B");
    bool testType = makeKey("C", "A|1", "B") != makeKey("M", "A|1", "B");
    bool testSame = makeKey("C", "Owner", "Item") == makeKey("C", "Owner", "Item");
    gLog.AppendLine("Key owner/name boundary: " + (testOwner ? "PASS" : "FAIL"));
    gLog.AppendLine("Key object-type boundary: " + (testType ? "PASS" : "FAIL"));
    gLog.AppendLine("Key determinism: " + (testSame ? "PASS" : "FAIL"));

    var testCycle = new Dictionary<string, List<string>>();
    testCycle["A"] = new List<string> { "B" };
    testCycle["B"] = new List<string> { "A" };
    var testNames = new Dictionary<string, string>();
    testNames["A"] = "A"; testNames["B"] = "B"; testNames["S"] = "S"; testNames["T"] = "T";
    gLog.AppendLine("Cycle detection: " + ((bool)runDfs(testCycle, testNames, "A", 100)[2] ? "PASS" : "FAIL"));

    var testLimit = new Dictionary<string, List<string>>();
    testLimit["S"] = new List<string> { "A", "B" };
    testLimit["A"] = new List<string> { "T" };
    testLimit["B"] = new List<string> { "T" };
    testNames["A"] = "A"; testNames["B"] = "B";
    gLog.AppendLine("Path cap: " + ((bool)runDfs(testLimit, testNames, "S", 1)[1] ? "PASS" : "FAIL"));

    int badEndpoints = 0;
    foreach (var fwTest in gForward)
    {
        if (!gRegistry.ContainsKey(fwTest.Key)) badEndpoints++;
        foreach (string fwDst in fwTest.Value) if (!gRegistry.ContainsKey(fwDst)) badEndpoints++;
    }
    gLog.AppendLine("Edge endpoints registered: " + (badEndpoints == 0 ? "PASS" : "FAIL (" + badEndpoints + ")"));

    foreach (string chkKey in gDfsKeys)
    {
        object[] chkRes = gDfsResults[chkKey];
        if ((bool)chkRes[1])
        {
            gLog.AppendLine("BFS vs DFS " + gShort[chkKey] + ": SKIPPED (path cap reached)");
            continue;
        }
        int dfsDistinct = ((HashSet<string>)chkRes[3]).Count - 1;
        gLog.AppendLine("BFS vs DFS " + gShort[chkKey] + ": " + (dfsDistinct == gTotal[chkKey] ? "PASS" : "FAIL") + " | BFS " + gTotal[chkKey] + " | DFS " + dfsDistinct);
    }
}

// ------------------------------------------------------------
// 8. EXPORT: HTML REPORT
// ------------------------------------------------------------

Func<string, string> js = jt =>
{
    if (jt == null) return "\"\"";
    var jb = new StringBuilder("\"");
    foreach (char ch in jt)
    {
        switch (ch)
        {
            case '\\': jb.Append("\\\\"); break;
            case '"': jb.Append("\\\""); break;
            case '\n': jb.Append("\\n"); break;
            case '\r': break;
            case '\t': jb.Append("\\t"); break;
            case '<': jb.Append("\\u003c"); break;
            case '>': jb.Append("\\u003e"); break;
            case '&': jb.Append("\\u0026"); break;
            default:
                if (ch < ' ') jb.Append("\\u" + ((int)ch).ToString("x4"));
                else jb.Append(ch);
                break;
        }
    }
    jb.Append('"');
    return jb.ToString();
};

Func<IEnumerable<string>, string> jsArray = items => "[" + string.Join(",", items.Select(it => js(it)).ToArray()) + "]";

string gModelName = readNameOf(Model, "Database");
string gHtmlText = "";
if (cfgExportHtml)
{
    var gJson = new StringBuilder();
    gJson.Append("{\"meta\":{\"generated\":" + js(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
    gJson.Append(",\"model\":" + js(gModelName));
    gJson.Append(",\"edges\":" + gEdgeKinds.Count + "},\"objects\":[");

    bool firstJsonObject = true;
    foreach (string jk in gAllKeys
        .OrderByDescending(x => gTotal[x])
        .ThenBy(x => gRegistry[x], StringComparer.Ordinal))
    {
        if (!firstJsonObject) gJson.Append(",");
        firstJsonObject = false;

        var upNames = gBackward.ContainsKey(jk)
            ? gBackward[jk].Select(x => gShort[x]).OrderBy(x => x, StringComparer.Ordinal).ToList()
            : new List<string>();
        var noteList = gNotes.ContainsKey(jk) ? gNotes[jk] : new List<string>();
        int downCount = gForward.ContainsKey(jk) ? gForward[jk].Count : 0;

        gJson.Append("{\"n\":" + js(gShort[jk]));
        gJson.Append(",\"t\":" + js(gType[jk]));
        gJson.Append(",\"d\":" + downCount);
        gJson.Append(",\"total\":" + gTotal[jk]);
        gJson.Append(",\"depth\":" + gDepth[jk]);
        gJson.Append(",\"up\":" + jsArray(upNames));
        gJson.Append(",\"notes\":" + jsArray(noteList));
        gJson.Append(",\"aff\":[");
        if (gAffected.ContainsKey(jk))
        {
            bool firstAff = true;
            foreach (string[] affRow in gAffected[jk])
            {
                if (!firstAff) gJson.Append(",");
                firstAff = false;
                gJson.Append("[" + js(affRow[0]) + "," + js(affRow[1]) + "," + affRow[2] + "," + js(affRow[3]) + "]");
            }
        }
        gJson.Append("]}");
    }
    gJson.Append("],\"log\":" + js(gLog.ToString()) + ",\"paths\":" + js(gPathText.ToString()) + "}");

    var gHtml = new StringBuilder();
    gHtml.Append(@"<!DOCTYPE html>
<html lang='en'><head><meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>Power BI Impact Analyzer</title>
<style>
:root{--bg:#ffffff;--fg:#1f2328;--muted:#656d76;--line:#d0d7de;--panel:#f6f8fa;--accent:#0969da;--sel:#ddf4ff;--warn:#9a6700}
@media (prefers-color-scheme: dark){:root{--bg:#0d1117;--fg:#e6edf3;--muted:#8b949e;--line:#30363d;--panel:#161b22;--accent:#58a6ff;--sel:#12263f;--warn:#d29922}}
*{box-sizing:border-box}
[hidden]{display:none !important}
body{margin:0;font:14px/1.45 system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;background:var(--bg);color:var(--fg);height:100vh;display:flex;flex-direction:column}
header{padding:10px 16px;border-bottom:1px solid var(--line);display:flex;gap:12px;align-items:baseline;flex-wrap:wrap}
header h1{font-size:16px;margin:0}
.tabs{display:flex;gap:6px;margin-left:auto}
.tabs button{padding:5px 12px;border:1px solid var(--line);border-radius:6px;background:var(--bg);color:var(--fg);font:inherit;cursor:pointer}
.tabs button.on{background:var(--sel);border-color:var(--accent)}
#info{flex:1;overflow:auto;padding:16px}
#info h2{font-size:15px;margin:14px 0 6px}
pre{margin:0;padding:10px;background:var(--panel);border:1px solid var(--line);border-radius:6px;white-space:pre-wrap;word-break:break-word;font:12px/1.5 Consolas,Menlo,monospace}
.muted{color:var(--muted)}
.warn{color:var(--warn)}
main{flex:1;display:flex;min-height:0}
aside{width:340px;border-right:1px solid var(--line);display:flex;flex-direction:column;min-height:0}
.tools{padding:10px;display:flex;flex-direction:column;gap:8px;border-bottom:1px solid var(--line)}
input[type=text],select{width:100%;padding:6px 8px;border:1px solid var(--line);border-radius:6px;background:var(--bg);color:var(--fg);font:inherit}
#list{list-style:none;margin:0;padding:0;overflow:auto;flex:1}
#list li{padding:6px 10px;border-bottom:1px solid var(--line);cursor:pointer;display:flex;justify-content:space-between;gap:8px}
#list li:hover{background:var(--panel)}
#list li.sel{background:var(--sel)}
#list li small{color:var(--muted);white-space:nowrap}
#detail{flex:1;overflow:auto;padding:16px;min-width:0}
#detail h2{margin:0 0 4px;font-size:18px;word-break:break-word}
#detail h3{margin:18px 0 6px;font-size:14px}
ul.plain{margin:0;padding-left:18px}
.tw{overflow-x:auto;margin-top:8px}
table{border-collapse:collapse;width:100%}
th,td{text-align:left;padding:5px 8px;border-bottom:1px solid var(--line);vertical-align:top}
th{background:var(--panel);position:sticky;top:0}
td.path{color:var(--muted);font-size:12px;word-break:break-word}
@media (max-width:800px){main{flex-direction:column}aside{width:100%;max-height:40vh;border-right:0;border-bottom:1px solid var(--line)}}
</style></head><body>
<header><h1>Power BI Impact Analyzer</h1><span id='meta' class='muted'></span>
<div class='tabs'><button id='tabE' class='on'>Explorer</button><button id='tabI'>Paths &amp; log</button></div></header>
<main id='explorer'>
<aside>
<div class='tools'>
<input type='text' id='q' placeholder='Search measure, column or table...'>
<select id='typeSel'><option value=''>All types</option><option>Measure</option><option>Column</option><option>Table</option></select>
<label><input type='checkbox' id='onlyImpact' checked> only objects with dependents</label>
<span id='count' class='muted'></span>
</div>
<ul id='list'></ul>
</aside>
<section id='detail'></section>
</main>
<section id='info' hidden><h2>DFS paths (selected targets)</h2><pre id='paths'></pre><h2>Run log</h2><pre id='log'></pre></section>
<script>
const DATA = ");
    gHtml.Append(gJson.ToString());
    gHtml.Append(@";
const objs = DATA.objects;
const $ = id => document.getElementById(id);
const LIST_CAP = 400, ROW_CAP = 3000;
let current = -1;

function el(tag, text, cls) {
  const e = document.createElement(tag);
  if (text !== undefined && text !== null) e.textContent = text;
  if (cls) e.className = cls;
  return e;
}

function filtered() {
  const term = $('q').value.trim().toLowerCase();
  const tp = $('typeSel').value;
  const only = $('onlyImpact').checked;
  const res = [];
  for (let i = 0; i < objs.length; i++) {
    const o = objs[i];
    if (only && o.total === 0) continue;
    if (tp && o.t !== tp) continue;
    if (term && o.n.toLowerCase().indexOf(term) < 0) continue;
    res.push(i);
  }
  return res;
}

function renderList() {
  const ul = $('list');
  ul.textContent = '';
  const res = filtered();
  res.slice(0, LIST_CAP).forEach(i => {
    const o = objs[i];
    const li = el('li', null, i === current ? 'sel' : '');
    li.appendChild(el('span', o.n));
    li.appendChild(el('small', o.t + ' \u00b7 ' + o.total));
    li.addEventListener('click', () => { current = i; renderList(); renderDetail(); });
    ul.appendChild(li);
  });
  $('count').textContent = res.length > LIST_CAP
    ? 'showing ' + LIST_CAP + ' of ' + res.length + ' - refine the search'
    : res.length + ' objects';
}

function addList(parent, title, items) {
  if (!items.length) return;
  parent.appendChild(el('h3', title));
  const u = el('ul', null, 'plain');
  items.forEach(t => u.appendChild(el('li', t)));
  parent.appendChild(u);
}

function renderDetail() {
  const d = $('detail');
  d.textContent = '';
  if (current < 0) {
    d.appendChild(el('p', 'Select an object on the left to see what is affected if you change it.', 'muted'));
    return;
  }
  const o = objs[current];
  d.appendChild(el('h2', o.n));
  d.appendChild(el('p', o.t + '  |  direct dependents: ' + o.d + '  |  total affected: ' + o.total + '  |  max depth: ' + o.depth, 'muted'));
  if (o.total === 0) {
    d.appendChild(el('p', 'No other model object depends on this one. Report visuals, filters and RLS are not analyzed, so check the report before deleting it.', 'warn'));
  }
  addList(d, 'Depends directly on', o.up);
  addList(d, 'Structural references (not part of the dependency graph)', o.notes);
  if (!o.aff.length) return;

  d.appendChild(el('h3', 'Affected objects (' + o.total + ')'));
  const f = el('input');
  f.type = 'text';
  f.placeholder = 'Filter affected objects...';
  d.appendChild(f);
  const wrap = el('div', null, 'tw');
  const tbl = el('table');
  const hr = el('tr');
  ['Level', 'Object', 'Type', 'Shortest path'].forEach(h => hr.appendChild(el('th', h)));
  const thead = el('thead');
  thead.appendChild(hr);
  tbl.appendChild(thead);
  const tb = el('tbody');
  tbl.appendChild(tb);
  wrap.appendChild(tbl);
  d.appendChild(wrap);
  const msg = el('p', '', 'muted');
  d.appendChild(msg);

  function fill() {
    const t = f.value.trim().toLowerCase();
    tb.textContent = '';
    let n = 0, shown = 0;
    o.aff.forEach(r => {
      if (t && (r[0] + ' ' + r[3]).toLowerCase().indexOf(t) < 0) return;
      n++;
      if (shown >= ROW_CAP) return;
      shown++;
      const tr = el('tr');
      tr.appendChild(el('td', r[2]));
      tr.appendChild(el('td', r[0]));
      tr.appendChild(el('td', r[1]));
      tr.appendChild(el('td', r[3], 'path'));
      tb.appendChild(tr);
    });
    msg.textContent = n > shown ? 'showing ' + shown + ' of ' + n + ' rows' : '';
  }
  f.addEventListener('input', fill);
  fill();
}

$('meta').textContent = 'Model: ' + (DATA.meta.model || '-') + '  |  generated ' + DATA.meta.generated +
  '  |  ' + objs.length + ' objects  |  ' + DATA.meta.edges + ' direct dependencies';
$('q').addEventListener('input', renderList);
$('typeSel').addEventListener('change', renderList);
$('onlyImpact').addEventListener('change', renderList);
function showTab(name) {
  $('explorer').hidden = name !== 'explorer';
  $('info').hidden = name !== 'info';
  $('tabE').className = name === 'explorer' ? 'on' : '';
  $('tabI').className = name === 'info' ? 'on' : '';
}
$('tabE').addEventListener('click', () => showTab('explorer'));
$('tabI').addEventListener('click', () => showTab('info'));
$('paths').textContent = DATA.paths;
$('log').textContent = DATA.log;
renderList();
renderDetail();
</script>
</body></html>");
    gHtmlText = gHtml.ToString();
}

// ------------------------------------------------------------
// 9. WRITE FILES
// ------------------------------------------------------------

string gOutFolder = cfgOutputRoot;
if (string.IsNullOrEmpty(gOutFolder))
    gOutFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ImpactAnalysis");

string gSafeModel = string.IsNullOrEmpty(gModelName) ? "Model" : gModelName;
foreach (char badChar in Path.GetInvalidFileNameChars())
    gSafeModel = gSafeModel.Replace(badChar, '_');
string gBaseName = "ImpactReport_" + gSafeModel;
string gRunFolder = Path.Combine(gOutFolder, "Run_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
string gHtmlPath = Path.Combine(gRunFolder, gBaseName + ".html");
string gCsvPath = Path.Combine(gRunFolder, gBaseName + ".csv");

string gWriteStatus = "SUCCESS";
try
{
    Directory.CreateDirectory(gRunFolder);
    if (cfgExportCsv)
        File.WriteAllText(gCsvPath, gDetailCsv.ToString(), new UTF8Encoding(true));
    if (cfgExportHtml)
        File.WriteAllText(gHtmlPath, gHtmlText, new UTF8Encoding(false));
}
catch (Exception writeEx)
{
    gWriteStatus = "FAILED: " + writeEx.Message;
}

if (gWriteStatus == "SUCCESS" && cfgOpenFolder)
{
    try { System.Diagnostics.Process.Start(cfgExportHtml ? gHtmlPath : gRunFolder); } catch { }
}

Info(
    "Impact analysis finished.\n\n" +
    "Tables: " + gDiscoveredTables + "   Columns: " + gDiscoveredColumns + "   Measures: " + gDiscoveredMeasures + "\n" +
    "Direct dependencies: " + gEdgeKinds.Count + "\n" +
    "Objects with no dependents in the model: " + gNoDependents + "\n" +
    "Warnings: " + gWarnings.Distinct().Count() + "   Unknown types: " + gUnknown.Distinct().Count() + "\n\n" +
    "Export: " + gWriteStatus + "\n" +
    (gWriteStatus == "SUCCESS" ? gRunFolder : "") + "\n\n" +
    "Note: report visuals, filters and RLS are not part of this analysis.");
