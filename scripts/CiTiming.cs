#:package YamlDotNet@18.1.0

// Records the timing and job graph of a GitHub Actions run, including a run that is still going.
//
// The jobs API reports each job's creation, start and finish time while the run is in progress,
// but not the dependency edges, so the edges are read from the workflow file at the run's commit.
// Three figures are reported, as ruled for CI timing:
//   - wait time: from the run's creation to the last job finishing (shown, no rule);
//   - queue time: from each job's creation to its start (recorded, no rule on pull requests);
//   - execution time: per job and along the longest path through the job graph (the only figure
//     a rule may judge).
// A run that is still going is measured up to the moment of reading, and every figure says so.
//
// Usage:
//   dotnet run scripts/CiTiming.cs -- --repo <owner/name> --run <id> [--attempt <n>]
//     [--exclude-job <name>] [--workflow-file <path>] [--summary <file>] [--payload <file>] [--save <dir>]
//     [--watch <seconds>]
//   dotnet run scripts/CiTiming.cs -- --input <dir> [--summary <file>] [--payload <file>]
//
// --input reads run.json, jobs.json and workflow.yml from a directory, as written by --save, so a
// run can be rendered offline. --watch re-reads the run at the given interval and prints a short
// table until the run completes. --workflow-file reads the job graph from a local file, which in CI
// is the checkout of the workflow that ran; otherwise it is read at the run's commit. GitHub is read
// through the gh CLI, which uses GH_TOKEN in CI.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

var options = ParseArguments(args);
if (options is null)
{
    Console.Error.WriteLine("Usage: CiTiming.cs --repo <owner/name> --run <id> [--attempt <n>] [--exclude-job <name>] [--workflow-file <path>] [--summary <file>] [--payload <file>] [--save <dir>] [--watch <seconds>] | --input <dir>");
    return 2;
}

while (true)
{
    Report report;
    try
    {
        var snapshot = options.Input is { } input ? ReadSaved(input) : Fetch(options);
        report = Analyse(snapshot, options.ExcludeJob);
    }
    catch (Exception e) when (e is not OutOfMemoryException)
    {
        Console.Error.WriteLine($"CI timing could not read the run: {e.Message}");
        return 1;
    }

    if (options.Watch is { } interval)
    {
        Console.WriteLine(RenderConsole(report));
        if (!report.Complete)
        {
            Thread.Sleep(TimeSpan.FromSeconds(interval));
            continue;
        }
    }

    // A watch has already printed its table, so the Markdown goes only to a requested summary file.
    var markdown = RenderMarkdown(report);
    if (options.Summary is { } summary)
    {
        File.AppendAllText(summary, markdown);
    }
    else if (options.Watch is null)
    {
        Console.Write(markdown);
    }

    if (options.Payload is { } payload)
    {
        File.WriteAllText(payload, RenderPayload(report).ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    break;
}

return 0;

static Options? ParseArguments(string[] args)
{
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
        {
            return null;
        }

        values[args[i][2..]] = args[++i];
    }

    int? watch = null;
    if (values.TryGetValue("watch", out var w))
    {
        if (!int.TryParse(w, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
        {
            return null;
        }

        watch = seconds;
    }

    var workflowFile = values.GetValueOrDefault("workflow-file");
    if (values.TryGetValue("input", out var inputDir))
    {
        return new Options(null, null, null, values.GetValueOrDefault("exclude-job"), workflowFile, values.GetValueOrDefault("summary"), values.GetValueOrDefault("payload"), null, null, inputDir);
    }

    if (!values.TryGetValue("repo", out var repo) || !values.TryGetValue("run", out var run) || !long.TryParse(run, CultureInfo.InvariantCulture, out _)
        || (values.TryGetValue("attempt", out var attempt) && !int.TryParse(attempt, CultureInfo.InvariantCulture, out _)))
    {
        return null;
    }

    return new Options(repo, run, values.GetValueOrDefault("attempt"), values.GetValueOrDefault("exclude-job"), workflowFile, values.GetValueOrDefault("summary"), values.GetValueOrDefault("payload"), values.GetValueOrDefault("save"), watch, null);
}

static RunSnapshot Fetch(Options options)
{
    var runPath = $"repos/{options.Repo}/actions/runs/{options.Run}" + (options.Attempt is { } a ? $"/attempts/{a}" : string.Empty);
    var runJson = Gh("api", runPath);
    var run = JsonNode.Parse(runJson)!;
    var attempt = run["run_attempt"]!.GetValue<int>();
    var jobsJson = Gh("api", "--paginate", $"repos/{options.Repo}/actions/runs/{options.Run}/attempts/{attempt}/jobs?per_page=100", "--jq", ".jobs[]");
    var jobs = new JsonArray(jobsJson.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)).ToArray());

    // A dynamic or reusable workflow may have no readable file; the run is then shown without edges.
    string workflow;
    try
    {
        workflow = options.WorkflowFile is { } local ? File.ReadAllText(local) : Gh("api", "-H", "Accept: application/vnd.github.raw", $"repos/{options.Repo}/contents/{run["path"]!.GetValue<string>()}?ref={run["head_sha"]!.GetValue<string>()}");
    }
    catch (Exception e) when (e is InvalidOperationException or IOException)
    {
        Console.Error.WriteLine($"CI timing could not read the workflow file: {e.Message}");
        workflow = string.Empty;
    }

    var now = DateTimeOffset.UtcNow;
    if (options.Save is { } save)
    {
        Directory.CreateDirectory(save);
        var savedRun = run.DeepClone();
        savedRun["read_at"] = now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(save, "run.json"), savedRun.ToJsonString());
        File.WriteAllText(Path.Combine(save, "jobs.json"), jobs.ToJsonString());
        File.WriteAllText(Path.Combine(save, "workflow.yml"), workflow);
    }

    return new RunSnapshot(run, jobs, workflow, now);
}

static RunSnapshot ReadSaved(string directory)
{
    var run = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "run.json")))!;
    var jobs = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "jobs.json")))!.AsArray();
    var workflow = File.ReadAllText(Path.Combine(directory, "workflow.yml"));
    var readAt = run["read_at"]?.GetValue<string>() is { } text ? Time(text)!.Value : DateTimeOffset.UtcNow;
    return new RunSnapshot(run, jobs, workflow, readAt);
}

static string Gh(params string[] arguments)
{
    var start = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }

    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start gh.");
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"gh {string.Join(' ', arguments)} failed: {error.Result.Trim()}");
    }

    return output.Result;
}

static List<WorkflowJob> ReadWorkflowJobs(string workflow)
{
    var result = new List<WorkflowJob>();
    if (string.IsNullOrWhiteSpace(workflow))
    {
        return result;
    }

    var stream = new YamlStream();
    try
    {
        stream.Load(new StringReader(workflow));
    }
    catch (YamlDotNet.Core.YamlException)
    {
        return result;
    }

    if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root
        || !root.Children.TryGetValue(new YamlScalarNode("jobs"), out var jobsNode) || jobsNode is not YamlMappingNode jobs)
    {
        return result;
    }

    foreach (var (keyNode, valueNode) in jobs.Children)
    {
        if (keyNode is not YamlScalarNode { Value: { } key } || valueNode is not YamlMappingNode job)
        {
            continue;
        }

        var name = job.Children.TryGetValue(new YamlScalarNode("name"), out var n) && n is YamlScalarNode { Value: { } text } ? text : key;
        var needs = new List<string>();
        if (job.Children.TryGetValue(new YamlScalarNode("needs"), out var needsNode))
        {
            if (needsNode is YamlScalarNode { Value: { } single })
            {
                needs.Add(single);
            }
            else if (needsNode is YamlSequenceNode sequence)
            {
                needs.AddRange(sequence.Children.OfType<YamlScalarNode>().Select(s => s.Value).OfType<string>());
            }
        }

        result.Add(new WorkflowJob(key, name, needs));
    }

    return result;
}

static Report Analyse(RunSnapshot snapshot, string? excludeJob)
{
    var run = snapshot.Run;
    // A re-run keeps the run's creation time, so the latest attempt is measured from its own start.
    var runCreated = Time(run["run_started_at"]?.GetValue<string>()) ?? Time(run["created_at"]?.GetValue<string>())!.Value;
    var runComplete = run["status"]?.GetValue<string>() == "completed";
    var runAttempt = run["run_attempt"]?.GetValue<int>() ?? 1;
    var workflowJobs = ReadWorkflowJobs(snapshot.Workflow);
    var nodes = workflowJobs.Select(j => new Node(j.Key, j.Name, j.Needs.Where(need => workflowJobs.Any(w => w.Key == need)).ToList())).ToList();

    // Match each job the API reports to a workflow job, most specific first: its exact name or key,
    // then the name a matrix leg (" (...)") or a called workflow (" / ...") adds to it, then a name
    // built from expressions, read as a pattern with each expression matching anything. A job that
    // matches nothing is shown on its own.
    foreach (var element in snapshot.Jobs)
    {
        var job = element!;
        var created = Time(job["created_at"]?.GetValue<string>());
        // A job that a partial re-run did not repeat is reported again with its earlier attempt
        // number and times. It is shown but not measured.
        var carried = (job["run_attempt"]?.GetValue<int>() ?? runAttempt) < runAttempt;
        var instance = new JobInstance(
            job["name"]?.GetValue<string>() ?? "(unnamed job)",
            job["status"]!.GetValue<string>(),
            job["conclusion"]?.GetValue<string>(),
            created,
            Time(job["started_at"]?.GetValue<string>()),
            Time(job["completed_at"]?.GetValue<string>()),
            job["steps"] is JsonArray steps && steps.Count > 0 && steps.All(s => s!["status"]?.GetValue<string>() == "completed"),
            !string.IsNullOrEmpty(job["runner_name"]?.GetValue<string>()),
            carried);
        var node = Match(nodes, instance.Name);
        if (node is null)
        {
            node = nodes.FirstOrDefault(n => n.Key == instance.Name);
            if (node is null)
            {
                node = new Node(instance.Name, instance.Name, []);
                nodes.Add(node);
            }
        }

        node.Instances.Add(instance);
    }

    var asOf = runComplete
        ? nodes.SelectMany(n => n.Instances).Where(i => !i.Carried).Select(i => i.Completed).Where(t => t is not null).DefaultIfEmpty(snapshot.ReadAt).Max()!.Value
        : snapshot.ReadAt;
    foreach (var node in nodes)
    {
        node.Excluded = excludeJob is not null && (node.Key == excludeJob || node.Name == excludeJob);
        node.DidNotRun = runComplete && node.Instances.Count == 0;
    }

    // Once the run is complete, a declared job with no reported job did not run, so it does not keep
    // the figures open.
    var measured = nodes.Where(n => !n.Excluded).ToList();
    var current = measured.SelectMany(n => n.Instances).Where(i => !i.Carried).ToList();
    var lastFinish = current.Select(i => i.Completed).Where(t => t is not null).Max();
    var allFinished = measured.All(n => n.DidNotRun || (n.Instances.Count > 0 && n.Instances.All(i => i.Status == "completed")));
    var wait = Positive((allFinished && lastFinish is not null ? lastFinish.Value : asOf) - runCreated);
    var firstJobCreated = current.Select(i => i.Created).Where(t => t is not null).Min();
    var dispatch = Positive(firstJobCreated is null ? asOf - runCreated : firstJobCreated.Value - runCreated);
    var queue = current.Sum(i => i.Queue(asOf).TotalSeconds);
    var path = LongestPath(measured, asOf);

    return new Report(
        run["name"]?.GetValue<string>() ?? "workflow",
        runAttempt,
        runComplete || allFinished,
        allFinished,
        asOf,
        wait,
        dispatch,
        TimeSpan.FromSeconds(queue),
        path,
        nodes,
        workflowJobs.Count > 0);
}

static Node? Match(List<Node> nodes, string reported)
{
    static bool Suffixed(string declared, string reported) =>
        reported.StartsWith(declared + " (", StringComparison.Ordinal) || reported.StartsWith(declared + " / ", StringComparison.Ordinal);

    return nodes.FirstOrDefault(n => n.Name == reported)
        ?? nodes.FirstOrDefault(n => n.Key == reported && !n.Name.Contains("${{", StringComparison.Ordinal))
        ?? nodes.FirstOrDefault(n => !n.Name.Contains("${{", StringComparison.Ordinal) && Suffixed(n.Name, reported))
        ?? nodes.Where(n => n.Name.Contains("${{", StringComparison.Ordinal) && NamePattern(n.Name).IsMatch(reported))
            .OrderByDescending(n => Regex.Replace(n.Name, @"\$\{\{.*?\}\}", string.Empty).Length)
            .FirstOrDefault();
}

static Regex NamePattern(string declared)
{
    var pattern = new StringBuilder("^");
    var rest = declared;
    while (rest.IndexOf("${{", StringComparison.Ordinal) is var open and >= 0)
    {
        var close = rest.IndexOf("}}", open, StringComparison.Ordinal);
        if (close < 0)
        {
            break;
        }

        pattern.Append(Regex.Escape(rest[..open])).Append(".*");
        rest = rest[(close + 2)..];
    }

    // A matrix leg or called workflow can add to an expression name too.
    pattern.Append(Regex.Escape(rest)).Append(@"(?:\s\(.*\)|\s/\s.*)?$");
    return new Regex(pattern.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}

static TimeSpan Positive(TimeSpan span) => span < TimeSpan.Zero ? TimeSpan.Zero : span;

// The longest chain of execution time through the job graph. A job's weight is the time at least
// one of its legs was running, so a matrix counts once and a leg waiting for a runner adds nothing. A job still running counts
// up to the moment of reading. A cycle, which GitHub would reject, leaves no path.
static PathResult LongestPath(List<Node> nodes, DateTimeOffset asOf)
{
    var byKey = nodes.ToDictionary(n => n.Key, StringComparer.Ordinal);
    var best = new Dictionary<string, (TimeSpan Total, string? Previous)>(StringComparer.Ordinal);
    var visiting = new HashSet<string>(StringComparer.Ordinal);

    (TimeSpan Total, string? Previous)? Visit(Node node)
    {
        if (best.TryGetValue(node.Key, out var known))
        {
            return known;
        }

        if (!visiting.Add(node.Key))
        {
            return null;
        }

        (TimeSpan Total, string? Previous) result = (TimeSpan.Zero, null);
        foreach (var need in node.Needs)
        {
            if (!byKey.TryGetValue(need, out var parent))
            {
                continue;
            }

            var parentResult = Visit(parent);
            if (parentResult is null)
            {
                return null;
            }

            if (parentResult.Value.Total > result.Total)
            {
                result = (parentResult.Value.Total, need);
            }
        }

        visiting.Remove(node.Key);
        result = (result.Total + node.Execution(asOf), result.Previous);
        best[node.Key] = result;
        return result;
    }

    foreach (var node in nodes)
    {
        if (Visit(node) is null)
        {
            return new PathResult(TimeSpan.Zero, []);
        }
    }

    if (best.Count == 0)
    {
        return new PathResult(TimeSpan.Zero, []);
    }

    var end = best.OrderByDescending(p => p.Value.Total).ThenByDescending(p => byKey[p.Key].Execution(asOf) > TimeSpan.Zero).ThenBy(p => nodes.FindIndex(n => n.Key == p.Key)).First();
    var chain = new List<string>();
    for (string? key = end.Key; key is not null; key = best[key].Previous)
    {
        chain.Add(byKey[key].Name);
    }

    chain.Reverse();
    return new PathResult(end.Value.Total, chain);
}

static string RenderMarkdown(Report report)
{
    var b = new StringBuilder();
    b.Append("## CI timing\n\n");
    b.Append(report.JobsFinished
        ? $"Run `{Escape(report.Name)}`, attempt {report.Attempt}. Every job has finished.\n\n"
        : $"Run `{Escape(report.Name)}`, attempt {report.Attempt}, read at {report.AsOf:HH:mm:ss} UTC while it was still going. Figures run up to that moment, and a job still running is counted so far.\n\n");
    if (report.Attempt > 1)
    {
        b.Append(CultureInfo.InvariantCulture, $"This is attempt {report.Attempt}. Only it is measured; the {report.Attempt - 1} earlier {(report.Attempt == 2 ? "attempt is" : "attempts are")} not.\n\n");
    }

    b.Append("| Figure | Time | Rule |\n| --- | --- | --- |\n");
    b.Append(CultureInfo.InvariantCulture, $"| Wait (run created to last job finished{(report.JobsFinished ? string.Empty : ", so far")}) | {Duration(report.Wait)} | none |\n");
    var soFar = report.JobsFinished ? string.Empty : ", so far";
    b.Append(CultureInfo.InvariantCulture, $"| Queue (sum of job waits for a runner{soFar}) | {Duration(report.Queue)} | none on pull requests |\n");
    b.Append(CultureInfo.InvariantCulture, $"| Execution, longest path{soFar} | {Duration(report.Path.Total)} | may be judged |\n");
    b.Append(CultureInfo.InvariantCulture, $"| Dispatch (run created to first job) | {Duration(report.Dispatch)} | none |\n\n");
    if (report.Path.Jobs.Count > 0)
    {
        b.Append("Longest path: ").Append(string.Join(" → ", report.Path.Jobs.Select(j => $"`{Escape(j)}`"))).Append("\n\n");
    }

    if (!report.HasWorkflow)
    {
        b.Append("The workflow file could not be read, so the jobs are shown without their dependencies.\n\n");
    }

    b.Append("```mermaid\nflowchart LR\n");
    var ids = report.Nodes.Select((n, i) => (n, Id: $"j{i}")).ToDictionary(p => p.n.Key, p => p.Id, StringComparer.Ordinal);
    foreach (var node in report.Nodes)
    {
        var label = node.DidNotRun ? $"{node.Name}<br/>did not run"
            : node.State == "skipped" ? $"{node.Name}<br/>skipped"
            : node.Instances.Count == 0 ? $"{node.Name}<br/>not started"
            : $"{node.Name}<br/>{Duration(node.Execution(report.AsOf))}";
        b.Append("  ").Append(ids[node.Key]).Append("[\"").Append(MermaidText(label)).Append("\"]:::").Append(node.State).Append('\n');
    }

    foreach (var node in report.Nodes)
    {
        foreach (var need in node.Needs)
        {
            b.Append("  ").Append(ids[need]).Append(" --> ").Append(ids[node.Key]).Append('\n');
        }
    }

    b.Append("  classDef success fill:#dafbe1,stroke:#1a7f37\n");
    b.Append("  classDef failure fill:#ffebe9,stroke:#cf222e\n");
    b.Append("  classDef running fill:#fff8c5,stroke:#9a6700\n");
    b.Append("  classDef waiting fill:#f6f8fa,stroke:#8c959f,stroke-dasharray:4\n");
    b.Append("  classDef skipped fill:#f6f8fa,stroke:#d0d7de,color:#8c959f\n");
    b.Append("```\n\n");

    b.Append("| Job | State | Queue | Execution |\n| --- | --- | --- | --- |\n");
    foreach (var node in report.Nodes)
    {
        foreach (var instance in node.Instances.OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            var state = instance.Status == "completed" ? instance.Conclusion ?? "completed" : instance.Status;
            if (instance.Status == "in_progress" && instance.StepsDone)
            {
                state += " (every step has finished)";
            }

            var note = node.Excluded ? " (this job; not measured)" : instance.Carried ? " (earlier attempt; not measured)" : string.Empty;
            b.Append(CultureInfo.InvariantCulture, $"| `{Escape(instance.Name)}`{note} | {state} | {Duration(instance.Queue(report.AsOf))} | {Duration(instance.Execution(report.AsOf))} |\n");
        }

        if (node.Instances.Count == 0)
        {
            b.Append(CultureInfo.InvariantCulture, $"| `{Escape(node.Name)}` | {(node.DidNotRun ? "did not run" : "not started")} | | |\n");
        }
    }

    b.Append('\n');
    return b.ToString();
}

static string RenderConsole(Report report)
{
    var b = new StringBuilder();
    b.Append(CultureInfo.InvariantCulture, $"[{report.AsOf:HH:mm:ss}] {report.Name} attempt {report.Attempt}: wait {Duration(report.Wait)}, queue {Duration(report.Queue)}, longest path {Duration(report.Path.Total)}, dispatch {Duration(report.Dispatch)}{(report.Complete ? ", complete" : string.Empty)}\n");
    foreach (var node in report.Nodes)
    {
        var state = node.DidNotRun ? "did not run" : node.Instances.Count == 0 ? "not started" : node.State;
        var flag = node.Instances.Any(i => i.Status == "in_progress" && i.StepsDone) ? "  every step finished but the job is still in progress" : string.Empty;
        b.Append(CultureInfo.InvariantCulture, $"  {node.Name,-28} {state,-10} queue {Duration(node.Instances.Select(i => i.Queue(report.AsOf)).DefaultIfEmpty().Max()),8}  execution {Duration(node.Execution(report.AsOf)),8}{flag}\n");
    }

    return b.ToString().TrimEnd('\n');
}

// The figures as a numeric payload in the shape `diffra collect` reads. Only finished jobs are
// included, and the run's totals only once every job has finished, so no figure is partial.
static JsonObject RenderPayload(Report report)
{
    var jobs = new JsonObject();
    foreach (var node in report.Nodes.Where(n => !n.Excluded && n.State != "skipped" && n.Instances.Count > 0 && n.Instances.All(i => i.Completed is not null)))
    {
        jobs[node.Key] = new JsonObject
        {
            ["queueSeconds"] = (long)node.Instances.Where(i => !i.Carried).Sum(i => i.Queue(report.AsOf).TotalSeconds),
            ["executionSeconds"] = (long)node.Execution(report.AsOf).TotalSeconds,
        };
    }

    var metrics = new JsonObject
    {
        ["dispatchSeconds"] = (long)report.Dispatch.TotalSeconds,
        ["jobs"] = jobs,
    };
    if (report.JobsFinished)
    {
        metrics["waitSeconds"] = (long)report.Wait.TotalSeconds;
        metrics["queueSeconds"] = (long)report.Queue.TotalSeconds;
        metrics["longestPathSeconds"] = (long)report.Path.Total.TotalSeconds;
    }

    return new JsonObject
    {
        ["scope"] = "ci-run",
        ["methodology"] = report.JobsFinished ? "github-actions-jobs-api" : "github-actions-jobs-api-partial",
        ["metrics"] = metrics,
    };
}

static DateTimeOffset? Time(string? text) =>
    string.IsNullOrEmpty(text) ? null : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

static string Duration(TimeSpan span)
{
    if (span <= TimeSpan.Zero)
    {
        return "0s";
    }

    var seconds = (long)span.TotalSeconds;
    return seconds >= 3600 ? $"{seconds / 3600}h{seconds % 3600 / 60:00}m"
        : seconds >= 60 ? $"{seconds / 60}m{seconds % 60:00}s"
        : $"{seconds}s";
}

static string Escape(string text) =>
    text.Replace('`', '\'').Replace("|", "\\|", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');

static string MermaidText(string text) =>
    text.Replace("\"", "#quot;", StringComparison.Ordinal).Replace('`', '\'').Replace('\r', ' ').Replace('\n', ' ');

sealed record Options(string? Repo, string? Run, string? Attempt, string? ExcludeJob, string? WorkflowFile, string? Summary, string? Payload, string? Save, int? Watch, string? Input);

sealed record RunSnapshot(JsonNode Run, JsonArray Jobs, string Workflow, DateTimeOffset ReadAt);

sealed record WorkflowJob(string Key, string Name, List<string> Needs);

sealed record JobInstance(
    string Name, string Status, string? Conclusion, DateTimeOffset? Created, DateTimeOffset? Started,
    DateTimeOffset? Completed, bool StepsDone, bool HadRunner, bool Carried)
{
    // GitHub gives a start time to a job that is still queued, and to one cancelled before a runner
    // took it, so a job counts as running only once a runner has it. Spans never go below zero:
    // GitHub can report a finish a moment before the start.
    public bool Ran => !Carried && Conclusion != "skipped" && Started is not null
        && (Status == "in_progress" || (Status == "completed" && HadRunner));

    public TimeSpan Queue(DateTimeOffset asOf)
    {
        if (Carried || Conclusion == "skipped" || Created is null)
        {
            return TimeSpan.Zero;
        }

        var end = Ran ? Started!.Value : Completed ?? asOf;
        return end < Created.Value ? TimeSpan.Zero : end - Created.Value;
    }

    public (DateTimeOffset Start, DateTimeOffset End)? Running(DateTimeOffset asOf)
    {
        if (!Ran)
        {
            return null;
        }

        var end = Completed ?? asOf;
        return (Started!.Value, end < Started.Value ? Started.Value : end);
    }

    public TimeSpan Execution(DateTimeOffset asOf) => Running(asOf) is { } span ? span.End - span.Start : TimeSpan.Zero;
}

sealed class Node(string key, string name, List<string> needs)
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public List<string> Needs { get; } = needs;
    public List<JobInstance> Instances { get; } = [];
    public bool Excluded { get; set; }
    public bool DidNotRun { get; set; }

    // The union of the legs' running time.
    public TimeSpan Execution(DateTimeOffset asOf)
    {
        var total = TimeSpan.Zero;
        DateTimeOffset? reached = null;
        foreach (var (start, end) in Instances.Select(i => i.Running(asOf)).OfType<(DateTimeOffset Start, DateTimeOffset End)>().OrderBy(s => s.Start))
        {
            var from = reached is { } r && r > start ? r : start;
            if (end > from)
            {
                total += end - from;
                reached = end;
            }
        }

        return total;
    }

    public string State =>
        Instances.Count == 0 ? DidNotRun ? "skipped" : "waiting"
        : Instances.Where(i => !i.Carried).Any(i => i.Conclusion is "failure" or "cancelled" or "timed_out" or "startup_failure") ? "failure"
        : Instances.Any(i => i.Status == "in_progress") ? "running"
        : Instances.Any(i => i.Status != "completed") ? "waiting"
        : Instances.All(i => i.Conclusion == "skipped" || i.Carried) ? "skipped"
        : "success";
}

sealed record PathResult(TimeSpan Total, List<string> Jobs);

sealed record Report(
    string Name,
    int Attempt,
    bool Complete,
    bool JobsFinished,
    DateTimeOffset AsOf,
    TimeSpan Wait,
    TimeSpan Dispatch,
    TimeSpan Queue,
    PathResult Path,
    List<Node> Nodes,
    bool HasWorkflow);
