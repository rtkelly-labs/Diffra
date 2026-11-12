using Xunit;

namespace Diffra.Protocol.Tests;

// One fact per hand-rolled suite. All facts share one class, so xUnit runs them
// sequentially, matching the order-dependent behaviour of tests/Protocol.Tests/Program.cs.
public sealed class SuiteFacts
{
    [Fact] public void Comparison() => ComparisonTests.RunAll();
    [Fact] public void Reporting() => ReportingTests.RunAll();
    [Fact] public void Attention() => AttentionTests.RunAll();
    [Fact] public void Collect() => CollectTests.RunAll();
    [Fact] public void DiffEval() => DiffEvalTests.RunAll();
    [Fact] public void SampleFixture() => SampleFixtureTests.RunAll();
    [Fact] public void CasAndBundle() => CasAndBundleTests.RunAll();
    [Fact] public void Presentation() => PresentationTests.RunAll();
    [Fact] public void Coverage() => CoverageTests.RunAll();
    [Fact] public void TimeSeriesGraph() => TimeSeriesGraphTests.RunAll();
    [Fact] public void CloudEvents() => CloudEventsTests.RunAll();
    [Fact] public void Graph() => GraphTests.RunAll();
    [Fact] public void RootFile() => RootFileTests.RunAll();
    [Fact] public void RecordEntry() => RecordEntryTests.RunAll();
    [Fact] public void ExternalCommandAdapter() => ExternalCommandAdapterTests.RunAll();
    [Fact] public void PackageReferences() => PackageReferenceTests.RunAll();
    [Fact] public void BaselineTrust() => BaselineTrustTests.RunAll();
    [Fact] public void DogfoodPipeline() => DogfoodPipelineTests.RunAll();
}
